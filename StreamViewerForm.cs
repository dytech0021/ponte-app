using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Ponte;

/// <summary>
/// Assistir a uma transmissão de tela dentro do Ponte, em vez de no navegador. A transmissão não muda: o vídeo é o
/// mesmo que a página recebe, só que tirado do servidor de mídia pela conexão cifrada (RTSPS, porta 8322) e
/// decodificado pelo FFmpeg do Ponte (pela placa de vídeo, quando ela decodifica). Quem assiste não controla nada.
/// </summary>
internal sealed class StreamViewerForm : Form
{
    private readonly string _server, _token;
    private readonly AppConfig _config;
    // A transmissão está sem som (quem transmite desmarcou): o FFmpeg não pode ter a saída de áudio.
    private bool _noAudio;
    private readonly StreamView _view = new() { Dock = DockStyle.Fill };
    private readonly Label _state = new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(30, 30, 30),
        Font = new Font("Segoe UI", 11F), Dock = DockStyle.Fill,
    };
    private readonly ToolStrip _bar = new() { Dock = DockStyle.Bottom, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    private readonly ToolStripLabel _live = new("Conectando...");
    private readonly ToolStripButton _sound = new("Som ligado") { Checked = true, CheckOnClick = true };
    private readonly CancellationTokenSource _stop = new();
    private AudioPlayer? _player;
    private FormWindowState _windowedState = FormWindowState.Normal;
    private Rectangle _windowedBounds;
    private bool _fullScreen;

    /// <summary>
    /// Lê o link da transmissão (https://servidor/ponte/assistir/#código) ou só o código, como é colado no campo de
    /// endereço. Devolve null se não for de transmissão. Só vale o servidor deste Ponte.
    /// </summary>
    public static (string Server, string Token)? Parse(string text, AppConfig config)
    {
        text = text.Trim();
        if (Broadcast.WebHost(config) is not { } server) return null;
        if (text.Length == 24 && text.All(char.IsAsciiLetterOrDigit) && text.Any(char.IsAsciiLetter)) return (server, text);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.AbsolutePath.TrimEnd('/').EndsWith("/ponte/assistir", StringComparison.OrdinalIgnoreCase)) return null;
        string token = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')).Trim();
        if (token.Length != 24 || !token.All(char.IsAsciiLetterOrDigit)) return null;
        if (!string.Equals(uri.Host, server, StringComparison.OrdinalIgnoreCase))
            throw new PonteException($"Este link é de outro servidor ({uri.Host}); o Ponte só assiste às transmissões de {server}.");
        return (server, token);
    }

    public StreamViewerForm(AppConfig config, string server, string token)
    {
        _config = config;
        _server = server;
        _token = token;
        Text = "Assistindo à transmissão - Ponte";
        Icon = Ui.MakeIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(480, 320);
        BackColor = Color.Black;
        KeyPreview = true;

        _bar.Items.Add(_live);
        _bar.Items.Add(new ToolStripSeparator());
        _sound.Click += (_, _) => ApplySound();
        _bar.Items.Add(_sound);
        var full = new ToolStripButton("Tela cheia (F11)");
        full.Click += (_, _) => ToggleFullScreen();
        _bar.Items.Add(full);
        var copy = new ToolStripButton("Copiar link");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"https://{_server}/ponte/assistir/#{_token}"); }
            catch (ExternalException) { }
        };
        _bar.Items.Add(copy);

        Controls.Add(_view);
        Controls.Add(_state);
        Controls.Add(_bar);
        _view.Visible = false;
        _state.Text = "Conectando à transmissão...";
        _view.DoubleClick += (_, _) => ToggleFullScreen();
        Shown += (_, _) => _ = Task.Run(() => RunAsync(_stop.Token));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F11 || (e.KeyCode == Keys.Escape && _fullScreen)) { ToggleFullScreen(); e.Handled = true; }
        else if (e.KeyCode == Keys.M) { _sound.Checked = !_sound.Checked; ApplySound(); e.Handled = true; }
    }

    private void ApplySound()
    {
        _sound.Text = _sound.Checked ? "Som ligado" : "Som desligado";
        if (!_sound.Checked) _player?.Silence();
    }

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _windowedState = WindowState;
            if (WindowState == FormWindowState.Normal) _windowedBounds = Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _bar.Visible = false;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            if (_windowedBounds.Width > 0) Bounds = _windowedBounds;
            WindowState = _windowedState;
            _bar.Visible = true;
        }
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    /// <summary>A imagem da transmissão já apareceu nesta janela.</summary>
    public bool Watching { get; private set; }

    private void ShowState(string text, bool live)
    {
        if (live) Watching = true;
        Post(() =>
        {
            _live.Text = live ? "● Ao vivo" : text.Split('\n')[0];
            _live.ForeColor = live ? Color.Firebrick : SystemColors.ControlText;
            _state.Text = text;
            _state.Visible = !live;
            _view.Visible = live;
        });
    }

    // Assiste até a janela fechar: espera a transmissão começar, volta sozinho se ela cair, para quando ela termina.
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!VideoRuntime.Present)
            {
                ShowState("Baixando o componente de vídeo (só na primeira vez)...", false);
                await VideoRuntime.EnsureAvailableAsync(_config, percent => ShowState($"Baixando o componente de vídeo: {percent}%", false), ct);
            }
        }
        catch (Exception ex) when (ex is PonteException or IOException or UnauthorizedAccessException)
        {
            ShowState("Não foi possível baixar o componente de vídeo: " + ex.Message, false);
            return;
        }
        catch (OperationCanceledException) { return; }

        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var (outcome, detail) = await WatchOnceAsync(ct);
            if (ct.IsCancellationRequested) return;
            switch (outcome)
            {
                case Outcome.Ended:
                    ShowState("Esta transmissão terminou.\n(Ou o link não está completo.)", false);
                    return;
                case Outcome.NotStarted:
                    failures = 0;
                    ShowState("A transmissão ainda não começou ou está voltando.\nA imagem aparece aqui sozinha.", false);
                    break;
                default:
                    failures++;
                    ShowState($"A conexão com a transmissão caiu{(detail.Length > 0 ? $" ({detail})" : "")}.\nReconectando...", false);
                    break;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(outcome == Outcome.NotStarted ? 3 : Math.Min(15, 2 * failures)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private enum Outcome { Ended, NotStarted, Dropped }

    private async Task<(Outcome, string)> WatchOnceAsync(CancellationToken ct)
    {
        string id = Guid.NewGuid().ToString("N");
        using var videoPipe = new NamedPipeServerStream($"ponte-assistir-v-{id}", PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16 * 1024 * 1024, 4096);
        using var audioPipe = new NamedPipeServerStream($"ponte-assistir-a-{id}", PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1 << 20, 4096);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin",
            // Sem juntar vídeo antes de mostrar: o atraso fica perto do que a página tem.
            "-fflags", "nobuffer", "-flags", "low_delay", "-hwaccel", "auto",
            "-rtsp_transport", "tcp", "-tls_verify", "1", "-timeout", "10000000",
            "-i", $"rtsps://{_server}:{Broadcast.MediaPort}/ao/{_token}",
            // Vídeo em YUV 4:2:0 com cabeçalho (Y4M: traz a largura e a altura); a conversão para a tela é feita aqui.
            "-map", "0:v:0", "-pix_fmt", "yuv420p", "-f", "yuv4mpegpipe", "-y", $@"\\.\pipe\ponte-assistir-v-{id}" };
        if (!_noAudio) args.AddRange(["-map", "0:a:0", "-f", "s16le", "-ar", "48000", "-ac", "2", "-y", $@"\\.\pipe\ponte-assistir-a-{id}"]);
        using var process = new VideoProcess(args);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var kill = stopping.Token.Register(process.Kill);
        var video = Task.Run(() => ReadVideoAsync(videoPipe, stopping.Token));
        var audio = Task.Run(() => ReadAudioAsync(audioPipe, stopping.Token));
        try { await process.Process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { }
        finally
        {
            stopping.Cancel();
            try { await Task.WhenAll(video, audio).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or ObjectDisposedException) { }
        }
        string error = process.Error;
        if (!_noAudio && (error.Contains("matches no streams") || error.Contains("does not contain any stream")))
        {
            _noAudio = true;
            return (Outcome.NotStarted, "");
        }
        if (error.Contains("401") || error.Contains("403")) return (Outcome.Ended, "");
        if (error.Contains("404")) return (Outcome.NotStarted, "");
        return (Outcome.Dropped, error.Length > 120 ? error[..120] : error);
    }

    private async Task ReadVideoAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            var reader = new BufferedStream(pipe, 1 << 20);
            // Cabeçalho Y4M: "YUV4MPEG2 W1920 H1080 F60:1 ..." até o fim da linha.
            string header = await ReadLineAsync(reader, ct);
            int width = 0, height = 0;
            foreach (string part in header.Split(' '))
            {
                if (part.StartsWith('W')) int.TryParse(part[1..], out width);
                else if (part.StartsWith('H')) int.TryParse(part[1..], out height);
            }
            if (!header.StartsWith("YUV4MPEG2") || width < 16 || height < 16 || width > 8192 || height > 8192 || (width & 1) != 0 || (height & 1) != 0)
                throw new InvalidDataException("Vídeo em formato inesperado.");
            _view.Prepare(width, height);
            var yuv = GC.AllocateUninitializedArray<byte>(width * height * 3 / 2, pinned: true);
            bool first = true;
            while (!ct.IsCancellationRequested)
            {
                string mark = await ReadLineAsync(reader, ct);   // "FRAME" (com parâmetros opcionais)
                if (!mark.StartsWith("FRAME")) throw new InvalidDataException("Vídeo em formato inesperado.");
                await reader.ReadExactlyAsync(yuv, ct);
                _view.Present(yuv);
                if (first)
                {
                    first = false;
                    ShowState("", true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (line.Length < 256)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
            if (one[0] == (byte)'\n') return line.ToString();
            line.Append((char)one[0]);
        }
        throw new InvalidDataException("Vídeo em formato inesperado.");
    }

    private async Task ReadAudioAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await pipe.WaitForConnectionAsync(ct);
            _player ??= new AudioPlayer();
            var buffer = new byte[3840];   // 20 ms de 48 kHz estéreo
            var samples = new short[buffer.Length / 2];
            while (!ct.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(buffer, ct);
                if (!_sound.Checked) continue;
                Buffer.BlockCopy(buffer, 0, samples, 0, buffer.Length);
                _player.Play(samples, AudioCodec.Rate, AudioCodec.Channels);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _stop.Cancel();
        _player?.Dispose();
        _view.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>Mostra o quadro mais recente, ajustado à janela (com faixas pretas). Dois bitmaps se revezam.</summary>
internal sealed class StreamView : Control
{
    private readonly object _gate = new();
    private Bitmap[]? _frames;
    private byte[][]? _memory;
    private int _width, _height, _current = -1;
    private int _pending;

    public StreamView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        BackColor = Color.Black;
    }

    public void Prepare(int width, int height)
    {
        lock (_gate)
        {
            if (_frames is not null && width == _width && height == _height) return;
            Release();
            _width = width;
            _height = height;
            _memory = new byte[2][];
            _frames = new Bitmap[2];
            for (int i = 0; i < 2; i++)
            {
                _memory[i] = GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true);
                _frames[i] = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, Marshal.UnsafeAddrOfPinnedArrayElement(_memory[i], 0));
            }
            _current = -1;
        }
    }

    /// <summary>Converte o quadro YUV no bitmap que não está na tela e troca. Chamado pela thread que lê o vídeo.</summary>
    public void Present(byte[] yuv)
    {
        int target;
        lock (_gate)
        {
            if (_memory is null) return;
            target = _current == 0 ? 1 : 0;
        }
        // O bitmap da tela fica intocado enquanto o outro recebe o quadro; a troca espera o desenho em andamento.
        VideoDecoder.ToBgra(yuv, _memory![target], _width, _height, false);
        lock (_gate) _current = target;
        if (Interlocked.Exchange(ref _pending, 1) == 0 && IsHandleCreated)
        {
            try { BeginInvoke(() => { _pending = 0; Invalidate(); }); }
            catch (InvalidOperationException) { }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        lock (_gate)
        {
            if (_frames is null || _current < 0)
            {
                g.Clear(Color.Black);
                return;
            }
            float scale = Math.Min((float)ClientSize.Width / _width, (float)ClientSize.Height / _height);
            int w = (int)(_width * scale), h = (int)(_height * scale);
            var target = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            using (var black = new SolidBrush(Color.Black))
            {
                g.FillRectangle(black, 0, 0, ClientSize.Width, target.Top);
                g.FillRectangle(black, 0, target.Bottom, ClientSize.Width, ClientSize.Height - target.Bottom);
                g.FillRectangle(black, 0, target.Top, target.Left, h);
                g.FillRectangle(black, target.Right, target.Top, ClientSize.Width - target.Right, h);
            }
            g.InterpolationMode = Math.Abs(scale - 1) < 0.01 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_frames[_current], target);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private void Release()
    {
        if (_frames is null) return;
        foreach (var frame in _frames) frame.Dispose();
        _frames = null;
        _memory = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_gate) Release();
        base.Dispose(disposing);
    }
}
