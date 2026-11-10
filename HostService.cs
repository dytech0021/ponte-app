using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ponte;

/// <summary>Id e CanRemember só vêm preenchidos quando quem pede provou ter a própria chave, o que permite autorizá-lo de vez.</summary>
internal sealed record IncomingRequest(string Name, string User, string Remote, string Id, bool CanRemember);

/// <summary>Resposta de quem está na frente do computador a um pedido de conexão.</summary>
internal enum Permission { Refuse, Once, Always }

/// <summary>Uma sessão em que este computador está sendo controlado.</summary>
internal sealed class HostSession(Channel channel, HelloMsg hello, string remote, string receiveDir, bool shareSound, AppConfig config)
{
    private readonly CancellationTokenSource _stop = new();
    private readonly PointerWatcher _pointer = new();
    private readonly bool _sendPointer = hello.Caps is { } wanted && (wanted & Caps.Cursor) != 0;
    // O som sai por uma fila curta: se a rede não der conta, perde-se o trecho mais antigo em vez de acumular atraso.
    private readonly System.Threading.Channels.Channel<byte[]> _sound = System.Threading.Channels.Channel.CreateBounded<byte[]>(
        new System.Threading.Channels.BoundedChannelOptions(50) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest, SingleReader = true });
    private AudioCapture? _capture;
    private int _screen = Math.Max(0, Array.FindIndex(Screen.AllScreens, s => s.Primary));
    private int _quality = 60;
    private int _forceFull = 1, _forcePointer = 1;
    private readonly bool _sendVideo = (hello.Caps.GetValueOrDefault() & Caps.Video) != 0;
    // 1.17.1: o celular de quem acessa abre o teclado quando o foco entra num campo de texto daqui.
    private readonly bool _sendTextFocus = (hello.Caps.GetValueOrDefault() & Caps.TextFocus) != 0;
    private readonly object _videoGate = new();
    private VideoOptions _video = new();
    private CancellationTokenSource? _activeVideo;
    private int _videoRevision, _streamId, _preparedRevision = -1;
    private string? _preparedEncoder, _viewerProblem;
    // Por que a sessão terminou: vale o primeiro motivo, o resto é consequência dele.
    private string? _end;
    private volatile bool _byeReceived, _stoppedHere;
    // O uso de quem acessa: quando chegou a última entrada dele, o tempo sem uso que ele pediu (-1 enquanto não
    // pediu) e quantos arquivos daqui estão indo para lá (enviando, a sessão não está parada).
    private long _lastInput = Stopwatch.GetTimestamp();
    private int _viewerIdle = -1, _sendingFiles;

    /// <summary>Quantos segundos tem cada minuto de <see cref="AppConfig.HostIdleMinutes"/>; o autoteste encurta.</summary>
    internal static int SecondsPerMinute = 60;
    private const int MaxIdleSeconds = 7 * 24 * 3600;

    public string PeerName { get; } = hello.Name;
    public string PeerUser { get; } = hello.User;
    public string Remote { get; } = remote;
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>Como a pessoa entrou: senha, aceite na tela ou autorização salva.</summary>
    public string Access { get; init; } = "";
    public string ViewerKeyHash { get; init; } = "";
    /// <summary>O outro lado grava os arquivos que chegam. O aplicativo do celular não grava, e até a 1.0.0 não avisava isso.</summary>
    public bool PeerReceivesFiles { get; } = hello.Caps is { } caps ? (caps & Caps.Files) != 0 : hello.User != "Android";
    /// <summary>O visualizador pediu o som e este computador o está transmitindo.</summary>
    public bool SoundOn => _capture is not null;
    /// <summary>Por que o som não está saindo; vazio quando está tudo certo ou ele não foi pedido.</summary>
    public string SoundProblem => _capture?.Problem ?? "";

    public event Action<string>? FileReceived;
    public event Action<string>? ClipboardReceived;

    public void Stop(bool reconnect = false)
    {
        if (!reconnect && !_stoppedHere)
        {
            _stoppedHere = true; config.BlockResume(ViewerKeyHash);
            // Finish the currently framed message before Bye. Cancelling a frame first can truncate it,
            // making the viewer mistake a deliberate local stop for an unexpected network failure.
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { channel.SendAsync(Msg.Bye, Encoding.UTF8.GetBytes("Sessão encerrada no computador remoto."), brief.Token).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        _stop.Cancel();
    }

    /// <summary>Por que a sessão terminou, para o registro de sessões.</summary>
    public string EndReason => _end ?? "motivo não identificado";

    /// <summary>A sessão foi encerrada aqui porque quem acessava ficou o tempo combinado sem mexer.</summary>
    public bool EndedIdle { get; private set; }

    /// <summary>Segundos sem uso até a sessão ser encerrada (0 é nunca): o tempo de quem acessa, se ele mandou, ou o daqui.</summary>
    public int IdleLimit
    {
        get
        {
            int wanted = Volatile.Read(ref _viewerIdle);
            return wanted >= 0 ? wanted : Math.Min(Math.Max(0, config.HostIdleMinutes) * SecondsPerMinute, MaxIdleSeconds);
        }
    }

    private void Touch() => Volatile.Write(ref _lastInput, Stopwatch.GetTimestamp());

    /// <summary>Como a imagem estava indo e quanto foi enviado, para o registro de sessões.</summary>
    public string Summary
    {
        get
        {
            VideoOptions video;
            lock (_videoGate) video = _video;
            return $"{VideoOptions.CodecName(video.Codec)} {video.Fps} FPS, qualidade {_quality}, enviados {SessionLog.Megabytes(Interlocked.Read(ref channel.BytesSent))}";
        }
    }

    private void Note(string reason)
    {
        if (reason.Length > 0) Interlocked.CompareExchange(ref _end, reason, null);
    }

    public Task SendClipboardAsync(string text) => channel.SendAsync(Msg.Clipboard, Encoding.UTF8.GetBytes(text), _stop.Token);

    public async Task SendFileAsync(string path, IProgress<int>? progress)
    {
        Interlocked.Increment(ref _sendingFiles);
        try { await FileSender.SendAsync(channel, path, progress, _stop.Token); }
        finally
        {
            Interlocked.Decrement(ref _sendingFiles);
            Touch();
        }
    }

    public async Task RunAsync(CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _stop.Token);
        var ct = linked.Token;
        Codecs.Ready();
        var capture = Task.Run(async () =>
        {
            try { await CaptureLoopAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Quando quem acessa se despede e fecha, o envio da imagem costuma falhar antes de a despedida ser lida,
                // e o registro ficava com "o envio da imagem falhou" no lugar do motivo de verdade. A leitura tem um
                // instante para dizer primeiro por que acabou (despedida, conexão caída); só depois vale este motivo.
                for (int i = 0; i < 20 && _end is null && !_byeReceived && !ct.IsCancellationRequested; i++) await Task.Delay(25, CancellationToken.None);
                if (_byeReceived) Note("o outro computador encerrou");
                Note("o envio da imagem falhou: " + SessionLog.Describe(ex));
                throw;
            }
            finally { _stop.Cancel(); }
        }, CancellationToken.None);
        var pointer = Task.Run(() => PointerLoopAsync(ct), CancellationToken.None);
        var sound = Task.Run(() => SoundLoopAsync(ct), CancellationToken.None);
        var idle = Task.Run(() => IdleLoopAsync(ct), CancellationToken.None);
        var textFocus = TextFocusLoopAsync(ct);
        try
        {
            await ReadLoopAsync(ct);
            Note(_byeReceived ? "o outro computador encerrou" : "a conexão foi fechada sem aviso (pelo outro lado ou no caminho)");
        }
        catch (OperationCanceledException)
        {
            Note(_stoppedHere ? "encerrada neste computador" : outer.IsCancellationRequested ? "o Ponte deste computador foi fechado" : "");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            Note("a conexão caiu: " + SessionLog.Describe(ex));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            Note("erro do Ponte: " + ex.GetType().Name);
        }
        finally
        {
            if (_stoppedHere)
            {
                using var goodbye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Quiet(channel.SendAsync(Msg.Bye, Encoding.UTF8.GetBytes("Sessão encerrada no computador remoto."), goodbye.Token));
            }
            _stop.Cancel();
            // Quem se despediu foi este lado (encerrada aqui, ou por falta de uso): o que ainda chega é lido e descartado
            // até o outro fechar. Fechar com mouse e teclas por ler faria o Windows derrubar a conexão, e a despedida, com
            // o motivo, podia não chegar: lá apareceria "a conexão caiu" e uma tentativa de reconectar.
            if (_stoppedHere || EndedIdle) await DrainAsync(TimeSpan.FromSeconds(1));
            SetSound(false);
            // A consulta ao campo de texto depende de outros programas e pode demorar com um deles travado: o fim da
            // sessão não espera por ela além de um instante (senão este computador ficava "com sessão em andamento").
            var textFocusOrTimeout = Task.WhenAny(textFocus, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            foreach (var task in new[] { capture, sound, pointer, idle, textFocusOrTimeout })
            {
                try { await task; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                catch (Exception ex) { ErrorLog.Write(ex); }
            }
            InputInjector.ReleaseAll();
        }
    }

    private async Task DrainAsync(TimeSpan limit)
    {
        using var brief = new CancellationTokenSource(limit);
        try { while (await channel.ReadAsync(brief.Token) is not null) { } }
        // Uma leitura interrompida no meio pode deixar o canal sem condição de ler de novo: aí fica como era antes.
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or ObjectDisposedException
                                       or InvalidOperationException or NotSupportedException or CryptographicException) { }
    }

    // Só o laço de leitura liga e desliga a captura, então não há disputa por _capture.
    private void SetSound(bool wanted)
    {
        wanted &= shareSound && !_stop.IsCancellationRequested && !WindowsHost.Agent;
        if (wanted == (_capture is not null)) return;
        if (wanted)
        {
            _capture = new AudioCapture(payload => _sound.Writer.TryWrite(payload)) { Lossless = _quality >= 80 };
        }
        else
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    private async Task SoundLoopAsync(CancellationToken ct)
    {
        await foreach (byte[] payload in _sound.Reader.ReadAllAsync(ct))
            await channel.SendAsync(Msg.Audio, payload, ct);
    }

    private static Rectangle ScreenBounds(int index)
    {
        // Todas as telas (1.16.0): a área de trabalho inteira, que quem acessa recorta em uma janela por tela.
        if (index == ScreenInfo.All) return SystemInformation.VirtualScreen;
        var screens = Screen.AllScreens;
        return screens[Math.Clamp(index, 0, screens.Length - 1)].Bounds;
    }

    // A tela segue em JPEG (ou PNG, no 4:4:4) enquanto o H.264/H.265 não está pronto, se ele não for possível
    // aqui ou se parar no meio: o vídeo nunca deixa a sessão sem imagem.
    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        using var capturer = new ScreenCapturer();
        int applied = -1;
        string? retryEncoder = null;
        long retryAt = 0;
        while (!ct.IsCancellationRequested)
        {
            VideoOptions options;
            int revision;
            lock (_videoGate) { options = _video; revision = _videoRevision; }
            if (applied != revision)
            {
                applied = revision;
                retryEncoder = null;
                _forceFull = 1;
                if (_sendVideo)
                {
                    if (options.Codec == "jpeg") await SendVideoStatusAsync(CompatibleStatus(options), ct);
                    else _ = PrepareVideoAsync(options, revision, ct);
                }
            }
            string? encoder = TakePreparedEncoder(revision);
            if (encoder is null && retryEncoder is not null && Stopwatch.GetTimestamp() >= retryAt)
            {
                encoder = retryEncoder;
                retryEncoder = null;
            }
            if (encoder is not null)
            {
                var (problem, retry) = await StreamVideoAsync(options, revision, encoder, ct);
                if (revision != Volatile.Read(ref _videoRevision)) continue;
                _forceFull = 1;
                if (retry)
                {
                    // Vinha funcionando: tela bloqueada, aviso do UAC ou troca de resolução. Tenta de novo daqui a pouco.
                    retryEncoder = encoder;
                    retryAt = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
                    await SendVideoStatusAsync(CompatibleStatus(options, problem + " Tentando de novo..."), ct);
                }
                else
                {
                    await SendVideoStatusAsync(CompatibleStatus(options, problem), ct);
                }
                continue;
            }

            long start = Stopwatch.GetTimestamp();
            byte[]? frame = null;
            try
            {
                frame = capturer.Capture(ScreenBounds(_screen), _quality, Interlocked.Exchange(ref _forceFull, 0) == 1, options.Chroma444, options.MaxHeight);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                // Tela bloqueada ou prompt do UAC: a captura falha até a área de trabalho voltar.
                _forceFull = 1;
                await Task.Delay(500, ct);
                continue;
            }
            if (frame is not null) await channel.SendAsync(Msg.Frame, frame, ct);
            int elapsed = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await Task.Delay(Math.Max(1, 1000 / options.Fps - elapsed), ct);
        }
    }

    private static string CompatibleStatus(VideoOptions o, string problem = "")
    {
        string status = $"{(o.Chroma444 ? "PNG sem perdas (4:4:4)" : "JPEG")} · {o.Fps} FPS alvo";
        return problem.Length > 0 ? $"{status} · {VideoOptions.CodecName(o.Codec)} indisponível: {problem}" : status;
    }

    private string? TakePreparedEncoder(int revision)
    {
        lock (_videoGate)
        {
            if (_preparedRevision != revision || _preparedEncoder is null) return null;
            string encoder = _preparedEncoder;
            _preparedEncoder = null;
            return encoder;
        }
    }

    // Em paralelo à imagem compatível: baixa o FFmpeg na primeira vez e descobre qual placa codifica o pedido.
    private async Task PrepareVideoAsync(VideoOptions options, int revision, CancellationToken ct)
    {
        bool Current() => Volatile.Read(ref _videoRevision) == revision;
        string name = VideoOptions.CodecName(options.Codec);
        try
        {
            if (!VideoRuntime.Present)
            {
                await SendVideoStatusAsync($"Baixando o componente de vídeo para o {name} (só na primeira vez)...", ct);
                long last = 0;
                await VideoRuntime.EnsureAvailableAsync(config, percent =>
                {
                    if (!Current() || Stopwatch.GetElapsedTime(last).TotalMilliseconds < 700) return;
                    last = Stopwatch.GetTimestamp();
                    _ = Quiet(SendVideoStatusAsync($"Baixando o componente de vídeo para o {name}: {percent}%", ct));
                }, ct);
            }
            if (!Current()) return;
            await SendVideoStatusAsync($"Procurando a placa de vídeo que codifica {name}{(options.Chroma444 ? " 4:4:4" : "")}...", ct);
            string? encoder = await VideoEncoder.FindAsync(options, ct);
            if (!Current()) return;
            if (encoder is null)
            {
                await SendVideoStatusAsync(CompatibleStatus(options, options.Chroma444
                    ? $"a placa de vídeo deste computador não codifica {name} 4:4:4 (só placas NVIDIA, ou Intel no H.265)."
                    : $"nenhuma placa de vídeo deste computador codifica {name}."), ct);
                return;
            }
            lock (_videoGate)
            {
                if (revision != _videoRevision) return;
                _preparedRevision = revision;
                _preparedEncoder = encoder;
            }
        }
        catch (Exception ex) when (ex is PonteException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (Current()) await Quiet(SendVideoStatusAsync(CompatibleStatus(options, ex.Message), ct));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Transmite até a troca de opções ou uma falha. Devolve o motivo da falha e se vale tentar de novo.</summary>
    private async Task<(string Problem, bool Retry)> StreamVideoAsync(VideoOptions options, int revision, string encoder, CancellationToken ct)
    {
        using var changing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_videoGate)
        {
            if (revision != _videoRevision) return ("", false);
            _activeVideo = changing;
            _viewerProblem = null;
        }
        var screens = Screen.AllScreens;
        var sources = _screen == ScreenInfo.All ? VideoSource.AllScreens() : VideoSource.For(screens[Math.Clamp(_screen, 0, screens.Length - 1)]);
        string problem = "";
        long started = 0;
        try
        {
            foreach (var source in sources)
            {
                int id = 0;
                try
                {
                    await VideoEncoder.RunAsync(source, options, encoder, async frame =>
                    {
                        if (id == 0)
                        {
                            id = Interlocked.Increment(ref _streamId);
                            started = Stopwatch.GetTimestamp();
                            var output = options.Size(source.Bounds.Width, source.Bounds.Height);
                            await channel.SendJsonAsync(Msg.VideoStart, new VideoStartMsg(id, output.Width, output.Height,
                                options.Codec, encoder, options.Fps, options.Chroma444, options.UltraLowLatency, source.Name), ct);
                        }
                        byte[] packet = new byte[4 + frame.Length];
                        BitConverter.TryWriteBytes(packet, id);
                        frame.CopyTo(packet.AsSpan(4));
                        // A troca de opções espera esta mensagem terminar: cancelar no meio estragaria o fluxo cifrado.
                        await channel.SendAsync(Msg.VideoData, packet, ct);
                    }, changing.Token);
                }
                catch (IOException ex) when (id == 0 && !changing.IsCancellationRequested)
                {
                    // Esta captura nem começou (o DXGI pode não estar disponível): passa para a próxima.
                    problem = ex.Message;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Troca de opções (motivo vazio) ou o outro lado que não conseguiu decodificar.
            lock (_videoGate) return (_viewerProblem ?? "", false);
        }
        catch (IOException ex) { problem = ex.Message; }
        finally { lock (_videoGate) { if (_activeVideo == changing) _activeVideo = null; } }
        return (problem, problem.Length > 0 && started != 0 && Stopwatch.GetElapsedTime(started).TotalSeconds >= 10);
    }

    // Cancela o vídeo em andamento (chamado com _videoGate travado). Uma falha de quem reage ao cancelamento não pode
    // chegar ao laço de leitura: ela encerraria a sessão.
    private void CancelActiveVideo()
    {
        try { _activeVideo?.Cancel(); }
        catch (AggregateException ex) { ErrorLog.Write(ex); }
    }

    private Task SendVideoStatusAsync(string status, CancellationToken ct) => channel.SendAsync(Msg.VideoStatus, Encoding.UTF8.GetBytes(status), ct);

    private static async Task Quiet(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task PointerLoopAsync(CancellationToken ct)
    {
        if (!_sendPointer) return;
        while (!ct.IsCancellationRequested)
        {
            if (_pointer.Poll(Interlocked.Exchange(ref _forcePointer, 0) == 1) is { } pointer)
                await channel.SendAsync(Msg.Cursor, pointer, ct);
            if (_pointer.PollPosition(ScreenBounds(_screen)) is { } position)
                await channel.SendAsync(Msg.CursorPos, position, ct);
            await Task.Delay(20, ct);
        }
    }

    // Olha o foco do teclado a cada 300 ms numa linha própria (a Automação do Windows pode demorar com um programa
    // travado) e avisa só quando muda.
    private Task TextFocusLoopAsync(CancellationToken ct)
    {
        if (!_sendTextFocus) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                bool? last = null;
                while (!ct.IsCancellationRequested)
                {
                    bool now = TextFocus.Focused();
                    if (now != last)
                    {
                        last = now;
                        channel.SendAsync(Msg.TextFocus, new byte[] { (byte)(now ? 1 : 0) }, ct).GetAwaiter().GetResult();
                    }
                    ct.WaitHandle.WaitOne(300);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            catch (Exception ex) { ErrorLog.Write(ex); }
            finally { done.TrySetResult(); }
        }) { IsBackground = true, Name = "ponte-campo-de-texto" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task;
    }

    // Encerra a sessão depois do tempo sem uso, avisando antes: um minuto antes, ou na metade de um tempo mais curto.
    private async Task IdleLoopAsync(CancellationToken ct)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        bool warned = false;
        while (await tick.WaitForNextTickAsync(ct))
        {
            if (Volatile.Read(ref _sendingFiles) > 0) Touch();
            int limit = IdleLimit;
            double idle = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastInput)).TotalSeconds;
            if (limit > 0 && idle >= limit)
            {
                if (Interlocked.CompareExchange(ref _end, $"encerrada por falta de uso ({Span(limit)} sem mexer)", null) is null) EndedIdle = true;
                config.BlockResume(ViewerKeyHash);
                // O motivo vai na despedida: o visualizador 1.13.0 em diante o mostra; um anterior só vê a sessão terminar.
                using var brief = CancellationTokenSource.CreateLinkedTokenSource(ct);
                brief.CancelAfter(TimeSpan.FromSeconds(2));
                await Quiet(channel.SendAsync(Msg.Bye, Encoding.UTF8.GetBytes(
                    $"Sessão encerrada por falta de uso: {Span(limit)} sem mexer no mouse, no teclado ou na tela."), brief.Token));
                _stop.Cancel();
                return;
            }
            bool warning = limit > 0 && idle >= limit - Math.Min(60, limit / 2.0);
            if (warning == warned) continue;
            warned = warning;
            await Quiet(channel.SendAsync(Msg.Notice, Encoding.UTF8.GetBytes(warning
                ? $"A sessão vai ser encerrada em {Span(Math.Max(1, (int)Math.Round(limit - idle)))} por falta de uso. " +
                  "Para continuar, mexa o mouse, use o teclado ou toque na tela."
                : "Pronto: a sessão continua."), ct));
        }
    }

    /// <summary>"40 segundos", "1 minuto", "10 minutos", "1 hora", "1 h 30 min".</summary>
    internal static string Span(int seconds)
    {
        if (seconds < 60) return seconds == 1 ? "1 segundo" : $"{seconds} segundos";
        int minutes = (int)Math.Round(seconds / 60.0);
        if (minutes < 60) return minutes == 1 ? "1 minuto" : $"{minutes} minutos";
        int hours = minutes / 60, rest = minutes % 60;
        return rest > 0 ? $"{hours} h {rest} min" : hours == 1 ? "1 hora" : $"{hours} horas";
    }

    public const string BlockedNotice =
        "O Windows bloqueou o mouse e o teclado: a janela ativa no outro computador é de um programa aberto como administrador. " +
        "Para controlá-la, abra o Ponte como administrador lá.";

    private long _lastNotice;

    // Avisa o visualizador, no máximo a cada 8 s, em vez de deixar o clique sumir sem explicação.
    private async void WarnIfBlocked(CancellationToken ct)
    {
        if (!InputInjector.LastBlocked || Stopwatch.GetElapsedTime(_lastNotice).TotalSeconds < 8) return;
        _lastNotice = Stopwatch.GetTimestamp();
        try { await channel.SendAsync(Msg.Notice, Encoding.UTF8.GetBytes(BlockedNotice), ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    // Ctrl+Alt+Del pelo serviço; se não der, quem acessa fica sabendo o porquê.
    private async void SecureAttention(CancellationToken ct)
    {
        try
        {
            SessionLog.Write($"controlado: Ctrl+Alt+Del pedido por {PeerName}");
            string? problem = await WindowsHost.SecureAttentionAsync(ct);
            if (problem is not null) await channel.SendAsync(Msg.Notice, Encoding.UTF8.GetBytes(problem), ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        using var files = new FileReceiver(receiveDir);
        while (await channel.ReadAsync(ct) is { } m)
        {
            var p = m.Payload;
            switch (m.Type)
            {
                // Uso é o que vem de quem acessa: mouse, teclado, toque e arquivos (mesmo bloqueados aqui, ele está mexendo).
                case Msg.Mouse when p.Length >= 7:
                    Touch();
                    InputInjector.Mouse(ScreenBounds(_screen), BitConverter.ToUInt16(p, 0), BitConverter.ToUInt16(p, 2), p[4], BitConverter.ToInt16(p, 5));
                    WarnIfBlocked(ct);
                    break;
                case Msg.Key when p.Length >= 3:
                    Touch();
                    InputInjector.Key(BitConverter.ToUInt16(p, 0), (p[2] & 1) != 0, (p[2] & 2) != 0);
                    WarnIfBlocked(ct);
                    break;
                case Msg.Text when p.Length is > 0 and <= 4096:
                    Touch();
                    InputInjector.Text(Encoding.UTF8.GetString(p));
                    WarnIfBlocked(ct);
                    break;
                case Msg.Clipboard when p.Length <= 256 * 1024:
                    ClipboardReceived?.Invoke(Encoding.UTF8.GetString(p));
                    break;
                case Msg.FileStart or Msg.FileChunk or Msg.FileEnd:
                    Touch();
                    if (files.Handle(m.Type, p) is { } path) FileReceived?.Invoke(path);
                    break;
                case Msg.Settings:
                    var s = Channel.FromJson<SettingsMsg>(p);
                    // O tempo sem uso de quem acessa vale primeiro; sem ele (nulo, ou visualizador anterior à 1.13.0), vale o daqui.
                    Volatile.Write(ref _viewerIdle, s.IdleSeconds is { } wanted ? Math.Clamp(wanted, 0, MaxIdleSeconds) : -1);
                    // Só o som mudando não é motivo para mandar a tela inteira de novo.
                    if (s.Screen != _screen || s.Quality != _quality) _forceFull = 1;
                    lock (_videoGate)
                    {
                        var requested = _sendVideo ? (s.Video ?? _video).Normalize() : new VideoOptions();
                        if (WindowsHost.Agent) requested = requested with { Codec = "jpeg", Fps = 20 };
                        if (requested != _video || s.Screen != _screen)
                        {
                            _video = requested;
                            _videoRevision++;
                            CancelActiveVideo();
                        }
                        _screen = s.Screen < 0 ? ScreenInfo.All : s.Screen;
                    }
                    _quality = Math.Clamp(s.Quality, 10, 100);
                    SetSound(s.Audio);
                    if (_capture is { } capturing) capturing.Lossless = _quality >= 80;
                    break;
                case Msg.VideoFeedback when _sendVideo && p.Length <= 1024:
                    var feedback = Channel.FromJson<VideoFeedbackMsg>(p);
                    if (feedback.Failed && feedback.Id == Volatile.Read(ref _streamId))
                    {
                        string reason = new string((feedback.Reason ?? "").Where(c => !char.IsControl(c)).Take(200).ToArray());
                        lock (_videoGate)
                        {
                            _viewerProblem = "o outro computador não conseguiu mostrar o vídeo" + (reason.Length > 0 ? $" ({reason})" : ".");
                            CancelActiveVideo();
                        }
                    }
                    break;
                case Msg.Bye:
                    _byeReceived = true;
                    return;
                case Msg.SecureAttention when p.Length == 0:
                    Touch();
                    SecureAttention(ct);
                    break;
                case Msg.Ping when p.Length == 8:
                    await channel.SendAsync(Msg.Pong, p, ct);
                    break;
                case Msg.Restart when p.Length == 0:
                    if (!config.AllowRemoteRestart)
                    { await channel.SendAsync(Msg.RestartResult, Encoding.UTF8.GetBytes("O dono desativou o reinício remoto nas configurações."), ct); break; }
                    try
                    {
                        RemotePower.ScheduleRestart();
                        SessionLog.Write($"controlado: reinício solicitado por {PeerName} ({Access})");
                        await channel.SendAsync(Msg.RestartResult, default, ct);
                    }
                    catch (Exception ex) when (ex is PonteException or System.ComponentModel.Win32Exception)
                    { await channel.SendAsync(Msg.RestartResult, Encoding.UTF8.GetBytes(ex.Message), ct); }
                    break;
            }
        }
    }
}

/// <summary>
/// Deixa este computador acessível: escuta conexões diretas e, se houver servidor de retransmissão,
/// mantém o registro do ID nele. Toda conexão passa por senha, por um aceite explícito na tela ou
/// pela lista de computadores que este já autorizou.
/// </summary>
internal sealed class HostService(AppConfig config, bool standalone = false) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private HostSession? _current;
    private int _failures;
    private DateTime _lockedUntil;
    private HostService? _managed;
    internal Task? CoordinationTask { get; private set; }
    internal void SetManaged(HostService? host)
    {
        _managed = host;
        if (host is not null)
        {
            host.StatusChanged += () => StatusChanged?.Invoke();
            host.SessionStarted += s => SessionStarted?.Invoke(s);
            host.SessionEnded += s => SessionEnded?.Invoke(s);
            host.FileReceived += p => FileReceived?.Invoke(p);
            host.ClipboardReceived += t => ClipboardReceived?.Invoke(t);
        }
        StatusChanged?.Invoke();
    }

    public string ReceiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Ponte");
    public Func<IncomingRequest, Task<Permission>>? AskPermission;
    public event Action? StatusChanged;
    public event Action<HostSession>? SessionStarted;
    public event Action<HostSession>? SessionEnded;
    public event Action<string>? FileReceived;
    public event Action<string>? ClipboardReceived;

    public HostSession? Current => _managed?.Current ?? _current;
    private bool _relayOnline;
    private string _relayStatus = "";
    public bool RelayOnline { get => _managed?.RelayOnline ?? _relayOnline; private set => _relayOnline = value; }
    public string RelayStatus { get => _managed?.RelayStatus ?? _relayStatus; private set => _relayStatus = value; }
    public string DirectStatus { get; private set; } = "";

    public void Start(IPAddress? bind = null)
    {
        if (!standalone && (WindowsHost.Agent || WindowsHost.Owns(config)))
        {
            RelayStatus = "Serviço do Windows ativo; aguardando a área de trabalho ou a passagem do host.";
            CoordinationTask = WindowsHost.CoordinateAsync(this, config, _cts.Token);
            _ = CoordinationTask.ContinueWith(task => { ErrorLog.Write(task.Exception); SetRelay(false,"Não foi possível iniciar o host pelo serviço. Reabra o Ponte como administrador."); }, TaskContinuationOptions.OnlyOnFaulted);
            return;
        }
        if (config.DirectEnabled)
        {
            try
            {
                _listener = new TcpListener(bind ?? IPAddress.Any, config.ListenPort);
                _listener.Start();
                DirectStatus = "";
                _ = AcceptLoopAsync(_listener, _cts.Token);
            }
            catch (SocketException)
            {
                _listener = null;
                DirectStatus = $"A porta {config.ListenPort} já está em uso; conexões diretas desativadas.";
            }
        }
        if (config.RelayServer.Length > 0) _ = RelayLoopAsync(_cts.Token);
        else RelayStatus = "Sem servidor de retransmissão: só conexões diretas por IP.";
        StatusChanged?.Invoke();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                string remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "rede local";
                _ = HandleAsync(tcp, remote);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    // ---------- pedidos ao servidor pela conexão de registro (transmissão de tela) ----------

    private Stream? _relayStream;
    private readonly SemaphoreSlim _relayWrite = new(1, 1), _relayAsk = new(1, 1);
    private volatile TaskCompletionSource<string>? _relayReply;
    private volatile string _relayReplyPrefix = "\n";

    /// <summary>O registro no servidor de retransmissão (re)começou: quem transmite pede o mesmo código de novo.</summary>
    public event Action? RelayRegistered;

    private async Task WriteRelayAsync(Stream raw, string line, CancellationToken ct)
    {
        await _relayWrite.WaitAsync(ct);
        try { await Transport.WriteLineAsync(raw, line, ct); }
        finally { _relayWrite.Release(); }
    }

    /// <summary>Manda uma linha ao servidor de retransmissão e devolve a resposta que começa com <paramref name="prefix"/>.</summary>
    public async Task<string> AskRelayAsync(string line, string prefix, CancellationToken ct)
    {
        await _relayAsk.WaitAsync(ct);
        try
        {
            var raw = _relayStream ?? throw new PonteException("Sem contato com o servidor de retransmissão.");
            var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _relayReplyPrefix = prefix;
            _relayReply = reply;
            await WriteRelayAsync(raw, line, ct);
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (TimeoutException) { throw new PonteException("O servidor de retransmissão não respondeu (ele pode ser de uma versão sem transmissão de tela)."); }
        catch (IOException) { throw new PonteException("Sem contato com o servidor de retransmissão."); }
        finally
        {
            _relayReply = null;
            _relayAsk.Release();
        }
    }

    /// <summary>Manda uma linha ao servidor de retransmissão sem esperar resposta (se estiver conectado).</summary>
    public async Task TellRelayAsync(string line, CancellationToken ct)
    {
        if (_relayStream is not { } raw) return;
        try { await WriteRelayAsync(raw, line, ct); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void SetRelay(bool online, string status)
    {
        RelayOnline = online;
        RelayStatus = status;
        StatusChanged?.Invoke();
    }

    private async Task RelayLoopAsync(CancellationToken ct)
    {
        var (host, port) = Transport.ParseEndpoint(config.RelayServer, Transport.DefaultRelayPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = await Transport.ConnectTcpAsync(host, port, ct);
                var raw = tcp.GetStream();
                await Transport.WriteLineAsync(raw, Transport.RelayLine(config, "HOST", config.Id), ct);

                // O servidor só registra o ID de quem prova ter a chave privada correspondente.
                string line = await Transport.ReadLineAsync(raw, ct);
                if (!line.StartsWith("NONCE ", StringComparison.Ordinal)) throw new PonteException(Transport.RelayError(line));
                using (var key = config.Certificate.GetECDsaPrivateKey() ?? throw new PonteException("Chave do computador indisponível."))
                {
                    byte[] signature = key.SignData(Convert.FromHexString(line[6..]), HashAlgorithmName.SHA256);
                    string spki = Convert.ToBase64String(config.Certificate.PublicKey.ExportSubjectPublicKeyInfo());
                    await Transport.WriteLineAsync(raw, $"PROOF {spki} {Convert.ToBase64String(signature)}", ct);
                }
                line = await Transport.ReadLineAsync(raw, ct);
                if (line != "OK") throw new PonteException(Transport.RelayError(line));
                SetRelay(true, "Online: pronto para receber conexões pelo ID.");
                _relayStream = raw;
                RelayRegistered?.Invoke();

                long lastPong = Stopwatch.GetTimestamp();
                using var alive = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ping = Task.Run(async () =>
                {
                    while (!alive.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(25), alive.Token);
                        if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastPong)).TotalSeconds > 70) { tcp.Close(); return; }
                        await WriteRelayAsync(raw, "PING", alive.Token);
                    }
                }, CancellationToken.None);
                try
                {
                    while (true)
                    {
                        line = await Transport.ReadLineAsync(raw, ct);
                        if (line == "PONG") Volatile.Write(ref lastPong, Stopwatch.GetTimestamp());
                        else if (line.StartsWith("INCOMING ", StringComparison.Ordinal)) _ = AcceptFromRelayAsync(host, port, line[9..]);
                        else if (_relayReply is { } reply && line.StartsWith(_relayReplyPrefix, StringComparison.Ordinal)) reply.TrySetResult(line);
                    }
                }
                finally
                {
                    _relayStream = null;
                    _relayReply?.TrySetException(new IOException("A conexão com o servidor de retransmissão caiu."));
                    alive.Cancel();
                    try { await ping; }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is PonteException or IOException or SocketException or ObjectDisposedException or FormatException or InvalidDataException or OperationCanceledException)
            {
                SetRelay(false, ex is PonteException ? ex.Message : "Sem contato com o servidor de retransmissão; tentando de novo.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AcceptFromRelayAsync(string host, int port, string token)
    {
        TcpClient? tcp = null;
        try
        {
            // O servidor responde na hora; o prazo é para uma conexão que morreu no caminho não ficar aberta para sempre.
            using var brief = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            brief.CancelAfter(TimeSpan.FromSeconds(20));
            tcp = await Transport.ConnectTcpAsync(host, port, brief.Token);
            var raw = tcp.GetStream();
            await Transport.WriteLineAsync(raw, Transport.RelayLine(config, "ACCEPT", token), brief.Token);
            if (await Transport.ReadLineAsync(raw, brief.Token) != "OK") return;
            var accepted = tcp;
            tcp = null; // daqui em diante quem fecha é o atendimento
            await HandleAsync(accepted, "pelo servidor de retransmissão");
        }
        catch (Exception ex) when (ex is PonteException or IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException) { }
        finally { tcp?.Dispose(); }
    }

    private async Task HandleAsync(TcpClient tcp, string remote)
    {
        Channel? channel = null;
        HostSession? session = null;
        try
        {
            using var preAuth = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            preAuth.CancelAfter(TimeSpan.FromSeconds(20));
            var secure = await Transport.AcceptAsync(tcp, config.Certificate, preAuth.Token);
            channel = new Channel(secure);

            var hello = await channel.ExpectAsync<HelloMsg>(Msg.Hello, preAuth.Token);
            string name = Trim(hello.Name), user = Trim(hello.User);
            hello = hello with { Name = name, User = user, Version = hello.Version ?? "" };
            byte[] nonce = RandomNumberGenerator.GetBytes(32), ownHash = Security.CertificateHash(config.Certificate);
            // A chave própria do visualizador é o que identifica um computador autorizado; o nome que ele manda não prova nada.
            byte[]? viewerKey = TryKey(hello.Key);
            string keyHash = viewerKey is null ? "" : Security.KeyHash(viewerKey);
            bool trusted = viewerKey is not null && config.IsTrusted(keyHash);
            // 1.17.0: os dois lados precisam estar numa conta liberada. Sem isso, o desafio vai sem senha nem autorização
            // (nada de pedir senha à toa) e a resposta recusa com o motivo, que um visualizador antigo também mostra.
            string? ownTicket = Ticket.Valid(config) ? config.Ticket : null;
            string ticketProblem = "";
            string? viewerAccount = viewerKey is null ? null : Ticket.Verify(hello.Ticket, keyHash, out ticketProblem);
            bool accounts = ownTicket is not null && viewerAccount is not null;
            if (!accounts) trusted = false;
            bool passwordOpen = accounts && config.HasPassword && DateTime.UtcNow >= _lockedUntil;
            await channel.SendJsonAsync(Msg.Challenge, new ChallengeMsg(passwordOpen, config.PasswordSalt, config.Iterations, Convert.ToBase64String(nonce), trusted, ownTicket), preAuth.Token);
            // Daqui em diante o outro lado pode estar digitando a senha.
            preAuth.CancelAfter(TimeSpan.FromMinutes(2));
            var response = await channel.ExpectAsync<AuthResponseMsg>(Msg.AuthResponse, preAuth.Token);
            bool signed = viewerKey is not null && Security.VerifyPairing(viewerKey, nonce, ownHash, response.Signature);
            string viewerId = signed ? Security.IdFromSpki(viewerKey!) : "";

            bool ok = false, paired = false;
            string reason, access = "";
            if (!accounts || !signed)
            {
                reason = ownTicket is null ? "O Ponte deste computador não está numa conta liberada. Alguém precisa abrir o Ponte nele e entrar na conta."
                       : hello.Ticket is null ? "Este computador agora só aceita acesso de quem entrou numa conta liberada. Atualize o Ponte (1.17.0 ou mais nova) e entre na conta."
                       : $"A conta de quem está acessando não vale aqui ({(signed ? ticketProblem : "aparelho sem prova da própria chave")}). Saia e entre de novo na conta.";
                if (ownTicket is not null) SessionLog.Write($"controlado: recusado {name} ({remote}): {(hello.Ticket is null ? "sem conta (versão antiga)" : ticketProblem)}");
            }
            else if (trusted && signed && config.TouchTrusted(keyHash, name, user))
            {
                ok = paired = true;
                reason = "";
                access = "autorização salva";
            }
            else if (response.Proof is not null)
            {
                if (!passwordOpen)
                {
                    reason = config.HasPassword ? "Muitas tentativas erradas. O acesso por senha está suspenso por alguns minutos." : "Este computador não tem senha de acesso definida.";
                }
                else
                {
                    byte[] expected = Security.Proof(Convert.FromBase64String(config.PasswordVerifier), nonce, ownHash);
                    ok = TryBase64(response.Proof, out byte[] given) && CryptographicOperations.FixedTimeEquals(expected, given);
                    reason = ok ? "" : "Senha incorreta.";
                    access = "senha";
                    RegisterAttempt(ok);
                    // Quem tem a senha já entra sozinho; autorizar a chave dele só dispensa digitá-la de novo.
                    if (ok && signed && response.Remember) paired = config.Trust(keyHash, viewerId, name, user, viaPassword: true);
                }
            }
            else if (AskPermission is null)
            {
                reason = "Este computador não está aceitando pedidos de conexão.";
            }
            else
            {
                var answer = await AskPermission(new IncomingRequest(name, user, remote, viewerId, signed));
                ok = answer != Permission.Refuse;
                reason = ok ? "" : "O pedido foi recusado.";
                access = "aceite na tela";
                if (answer == Permission.Always && signed) paired = config.Trust(keyHash, viewerId, name, user, viaPassword: false);
            }

            if (ok && hello.Resume && config.ResumeBlocked(keyHash)) { ok = false; reason = "A sessão foi encerrada no computador remoto. Conecte novamente manualmente."; }
            if (ok && signed && !hello.Resume) config.ClearResumeBlock(keyHash);
            if (ok)
            {
                access += " · conta " + viewerAccount;
                session = new HostSession(channel, hello, remote, ReceiveDir, config.ShareSound, config) { Access = access, ViewerKeyHash = signed ? keyHash : "" };
                if (Interlocked.CompareExchange(ref _current, session, null) is not null)
                {
                    session = null;
                    ok = false;
                    reason = "Já existe uma sessão em andamento neste computador.";
                }
            }

            var screens = Screen.AllScreens.Select((s, i) => new ScreenInfo(i, s.Bounds.Width, s.Bounds.Height, s.Primary, s.Bounds.X, s.Bounds.Y)).ToArray();
            int caps = Caps.Cursor | Caps.Video | Caps.Management | Caps.AllScreens | Caps.SecureAttention | Caps.TextFocus | (config.ShareSound && !WindowsHost.Agent ? Caps.Audio : 0);
            await channel.SendJsonAsync(Msg.AuthResult, new AuthResultMsg(ok, reason, Environment.MachineName, screens, paired, Updater.Current.ToString(), caps), _cts.Token);
            if (session is null) return;

            channel.Limit = Channel.MaxPayload;
            session.FileReceived += path => FileReceived?.Invoke(path);
            session.ClipboardReceived += text => ClipboardReceived?.Invoke(text);
            SessionStarted?.Invoke(session);
            SessionLog.Write($"controlado: começou a sessão de {session.PeerName} ({remote}, {access})");
            await session.RunAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is PonteException or IOException or InvalidDataException or OperationCanceledException
                                       or System.Security.Authentication.AuthenticationException or System.Text.Json.JsonException or ObjectDisposedException
                                       or CryptographicException) { }
        finally
        {
            if (session is not null)
            {
                SessionLog.Write($"controlado: fim da sessão de {session.PeerName} ({remote}) depois de {SessionLog.Duration(session.Started)}, " +
                                 $"{session.Summary}: {session.EndReason}");
                Interlocked.CompareExchange(ref _current, null, session);
                SessionEnded?.Invoke(session);
            }
            if (channel is not null) await channel.DisposeAsync();
            tcp.Dispose();
        }
    }

    // Cinco senhas erradas seguidas suspendem o acesso por senha por dez minutos.
    private void RegisterAttempt(bool ok)
    {
        if (ok) { _failures = 0; return; }
        if (Interlocked.Increment(ref _failures) < 5) return;
        _failures = 0;
        _lockedUntil = DateTime.UtcNow.AddMinutes(10);
    }

    private static bool TryBase64(string text, out byte[] bytes)
    {
        bytes = new byte[32];
        return text.Length <= 64 && Convert.TryFromBase64String(text, bytes, out int written) && written == 32;
    }

    private static byte[]? TryKey(string? text)
    {
        if (text is null || text.Length > 256) return null;
        var bytes = new byte[192];
        return Convert.TryFromBase64String(text, bytes, out int written) && written > 0 ? bytes[..written] : null;
    }

    // O nome vem de quem pede e pode faltar (um cliente que não seja o Ponte): vazio, e não uma falha no atendimento.
    private static string Trim(string? text) => text is null ? "" : text.Length > 60 ? text[..60] : text;

    public void Dispose()
    {
        _cts.Cancel();
        _managed?.Dispose();
        _relayStream?.Dispose();
        _listener?.Stop();
        _current?.Stop(reconnect: true);
    }
}
