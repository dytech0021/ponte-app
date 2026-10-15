using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Ponte;

/// <summary>Como a tela vai ao ar: altura da imagem (1080 ou 720), quadros por segundo, som e monitor.</summary>
internal sealed record BroadcastOptions(int Height = 1080, int Fps = 60, bool Sound = true, int Screen = -1)
{
    /// <summary>Taxa do vídeo em kbit/s: a que as plataformas recomendam para jogo em movimento nessa resolução.</summary>
    public int VideoKbps => (Height >= 1080, Fps >= 60) switch { (true, true) => 6000, (true, false) => 4500, (false, true) => 4500, _ => 3000 };

    public string Name => $"{Height}p · {Fps} FPS";
}

/// <summary>
/// Transmissão da tela para quem tiver o link, sem controle nenhum. Como o OBS, o Ponte codifica uma vez (H.264
/// pela placa de vídeo, som em Opus) e envia um único fluxo, cifrado (RTSPS), ao servidor de mídia, que entrega
/// a cada pessoa que abrir o link (WebRTC, com HLS de reserva). Só um computador registrado no servidor de
/// retransmissão consegue o código do link e a permissão de enviar; a senha do envio nasce aqui e só o resumo
/// dela vai ao servidor.
/// </summary>
internal sealed class Broadcast : IAsyncDisposable
{
    public const int MediaPort = 8322;
    private readonly HostService _host;
    private readonly BroadcastOptions _options;
    private readonly string _encoder, _server;
    private readonly string _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask, _counting = Task.CompletedTask;
    private readonly DateTime _started = DateTime.Now;
    // A captura pela placa (DXGI) falhou logo de cara: as próximas tentativas usam a do Windows (GDI).
    private bool _gdi;
    private int _dxgiFailures;
    private long _frames;   // quadros de vídeo que o FFmpeg já enviou nesta rodada (lido pelo cão de guarda)

    /// <summary>O código que vai no link (24 letras e números); o mesmo durante toda a transmissão.</summary>
    public string Token { get; private set; } = "";
    public string Link => $"https://{_server}/ponte/assistir/#{Token}";
    /// <summary>O que está acontecendo, para mostrar na janela.</summary>
    public string Status { get; private set; } = "";
    public bool Live { get; private set; }
    /// <summary>Quantas pessoas estão assistindo; -1 enquanto não se sabe.</summary>
    public int Viewers { get; private set; } = -1;
    public string Encoder => _encoder;
    public BroadcastOptions Options => _options;
    /// <summary>Algo mudou (estado, ao vivo, espectadores). Pode vir de qualquer thread.</summary>
    public event Action? Changed;

    private Broadcast(HostService host, BroadcastOptions options, string encoder, string server)
    {
        _host = host;
        _options = options;
        _encoder = encoder;
        _server = server;
    }

    /// <summary>Servidor da página e do vídeo: o mesmo da retransmissão, pelo nome (o certificado é do nome, não do IP).</summary>
    public static string? WebHost(AppConfig config)
    {
        if (config.RelayServer.Length == 0) return null;
        string host = Transport.ParseEndpoint(config.RelayServer, Transport.DefaultRelayPort).host;
        return System.Net.IPAddress.TryParse(host, out _) || !host.Contains('.') ? null : host;
    }

    private static bool IsToken(string text) => text.Length == 24 && text.All(char.IsAsciiLetterOrDigit);

    public static async Task<Broadcast> StartAsync(HostService host, AppConfig config, BroadcastOptions options, Action<string> progress, CancellationToken ct)
    {
        string server = WebHost(config) ?? throw new PonteException("Para transmitir, o servidor de retransmissão precisa estar configurado pelo nome (não pelo IP).");
        if (!host.RelayOnline) throw new PonteException("Este computador não está conectado ao servidor de retransmissão.");
        if (!VideoRuntime.Present)
        {
            progress("Baixando o componente de vídeo (só na primeira vez)...");
            await VideoRuntime.EnsureAvailableAsync(config, percent => progress($"Baixando o componente de vídeo: {percent}%"), ct);
        }
        progress("Procurando a placa de vídeo...");
        string encoder = await VideoEncoder.FindAsync(new VideoOptions(options.Fps, "h264"), ct)
            ?? throw new PonteException("Nenhuma placa de vídeo deste computador codifica H.264, e a transmissão precisa dela.");
        var broadcast = new Broadcast(host, options, encoder, server);
        progress("Pedindo o link ao servidor...");
        await broadcast.RegisterAsync(ct);
        host.RelayRegistered += broadcast.OnRelayRegistered;
        broadcast._running = Task.Run(broadcast.RunAsync);
        broadcast._counting = Task.Run(broadcast.CountAsync);
        SessionLog.Write($"transmissão: começou ({options.Name}, {(options.Sound ? "com som" : "sem som")}, {VideoEncoder.Vendor(encoder)}), código {broadcast.Token[..4]}…");
        return broadcast;
    }

    // Pede o código ao servidor; depois de uma reconexão, pede o mesmo, para o link continuar valendo.
    private async Task RegisterAsync(CancellationToken ct)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_password))).ToLowerInvariant();
        string reply = await _host.AskRelayAsync(Token.Length > 0 ? $"TRANSMITIR {hash} {Token}" : $"TRANSMITIR {hash}", "TRANSMISSAO ", ct);
        string token = reply["TRANSMISSAO ".Length..].Trim();
        if (!IsToken(token)) throw new PonteException("O servidor respondeu de um jeito inesperado ao pedido de transmissão.");
        Token = token;
    }

    private void OnRelayRegistered()
    {
        if (_stop.IsCancellationRequested || Token.Length == 0) return;
        _ = Task.Run(async () =>
        {
            try { await RegisterAsync(_stop.Token); }
            catch (Exception ex) when (ex is PonteException or OperationCanceledException) { }
        });
    }

    private void SetStatus(string status, bool live)
    {
        Status = status;
        Live = live;
        Changed?.Invoke();
    }

    // Transmite até ser parada; se o envio cair (internet, servidor reiniciando), tenta de novo com o mesmo link.
    private async Task RunAsync()
    {
        var ct = _stop.Token;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();
            string problem;
            try { problem = await StreamOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or PonteException or UnauthorizedAccessException) { problem = ex.Message; }
            if (ct.IsCancellationRequested) break;
            failures = Stopwatch.GetElapsedTime(started).TotalSeconds > 60 ? 1 : failures + 1;
            SessionLog.Write($"transmissão: o envio parou ({problem}); tentativa {failures}");
            int wait = Math.Min(30, 3 * failures);
            SetStatus($"O envio parou ({problem}). Tentando de novo em {wait} s...", false);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
            catch (OperationCanceledException) { break; }
            try { await RegisterAsync(ct); }
            catch (PonteException) { }
            catch (OperationCanceledException) { break; }
        }
    }

    // Conta quem está assistindo a cada 10 s, pelo servidor.
    private async Task CountAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Viewers < 0 ? 4 : 10), ct);
                string reply = await _host.AskRelayAsync($"ESPECTADORES {Token}", $"ESPECTADORES {Token} ", ct);
                if (int.TryParse(reply.AsSpan(reply.LastIndexOf(' ') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count != Viewers)
                {
                    Viewers = count;
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (PonteException) { }
        }
    }

    private async Task<string> StreamOnceAsync(CancellationToken ct)
    {
        var screens = Screen.AllScreens;
        int index = _options.Screen >= 0 && _options.Screen < screens.Length ? _options.Screen : Math.Max(0, Array.FindIndex(screens, s => s.Primary));
        var source = VideoSource.For(screens[index]).First(s => !_gdi || !s.Dxgi);
        string? pipeName = _options.Sound ? $"ponte-transmissao-{Guid.NewGuid():N}" : null;
        using var pipe = pipeName is null ? null : new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        string url = $"rtsps://ponte:{_password}@{_server}:{MediaPort}/ao/{Token}";
        var (outWidth, outHeight) = OutputSize(source.Bounds.Width, source.Bounds.Height, _options.Height);
        SessionLog.Write($"transmissão: capturando pelo {source.Name} ({source.Bounds.Width}x{source.Bounds.Height}), enviando {outWidth}x{outHeight}");
        SetStatus("Conectando ao servidor...", false);
        using var process = new VideoProcess(Arguments(source, _options, _encoder, pipeName is null ? null : $@"\\.\pipe\{pipeName}", url));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var kill = stopping.Token.Register(process.Kill);
        var sound = pipe is null ? Task.CompletedTask : Task.Run(() => PumpSoundAsync(pipe, stopping.Token));
        Interlocked.Exchange(ref _frames, 0);
        var progress = Task.Run(() => WatchProgressAsync(process.Process.StandardOutput, stopping.Token));
        // Cão de guarda: o som pode continuar indo com a imagem parada (a captura da tela travou), e quem assiste fica
        // em "conectando" para sempre. Sem quadro novo por 6 s (ou nenhum em 20 s), reinicia o envio.
        bool stalled = false;
        var watchdog = Task.Run(async () =>
        {
            long seen = 0, since = Stopwatch.GetTimestamp(), start = since;
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(1000, stopping.Token);
                long now = Interlocked.Read(ref _frames);
                if (now != seen)
                {
                    seen = now;
                    since = Stopwatch.GetTimestamp();
                }
                else if (now > 0 ? Stopwatch.GetElapsedTime(since) > TimeSpan.FromSeconds(6) : Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(20))
                {
                    stalled = true;
                    process.Kill();
                    return;
                }
            }
        });
        try
        {
            await process.Process.WaitForExitAsync(ct);
        }
        finally
        {
            stopping.Cancel();
            try { await Task.WhenAll(sound, progress, watchdog); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
        ct.ThrowIfCancellationRequested();
        if (stalled)
        {
            // Imagem parada com a captura pelo DirectX: a próxima rodada usa a do GDI.
            if (source.Dxgi) _gdi = true;
            return $"a imagem da tela parou de sair (captura pelo {source.Name}, {Interlocked.Read(ref _frames)} quadros enviados)";
        }
        string error = process.Error;
        // A captura pelo DirectX falhou: antes de entrar no ar, ou duas vezes já no ar, passa para a do GDI.
        if (source.Dxgi && (error.Contains("ddagrab", StringComparison.OrdinalIgnoreCase) || error.Contains("d3d11", StringComparison.OrdinalIgnoreCase)) &&
            (!Live || ++_dxgiFailures >= 2))
            _gdi = true;
        return error.Contains("401") || error.Contains("Unauthorized") ? "o servidor recusou a permissão de envio"
            : error.Length > 0 ? error : $"o FFmpeg terminou com o código {process.Process.ExitCode}";
    }

    // O FFmpeg informa o andamento ("frame=...", "progress=continue"): quadro saindo é transmissão no ar.
    private async Task WatchProgressAsync(StreamReader output, CancellationToken ct)
    {
        long frames = 0;
        while (await output.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("frame=", StringComparison.Ordinal) || !long.TryParse(line.AsSpan(6), out long now)) continue;
            if (now > frames && !Live) SetStatus("Ao vivo", true);
            frames = now;
            Interlocked.Exchange(ref _frames, now);
        }
    }

    /// <summary>
    /// Mantém o som chegando ao FFmpeg no ritmo do relógio: quando nada toca, o Windows não entrega nada, e o
    /// FFmpeg ficaria esperando (o vídeo junto). O que falta vira silêncio; o que sobra demais é descartado.
    /// </summary>
    private static async Task PumpSoundAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await pipe.WaitForConnectionAsync(ct);
        var queue = new ConcurrentQueue<short[]>();
        using var capture = new AudioCapture(chunk => { if (queue.Count < 100) queue.Enqueue(chunk); });
        const int rate = AudioCodec.Rate, frame = 960;   // 20 ms
        var silence = new byte[frame * 4];
        long start = Stopwatch.GetTimestamp(), written = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10, ct);
            long expected = (long)(Stopwatch.GetElapsedTime(start).TotalSeconds * rate);
            while (queue.TryDequeue(out var chunk))
            {
                if (written > expected + rate / 10) continue;   // mais de 100 ms adiantado: descarta
                await pipe.WriteAsync(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.AsSpan()).ToArray(), ct);
                written += chunk.Length / 2;
            }
            // Mais de 60 ms atrás (nada tocando): completa com silêncio.
            while (written < expected - rate * 6 / 100)
            {
                await pipe.WriteAsync(silence, ct);
                written += frame;
            }
        }
    }

    /// <summary>Tamanho da imagem transmitida: a tela reduzida até a altura pedida (nunca ampliada), com medidas pares.</summary>
    internal static (int Width, int Height) OutputSize(int width, int height, int targetHeight)
    {
        if (height <= targetHeight) return (width & ~1, height & ~1);
        int scaled = (int)Math.Round(width * (double)targetHeight / height / 2) * 2;
        return (scaled, targetHeight & ~1);
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static List<string> Arguments(VideoSource source, BroadcastOptions o, string encoder, string? soundPipe, string url)
    {
        var b = source.Bounds;
        var (w, h) = OutputSize(b.Width, b.Height, o.Height);
        string scale = $"scale={w}:{h}:flags=bicubic:out_color_matrix=bt709:out_range=tv,format=nv12";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-progress", "pipe:1" };
        if (source.Dxgi)
        {
            // Para quem assiste, o ponteiro faz parte da imagem.
            args.AddRange(["-init_hw_device", $"d3d11va=tela:{source.Adapter}", "-filter_hw_device", "tela", "-filter_complex",
                $"ddagrab=output_idx={source.Output}:framerate={o.Fps}:draw_mouse=1:video_size={b.Width}x{b.Height},hwdownload,format=bgra,{scale}[v]"]);
        }
        else
        {
            args.AddRange(["-f", "gdigrab", "-draw_mouse", "1", "-framerate", N(o.Fps), "-offset_x", N(b.X), "-offset_y", N(b.Y),
                "-video_size", $"{b.Width}x{b.Height}", "-i", "desktop"]);
        }
        if (soundPipe is not null) args.AddRange(["-thread_queue_size", "1024", "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", soundPipe]);
        if (source.Dxgi) args.AddRange(["-map", "[v]"]);
        else args.AddRange(["-map", "0:v", "-vf", scale]);
        if (soundPipe is not null) args.AddRange(["-map", $"{(source.Dxgi ? 0 : 1)}:a"]);

        long bitrate = o.VideoKbps * 1000L;
        // Sem quadros B (o WebRTC não os aceita) e quadro-chave a cada 2 s: quem entra no meio vê a imagem logo.
        args.AddRange(["-c:v", encoder, "-pix_fmt", "nv12", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-color_range", "tv", "-profile:v", "high", "-bf", "0", "-g", N(o.Fps * 2),
            "-b:v", N(bitrate), "-maxrate", N(bitrate), "-bufsize", N(bitrate)]);
        // Quadro completo (IDR) a cada 2 s, imposto quadro a quadro: alguns drivers ignoram o -g e, no modo de baixa
        // latência, trocam os quadros completos por "atualização em faixas" (intra refresh). Quem entra na transmissão
        // depois do começo nunca recebia um quadro de onde começar e ficava com a tela preta (visto com uma AMD em 15/10).
        args.AddRange(["-force_key_frames", "expr:gte(t,n_forced*2)"]);
        if (encoder.EndsWith("_nvenc")) args.AddRange(["-preset", "p4", "-tune", "ll", "-rc", "cbr", "-forced-idr", "1"]);
        else if (encoder.EndsWith("_amf"))
            args.AddRange(["-usage", "lowlatency", "-quality", "balanced", "-rc", "cbr", "-forced_idr", "1", "-intra_refresh_mb", "0",
                "-header_spacing", N(o.Fps * 2)]);
        else if (encoder.EndsWith("_qsv")) args.AddRange(["-preset", "veryfast", "-forced_idr", "1"]);
        else args.AddRange(["-preset", "veryfast"]);
        if (soundPipe is not null) args.AddRange(["-c:a", "libopus", "-b:a", "128k", "-ar", "48000", "-ac", "2"]);
        // O certificado do servidor é conferido pelo Windows: a senha do envio só vai para o servidor de verdade.
        args.AddRange(["-f", "rtsp", "-rtsp_transport", "tcp", "-tls_verify", "1", url]);
        return args;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _host.RelayRegistered -= OnRelayRegistered;
        _stop.Cancel();
        try { await Task.WhenAll(_running, _counting); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or PonteException) { }
        await _host.TellRelayAsync($"PARAR {Token}", CancellationToken.None);
        SessionLog.Write($"transmissão: encerrada depois de {SessionLog.Duration(_started)}");
        Live = false;
        Status = "Transmissão encerrada.";
    }
}
