using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Ponte;

/// <summary>Área que mostra a tela remota e transforma mouse e teclado locais em comandos para o outro lado.</summary>
internal sealed class RemoteView : Control
{
    private readonly ViewerSession _session;
    private readonly HashSet<int> _keysDown = new();
    private readonly System.Windows.Forms.Timer _moveTimer = new() { Interval = 12 };
    private RemotePointer? _pointer;
    private Point? _pendingMove;
    private int _repaintPending;
    private long _lastMove;
    /// <summary>Quantas vezes a imagem foi desenhada e quanto tempo isso levou, para a barra de estado.</summary>
    public long Paints, PaintTicks;

    // As áreas abertas (uma por janela, em "Cada tela em uma janela"): com o botão apertado, o mouse fica preso à área
    // onde o clique começou; quando ele passa por cima da janela de outra tela, o arrasto continua nela (1.17.2).
    private static readonly List<RemoteView> Open = new();

    public RemoteView(ViewerSession session)
    {
        _session = session;
        Open.Add(this);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        TabStop = true;
        BackColor = Color.FromArgb(30, 30, 30);
        _moveTimer.Tick += (_, _) =>
        {
            if (_pendingMove is { } p) SendMove(p);
            else _moveTimer.Stop();
        };
    }

    /// <summary>
    /// O ponteiro do outro computador passa a ser o ponteiro daqui enquanto o mouse está sobre a imagem:
    /// assim se vê a forma que ele tem lá (seta, barra de texto, redimensionar) sem o atraso da imagem.
    /// </summary>
    public void SetPointer(byte[] payload)
    {
        var previous = _pointer;
        _pointer = RemotePointer.FromPayload(payload);
        Cursor = _pointer?.Cursor ?? Cursors.Default;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Open.Remove(this);
            _moveTimer.Dispose();
            Cursor = Cursors.Default;
            _pointer?.Dispose();
            _pointer = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>Pode ser chamado de qualquer thread; vários quadros seguidos viram uma repintura só.</summary>
    public void RequestRepaint()
    {
        if (!IsHandleCreated || Interlocked.Exchange(ref _repaintPending, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                _repaintPending = 0;
                Invalidate();
            });
        }
        catch (InvalidOperationException) { _repaintPending = 0; }
    }

    private Rectangle Fit(int w, int h) => Fit(ClientSize, w, h);

    /// <summary>Onde a tela remota fica dentro da área: o maior retângulo com a proporção dela, centralizado.</summary>
    public static Rectangle Fit(Size view, int w, int h)
    {
        double scale = Math.Min(view.Width / (double)w, view.Height / (double)h);
        int dw = Math.Max(1, (int)(w * scale)), dh = Math.Max(1, (int)(h * scale));
        return new Rectangle((view.Width - dw) / 2, (view.Height - dh) / 2, dw, dh);
    }

    /// <summary>
    /// A posição que vai na mensagem de mouse para um ponto da área de tamanho <paramref name="view"/> que
    /// mostra uma tela remota de <paramref name="w"/> por <paramref name="h"/> pixels.
    /// </summary>
    public static (int X, int Y) ToRemote(Point p, Size view, int w, int h) => ToRemote(p, view, w, h, new Rectangle(0, 0, w, h));

    /// <param name="source">O pedaço da imagem de <paramref name="w"/> por <paramref name="h"/> que a área mostra (com uma janela por tela, o daquela tela).</param>
    public static (int X, int Y) ToRemote(Point p, Size view, int w, int h, Rectangle source)
    {
        var dest = Fit(view, source.Width, source.Height);
        // Primeiro o pixel de lá que está debaixo do ponteiro daqui (pelo centro do pixel daqui); depois a
        // escala de 0 a 65535 da mensagem, arredondada, que o outro lado desfaz sem perder o pixel.
        int px = source.X + Math.Clamp((int)Math.Floor((p.X - dest.X + 0.5) * source.Width / dest.Width), 0, source.Width - 1);
        int py = source.Y + Math.Clamp((int)Math.Floor((p.Y - dest.Y + 0.5) * source.Height / dest.Height), 0, source.Height - 1);
        return ((int)Math.Round(px * 65535.0 / Math.Max(1, w - 1)), (int)Math.Round(py * 65535.0 / Math.Max(1, h - 1)));
    }

    private RectangleF _crop = new(0, 0, 1, 1);
    private double _cropAspect;

    /// <summary>
    /// Mostra só um pedaço da imagem, em frações dela: com todas as telas numa imagem só, o de uma tela. O recorte só
    /// vale quando a imagem tem a proporção <paramref name="wholeAspect"/> da área de trabalho inteira; até ela chegar,
    /// a área mostra a imagem que houver.
    /// </summary>
    public void SetCrop(RectangleF crop, double wholeAspect)
    {
        _crop = crop;
        _cropAspect = wholeAspect;
        Invalidate();
    }

    public void ClearCrop() => SetCrop(new RectangleF(0, 0, 1, 1), 0);

    /// <summary>O pedaço da imagem de <paramref name="w"/> por <paramref name="h"/> pixels que esta área mostra.</summary>
    public Rectangle Source(int w, int h) => Source(_crop, _cropAspect, w, h);

    /// <summary>O mesmo cálculo sem precisar de um controle (o autoteste roda fora da linha de execução das janelas).</summary>
    public static Rectangle Source(RectangleF crop, double wholeAspect, int w, int h)
    {
        if (wholeAspect <= 0 || Math.Abs(w / (double)h / wholeAspect - 1) > 0.02) return new Rectangle(0, 0, w, h);
        int left = Math.Clamp((int)Math.Round(crop.Left * w), 0, w - 1), top = Math.Clamp((int)Math.Round(crop.Top * h), 0, h - 1);
        int right = Math.Clamp((int)Math.Round(crop.Right * w), left + 1, w), bottom = Math.Clamp((int)Math.Round(crop.Bottom * h), top + 1, h);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        lock (_session.CanvasLock)
        {
            var canvas = _session.Canvas;
            if (canvas is null)
            {
                TextRenderer.DrawText(e.Graphics, "Aguardando a imagem do outro computador...", Font, ClientRectangle, Color.Gainsboro,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            long start = Stopwatch.GetTimestamp();
            var source = Source(canvas.Width, canvas.Height);
            var dest = Fit(source.Width, source.Height);
            e.Graphics.InterpolationMode = dest.Width == source.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            if (source.Size == canvas.Size) e.Graphics.DrawImage(canvas, dest);
            else e.Graphics.DrawImage(canvas, dest, source, GraphicsUnit.Pixel);
            PaintTicks += Stopwatch.GetTimestamp() - start;
            Paints++;
        }
    }

    private bool Map(Point p, out int nx, out int ny)
    {
        nx = ny = 0;
        int w, h;
        lock (_session.CanvasLock)
        {
            if (_session.Canvas is null) return false;
            w = _session.Canvas.Width;
            h = _session.Canvas.Height;
        }
        (nx, ny) = ToRemote(p, ClientSize, w, h, Source(w, h));
        return true;
    }

    private void SendMouse(Point p, int action, int wheel = 0)
    {
        var (view, local) = Under(p);
        if (view.Map(local, out int nx, out int ny)) _ = _session.SendMouseAsync(nx, ny, action, wheel);
    }

    /// <summary>
    /// A área (desta sessão) que está debaixo do ponto: fora desta, com o mouse preso a ela num arrasto, pode ser a janela
    /// de outra tela, e o ponto vira o ponto dentro dela. Assim uma janela de lá pode ser arrastada de uma tela para outra.
    /// </summary>
    private (RemoteView View, Point Local) Under(Point p)
    {
        if (ClientRectangle.Contains(p) || !IsHandleCreated) return (this, p);
        var onScreen = PointToScreen(p);
        foreach (var other in Open)
        {
            if (other == this || other._session != _session || !other.Visible || !other.IsHandleCreated) continue;
            if (other.RectangleToScreen(other.ClientRectangle).Contains(onScreen)) return (other, other.PointToClient(onScreen));
        }
        return (this, p);
    }

    private void SendMove(Point p)
    {
        _pendingMove = null;
        _lastMove = Stopwatch.GetTimestamp();
        SendMouse(p, 0);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Um movimento a cada 12 ms basta; mais que isso só enche a fila de envio. O que ficou para trás
        // sai pelo temporizador: é o último movimento que deixa o ponteiro de lá onde o daqui parou.
        if (Stopwatch.GetElapsedTime(_lastMove).TotalMilliseconds < 12)
        {
            _pendingMove = e.Location;
            _moveTimer.Start();
            return;
        }
        SendMove(e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _pendingMove = null;
        int action = e.Button switch { MouseButtons.Left => 1, MouseButtons.Right => 3, MouseButtons.Middle => 5, _ => 0 };
        SendMouse(e.Location, action);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pendingMove = null;
        int action = e.Button switch { MouseButtons.Left => 2, MouseButtons.Right => 4, MouseButtons.Middle => 6, _ => 0 };
        SendMouse(e.Location, action);
    }

    protected override void OnMouseWheel(MouseEventArgs e) => SendMouse(e.Location, 7, e.Delta);

    // Todas as teclas vão para o outro computador, inclusive Tab, Alt e F4, que o Windows Forms usaria aqui.
    public override bool PreProcessMessage(ref Message msg)
    {
        switch (msg.Msg)
        {
            case Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN or Native.WM_KEYUP or Native.WM_SYSKEYUP:
                int vk = (int)msg.WParam & 0xFFFF;
                // Num celular, o que forma letra vai como texto pronto (WM_CHAR, com acentos e o layout daqui); só as
                // teclas de comando vão como teclas.
                if (_session.HostIsPhone && !PhoneCommandKey(vk)) return true;
                bool up = msg.Msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                bool extended = (((long)msg.LParam >> 24) & 1) != 0;
                if (up) _keysDown.Remove(vk);
                else _keysDown.Add(vk);
                _ = _session.SendKeyAsync(vk, up, extended);
                return true;
            case Native.WM_CHAR when _session.HostIsPhone:
                char c = (char)((int)msg.WParam & 0xFFFF);
                if (char.IsHighSurrogate(c)) { _pendingSurrogate = c; return true; }
                string text = _pendingSurrogate is { } high && char.IsLowSurrogate(c) ? $"{high}{c}" : c.ToString();
                _pendingSurrogate = null;
                if (!char.IsControl(text[0])) _ = _session.SendTextAsync(text);
                return true;
            case Native.WM_CHAR or Native.WM_DEADCHAR or Native.WM_SYSCHAR or Native.WM_SYSDEADCHAR:
                return true;
        }
        return base.PreProcessMessage(ref msg);
    }

    private char? _pendingSurrogate;

    // Teclas que um celular trata como comando: modificadoras, Enter, apagar, setas, Esc (Voltar), Windows (Início) e
    // qualquer tecla com Ctrl (copiar, colar...). As outras viram texto pelo WM_CHAR.
    private static bool PhoneCommandKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x1B or 0x0D or 0x08 or 0x2E or 0x09 or (>= 0x23 and <= 0x28)
            or 0x70 or 0x5B or 0x5C or PhoneKeys.Back or PhoneKeys.Home or PhoneKeys.Recents
        || (Native.GetKeyState(0x11) & 0x8000) != 0 && (Native.GetKeyState(0x12) & 0x8000) == 0;

    protected override void OnLostFocus(EventArgs e)
    {
        // Ao sair da janela com uma tecla apertada, a soltura nunca chegaria ao outro lado.
        foreach (int vk in _keysDown) _ = _session.SendKeyAsync(vk, true, false);
        _keysDown.Clear();
        base.OnLostFocus(e);
    }

    /// <summary>Envia uma combinação que o Windows local interceptaria se fosse digitada, como Alt+Tab.</summary>
    public void SendCombo(params int[] keys)
    {
        foreach (int vk in keys) _ = _session.SendKeyAsync(vk, false, vk is 0x5B);
        foreach (int vk in keys.Reverse()) _ = _session.SendKeyAsync(vk, true, vk is 0x5B);
    }
}

/// <summary>Janela da sessão: a tela remota e a barra com os comandos.</summary>
internal sealed class ViewerForm : Form
{
    private readonly AutomaticQuality _automaticQuality = new();
    private readonly ToolStripMenuItem _automaticQualityItem;
    private int _qualityTicks;
    private readonly ViewerSession _session;
    private readonly RemoteView _view;
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _stats = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    // Na tela cheia as barras somem; a de comandos volta quando o mouse encosta no alto da tela.
    private readonly System.Windows.Forms.Timer _reveal = new() { Interval = 150 };
    private readonly ToolStrip _bar;
    private readonly StatusStrip _statusBar;
    private readonly ToolStripButton _clipboard, _sound;
    private readonly ToolStripDropDownButton _video;
    private readonly Dictionary<int, ToolStripMenuItem> _fpsItems = new();
    private readonly Dictionary<string, ToolStripMenuItem> _codecItems = new();
    private readonly ToolStripMenuItem _chroma444, _ultraLowLatency, _bitrate, _restoreProfile;
    private readonly ToolStripDropDownButton _profileButton;
    private readonly Dictionary<string, ToolStripMenuItem> _profileItems = new();
    private readonly Dictionary<int, ToolStripMenuItem> _qualityItems = new();
    private string _profile;
    private readonly AudioPlayer _player = new();
    private readonly AppConfig? _config;
    private readonly string? _thumbnail;
    private int _screen, _quality = 60, _seconds;
    private long _lastFrames, _lastBytes, _lastPaints, _lastPaintTicks;
    /// <summary>Tempo médio de cada desenho da imagem no último segundo (diagnóstico).</summary>
    public double PaintMilliseconds { get; private set; }
    public string StatsText => _stats.Text ?? "";
    private long _lastStatsAt = Stopwatch.GetTimestamp();
    private bool _fullScreen, _closing, _hasThumbnail, _started;
    private VideoOptions _videoOptions;
    private VideoSettingsDialog? _videoDialog;
    private string _videoStatusText, _performanceText = "Aguardando quadros", _localVideoNote = "";
    private bool _preparingRuntime;
    private readonly CancellationTokenSource _closed = new();
    private const string VideoUpdateRequired = "Atualize o Ponte no outro computador para usar as opções de vídeo.";
    private volatile bool _playing;
    private FormWindowState _previousState;

    public bool ClipboardSync => _clipboard.Checked;
    public ViewerSession Session => _session;
    /// <summary>A primeira miniatura desta sessão foi gravada.</summary>
    public event Action? ThumbnailSaved;

    /// <param name="config">Sem configuração (sessão de demonstração) não há miniatura nem som.</param>
    public ViewerForm(ViewerSession session, AppConfig? config = null)
    {
        _session = session;
        _config = config;
        _profile = config?.Profile ?? StreamProfile.Default;
        var profile = config?.GetProfile(_profile) ?? StreamProfile.Original(_profile);
        _quality = profile.Quality;
        _videoOptions = profile.Video!;
        _videoStatusText = session.VideoStatus;
        _thumbnail = config?.ThumbnailPath(session.Key);
        _screen = Math.Max(0, Array.FindIndex(session.Screens, s => s.Primary));

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = $"{session.HostName} ({session.Target}) - Ponte {Updater.Current}";
        Icon = Ui.MakeIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(520, 360);

        _view = new RemoteView(session) { Dock = DockStyle.Fill };

        var bar = _bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Padding = new Padding(6, 2, 6, 2) };
        var screens = new ToolStripDropDownButton("Tela") { Enabled = session.Screens.Length > 1 };
        foreach (var s in session.Screens)
        {
            int index = s.Index;
            screens.DropDownItems.Add($"Tela {s.Index + 1}  ({s.Width} x {s.Height}){(s.Primary ? "  principal" : "")}", null, (_, _) => ShowScreen(index));
        }
        _eachWindow = new ToolStripMenuItem("Cada tela em uma janela")
        {
            Enabled = session.HostHasAllScreens,
            ToolTipText = session.HostHasAllScreens ? "Abre as outras telas em janelas separadas, que podem ter qualquer tamanho; esta janela fica com a tela atual"
                : "Atualize o Ponte no outro computador (1.16.0 ou mais nova) para abrir cada tela em uma janela",
        };
        _eachWindow.Click += (_, _) => { if (_allScreens) ShowScreen(_screen); else OpenScreenWindows(); };
        screens.DropDownItems.Add(new ToolStripSeparator());
        screens.DropDownItems.Add(_eachWindow);
        _simulate = new ToolStripMenuItem("Simular a disposição das telas de lá")
        {
            Enabled = session.HostHasAllScreens && session.Screens.Length > 1,
            Checked = config?.SimulateScreens == true,
            ToolTipText = "Cada tela numa janela, na mesma posição do Windows de lá, no maior tamanho que cabe neste monitor. " +
                "As janelas continuam podendo ser arrastadas e redimensionadas. Fica marcado para as próximas conexões.",
        };
        _simulate.Click += (_, _) => SimulateClicked();
        screens.DropDownItems.Add(_simulate);
        // Perfis: escolher um aplica a qualidade e o vídeo dele; mudar a qualidade ou o vídeo muda o perfil em uso.
        _profileButton = new ToolStripDropDownButton { ToolTipText = "Perfil de transmissão: o que for mudado nele fica guardado para as próximas conexões" };
        foreach (var (key, name) in StreamProfile.All)
        {
            var item = new ToolStripMenuItem(name);
            item.Click += (_, _) => SelectProfile(key);
            _profileItems.Add(key, item);
            _profileButton.DropDownItems.Add(item);
        }
        _restoreProfile = new ToolStripMenuItem();
        _restoreProfile.Click += (_, _) => RestoreProfile();
        _profileButton.DropDownItems.AddRange(new ToolStripItem[] { new ToolStripSeparator(), _restoreProfile });
        var quality = new ToolStripDropDownButton("Qualidade");
        _automaticQualityItem = new ToolStripMenuItem("Ajustar automaticamente à conexão") { CheckOnClick = true,
            Checked = config?.AutomaticQuality == true && session.HostHasManagement, Enabled = session.HostHasManagement };
        _automaticQualityItem.CheckedChanged += (_, _) =>
        {
            _automaticQuality.Reset();
            if (_config is not null) { _config.AutomaticQuality = _automaticQualityItem.Checked; _config.Save(); }
            Apply();
        };
        quality.DropDownItems.Add(_automaticQualityItem);
        quality.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (name, value) in new[] { ("Baixa (mais rápida)", 35), ("Média", 60), ("Alta", 85) })
        {
            var item = new ToolStripMenuItem(name);
            item.Click += (_, _) => { _quality = value; ProfileChanged(); Apply(); };
            _qualityItems.Add(value, item);
            quality.DropDownItems.Add(item);
        }
        _video = new ToolStripDropDownButton("Vídeo")
        {
            Enabled = session.HostHasVideo,
            ToolTipText = session.HostHasVideo ? "Preferências de vídeo; o status mostra o modo realmente usado" : VideoUpdateRequired,
        };
        var fps = new ToolStripMenuItem("FPS alvo");
        foreach (int value in new[] { 20, 30, 60, 120 })
        {
            var item = new ToolStripMenuItem(value == 20 ? "Atual (20 FPS)" : $"{value} FPS");
            item.Click += (_, _) => SetVideoOptions(_videoOptions with { Fps = value });
            _fpsItems.Add(value, item);
            fps.DropDownItems.Add(item);
        }
        var codec = new ToolStripMenuItem("Codec");
        foreach (var (name, value) in new[] { ("JPEG (Compatível)", "jpeg"), ("H.264 (GPU)", "h264"), ("H.265 (GPU)", "h265") })
        {
            var item = new ToolStripMenuItem(name);
            item.Click += (_, _) => SetVideoOptions(_videoOptions with { Codec = value });
            _codecItems.Add(value, item);
            codec.DropDownItems.Add(item);
        }
        _chroma444 = new ToolStripMenuItem("Chroma 4:4:4") { ToolTipText = "No modo compatível, usa PNG RGB sem perdas" };
        _chroma444.Click += (_, _) => SetVideoOptions(_videoOptions with { Chroma444 = !_videoOptions.Chroma444 });
        _ultraLowLatency = new ToolStripMenuItem("Ultra baixa latência") { ToolTipText = "Solicitar latência mínima, conforme o suporte do modo de vídeo" };
        _ultraLowLatency.Click += (_, _) => SetVideoOptions(_videoOptions with { UltraLowLatency = !_videoOptions.UltraLowLatency });
        _bitrate = new ToolStripMenuItem();
        _bitrate.Click += (_, _) => ShowVideoSettings();
        _video.DropDownItems.AddRange(new ToolStripItem[]
        {
            fps, codec, new ToolStripSeparator(), _chroma444, _ultraLowLatency, _bitrate,
            new ToolStripSeparator(), new ToolStripMenuItem("Configurações de vídeo...", null, (_, _) => ShowVideoSettings()),
        });
        UpdateVideoMenu();
        var keys = new ToolStripDropDownButton("Teclas");
        // Ctrl+Alt+Del de verdade não passa como teclas comuns: o outro lado pede ao serviço do Ponte de lá (1.17.0).
        var secure = new ToolStripMenuItem("Ctrl + Alt + Del") { Enabled = session.HostHasSecureAttention };
        if (!secure.Enabled) secure.ToolTipText = session.HostIsPhone ? "Não existe em celular." : "O outro computador precisa estar no Ponte 1.17.0 ou mais novo.";
        secure.Click += async (_, _) =>
        {
            _status.Text = "Ctrl+Alt+Del enviado.";
            await session.SendSecureAttentionAsync();
        };
        keys.DropDownItems.Add(secure);
        keys.DropDownItems.Add(new ToolStripSeparator());
        keys.DropDownItems.Add("Alt + Tab", null, (_, _) => _view.SendCombo(0x12, 0x09));
        keys.DropDownItems.Add("Tecla Windows", null, (_, _) => _view.SendCombo(0x5B));
        keys.DropDownItems.Add("Windows + R (Executar)", null, (_, _) => _view.SendCombo(0x5B, 0x52));
        keys.DropDownItems.Add("Windows + D (Área de trabalho)", null, (_, _) => _view.SendCombo(0x5B, 0x44));
        keys.DropDownItems.Add("Ctrl + Shift + Esc (Gerenciador de Tarefas)", null, (_, _) => _view.SendCombo(0x11, 0x10, 0x1B));
        keys.DropDownItems.Add("Print Screen", null, (_, _) => _view.SendCombo(0x2C));
        var restart = new ToolStripMenuItem("Reiniciar computador remoto...") { Enabled = session.HostHasManagement && !session.HostIsPhone };
        restart.Click += async (_, _) =>
        {
            string reconnect = session.CanReconnect ? "O Ponte tentará reconectar por até dez minutos." : "Será necessário conectar e autorizar novamente.";
            if (MessageBox.Show(this, $"Reiniciar {session.HostName}? Salve os arquivos abertos antes.\n\n{reconnect}\nPara voltar antes do login, o serviço do Ponte precisa estar instalado lá.", "Reiniciar computador remoto", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { await session.RequestRestartAsync(); }
            catch (Exception ex) when (ex is PonteException or IOException or OperationCanceledException) { _status.Text = ex.Message; }
        };
        keys.DropDownItems.Add(new ToolStripSeparator()); keys.DropDownItems.Add(restart);

        var full = _fullButton = new ToolStripButton("Tela cheia") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        full.Click += (_, _) => ToggleFullScreen(full);
        var file = new ToolStripButton("Enviar arquivo...") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        file.Click += async (_, _) => await SendFileAsync();
        var shot = new ToolStripButton("Salvar imagem...") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        shot.Click += (_, _) => SaveImage();
        _clipboard = new ToolStripButton("Área de transferência") { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true, Checked = true, ToolTipText = "Copiar e colar texto entre os dois computadores" };

        // Com os dois lados na mesma máquina, o som tocado aqui seria capturado de novo e repetido sem fim.
        bool sameMachine = config is not null && session.Fingerprint == Security.Fingerprint(config.Certificate);
        bool canPlay = config is not null && session.HostHasSound && !sameMachine;
        _sound = new ToolStripButton("Som")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true, Enabled = canPlay, Checked = canPlay && config!.PlaySound,
            ToolTipText = canPlay ? "Ouvir aqui o que o outro computador está tocando"
                : sameMachine ? "Sem som: o outro lado é este mesmo computador"
                : "O outro computador não transmite som (Ponte anterior à 1.5.0, ou o som foi desligado nas configurações de lá)",
        };
        _playing = _sound.Checked;
        _sound.CheckedChanged += (_, _) =>
        {
            _playing = _sound.Checked;
            if (!_playing) _player.Silence();
            if (_config is not null)
            {
                _config.PlaySound = _sound.Checked;
                _config.Save();
            }
            Apply();
        };

        var end = new ToolStripButton("Desconectar") { DisplayStyle = ToolStripItemDisplayStyle.Text, Alignment = ToolStripItemAlignment.Right, ForeColor = Ui.Danger };
        end.Click += (_, _) => Close();
        if (session.HostIsPhone)
        {
            // Um celular: os botões dele no lugar das combinações do Windows.
            ToolStripButton phone(string text, int vk) =>
                new(text, null, (_, _) => { _view.SendCombo(vk); _view.Focus(); }) { DisplayStyle = ToolStripItemDisplayStyle.Text };
            bar.Items.AddRange(new ToolStripItem[] { phone("◀ Voltar", PhoneKeys.Back), phone("● Início", PhoneKeys.Home), phone("▮▮ Apps", PhoneKeys.Recents),
                new ToolStripSeparator(), quality, new ToolStripSeparator(), full, shot, _clipboard, end });
        }
        else
        {
            bar.Items.AddRange(new ToolStripItem[] { screens, _profileButton, quality, _video, keys, new ToolStripSeparator(), full, file, shot, _clipboard, _sound, end });
        }

        var statusBar = _statusBar = new StatusStrip { SizingGrip = false, ShowItemToolTips = true };
        statusBar.Items.AddRange(new ToolStripItem[] { _status, _stats });
        _status.Text = $"Conectado a {session.HostName}{(session.HostVersion.Length > 0 ? $" (Ponte {session.HostVersion} lá)" : "")}";
        RefreshStatsText();

        Controls.Add(_view);
        Controls.Add(bar);
        Controls.Add(statusBar);

        session.FrameUpdated += _view.RequestRepaint;
        session.VideoStatusChanged += OnVideoStatusChanged;
        session.Closed += OnSessionClosed;
        session.ConnectionStatusChanged += text => Post(() => { _status.Text = text; _player.Silence(); });
        session.Reconnected += () => Post(() => { _automaticQuality.Reset(); _view.RequestRepaint(); Apply(); });
        session.FileReceived += path => Post(() => _status.Text = $"Arquivo recebido: {path}");
        session.Notice += text => Post(() => _status.Text = text);
        session.PointerChanged += payload => Post(() => _view.SetPointer(payload));
        session.SoundReceived += (samples, rate, channels) =>
        {
            if (_playing) _player.Play(samples, rate, channels);
        };
        _timer.Tick += (_, _) => UpdateStats();
        _timer.Start();
        _reveal.Tick += (_, _) => RevealBar();
        Shown += (_, _) =>
        {
            _view.Focus();
            session.Start();
            _started = true;
            _lastStatsAt = Stopwatch.GetTimestamp();
            Apply();
            if (_simulate.Checked && _simulate.Enabled) BeginInvoke(SimulateLayout);
        };
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Manda de novo as preferências desta sessão; chamado também quando as Configurações mudam.</summary>
    public void Apply()
    {
        if (!_started || _closing) return;
        var video = _videoOptions;
        // O H.264/H.265 só é pedido quando este computador já tem como decodificá-lo; até lá segue o compatível.
        if (video.Codec != "jpeg" && !VideoRuntime.Present)
        {
            video = video with { Codec = "jpeg" };
            PrepareVideoRuntime();
        }
        int quality = _quality;
        if (_automaticQualityItem.Checked) (quality, video) = _automaticQuality.Apply(quality, video);
        _ = _session.SendSettingsAsync(_allScreens ? ScreenInfo.All : _screen, quality, _playing, _session.HostHasVideo ? video : null, _config?.ViewerIdleMinutes * 60);
    }

    // ---------- cada tela em uma janela ----------

    private readonly ToolStripMenuItem _eachWindow, _simulate;
    private readonly ToolStripButton _fullButton;
    private readonly List<ScreenWindow> _screenWindows = new();
    private readonly Dictionary<ScreenWindow, int> _windowScreen = new();

    private void SimulateClicked()
    {
        _simulate.Checked = !_simulate.Checked;
        if (_config is not null)
        {
            _config.SimulateScreens = _simulate.Checked;
            SaveConfig();
        }
        if (_simulate.Checked) SimulateLayout();
    }

    /// <summary>
    /// "Simular a disposição das telas de lá": cada tela numa janela, montadas como no Windows de lá (uma em cima da
    /// outra, lado a lado...), na maior escala que cabe no monitor daqui onde esta janela está. Depois disso cada janela
    /// continua livre para ser arrastada e redimensionada.
    /// </summary>
    private void SimulateLayout()
    {
        var screens = _session.Screens;
        if (_closing || !_session.HostHasAllScreens || screens.Length < 2) return;
        if (_fullScreen) ToggleFullScreen(_fullButton);
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        if (!_allScreens) OpenScreenWindows();
        if (!_allScreens) return;

        var area = Screen.FromControl(this).WorkingArea;
        int left = screens.Min(s => s.X), top = screens.Min(s => s.Y);
        int width = screens.Max(s => s.X + s.Width) - left, height = screens.Max(s => s.Y + s.Height) - top;
        // Cada janela tem borda e barra de título: quantas ficam acima e à esquerda de cada tela entra na conta.
        var frame = Size - ClientSize;
        int RowsAbove(ScreenInfo s) => screens.Where(o => o.Y + o.Height <= s.Y).Select(o => o.Y).Distinct().Count();
        int ColumnsLeft(ScreenInfo s) => screens.Where(o => o.X + o.Width <= s.X).Select(o => o.X).Distinct().Count();
        int rows = screens.Max(RowsAbove) + 1, columns = screens.Max(ColumnsLeft) + 1;
        double scale = Math.Min((area.Width - columns * frame.Width) / (double)width, (area.Height - rows * frame.Height) / (double)height);
        if (scale <= 0) return;
        int originX = area.X + (int)((area.Width - (width * scale + columns * frame.Width)) / 2);
        int originY = area.Y + (int)((area.Height - (height * scale + rows * frame.Height)) / 2);

        foreach (var s in screens)
        {
            Form? window = s.Index == _screen ? this : _windowScreen.FirstOrDefault(p => p.Value == s.Index).Key;
            if (window is null || window.IsDisposed) continue;
            if (window.WindowState != FormWindowState.Normal) window.WindowState = FormWindowState.Normal;
            window.ClientSize = new Size(Math.Max(1, (int)Math.Round(s.Width * scale)), Math.Max(1, (int)Math.Round(s.Height * scale)));
            window.Location = new Point(originX + (int)Math.Round((s.X - left) * scale) + ColumnsLeft(s) * frame.Width,
                originY + (int)Math.Round((s.Y - top) * scale) + RowsAbove(s) * frame.Height);
        }
        _view.Focus();
    }
    // Com todas as telas numa imagem só, esta janela mostra a tela _screen e cada uma das outras ganha uma ScreenWindow.
    private bool _allScreens;

    private void ShowScreen(int index)
    {
        CloseScreenWindows();
        _screen = index;
        Apply();
    }

    private void OpenScreenWindows()
    {
        var screens = _session.Screens;
        if (_allScreens || _closing || !_session.HostHasAllScreens || screens.Length < 2) return;
        // A imagem que chega é a área de trabalho inteira de lá; cada janela mostra o retângulo de uma tela dentro dela.
        int left = screens.Min(s => s.X), top = screens.Min(s => s.Y);
        int width = screens.Max(s => s.X + s.Width) - left, height = screens.Max(s => s.Y + s.Height) - top;
        double aspect = width / (double)height;
        RectangleF Crop(ScreenInfo s) => new((s.X - left) / (float)width, (s.Y - top) / (float)height, s.Width / (float)width, s.Height / (float)height);

        _screen = Math.Clamp(_screen, 0, screens.Length - 1);
        _allScreens = true;
        _eachWindow.Checked = true;
        _view.SetCrop(Crop(screens[_screen]), aspect);
        Text = $"{_session.HostName} ({_session.Target}) - Tela {_screen + 1} - Ponte {Updater.Current}";
        // Esta janela fica igual às das outras telas: só a imagem, no formato da tela. Os comandos aparecem com o mouse no
        // alto da janela, como na tela cheia.
        // Do tamanho de uma tela (pequena como as outras janelas, se for preciso para simular a disposição de lá).
        MinimumSize = new Size(160, 120);
        if (!_fullScreen)
        {
            _sizeBeforeWindows = WindowState == FormWindowState.Normal ? ClientSize : null;
            SetBarsFloating(true);
            if (WindowState == FormWindowState.Normal)
            {
                var current = screens[_screen];
                ClientSize = RemoteView.Fit(ClientSize, current.Width, current.Height).Size;
            }
        }

        // Com mais de um monitor aqui, cada janela vai para um monitor que esta não ocupa; com um só, ficam em cascata.
        var spare = Screen.AllScreens.Where(s => !s.Bounds.IntersectsWith(Bounds)).ToList();
        int cascade = 0;
        foreach (var s in screens.Where(s => s.Index != _screen))
        {
            var window = new ScreenWindow(_session, Crop(s), aspect, $"{_session.HostName} - Tela {s.Index + 1} ({s.Width} x {s.Height}) - Ponte");
            var area = spare.Count > 0 ? spare[0].WorkingArea : Screen.FromControl(this).WorkingArea;
            if (spare.Count > 0) spare.RemoveAt(0);
            var client = RemoteView.Fit(new Size(area.Width * 7 / 10, area.Height * 7 / 10), s.Width, s.Height).Size;
            window.StartPosition = FormStartPosition.Manual;
            window.ClientSize = client;
            window.Location = new Point(area.X + (area.Width - window.Width) / 2 + cascade * 32, area.Y + (area.Height - window.Height) / 2 + cascade * 32);
            if (area == Screen.FromControl(this).WorkingArea) cascade++;
            _windowScreen[window] = s.Index;
            window.FormClosed += (_, _) =>
            {
                _screenWindows.Remove(window);
                _windowScreen.Remove(window);
                // Fechou a última: volta a mandar só a tela desta janela, que é menos imagem pela rede.
                if (_allScreens && _screenWindows.Count == 0 && !_closing) ShowScreen(_screen);
            };
            _screenWindows.Add(window);
            window.Show();
        }
        Apply();
    }

    private Size? _sizeBeforeWindows;

    /// <summary>
    /// Barras flutuando: sem barra de estado, e a de comandos solta sobre a imagem, escondida até o mouse encostar no
    /// alto. Vale na tela cheia e em "Cada tela em uma janela".
    /// </summary>
    private void SetBarsFloating(bool floating)
    {
        SuspendLayout();
        if (floating)
        {
            _statusBar.Visible = false;
            _bar.Visible = false;
            _bar.Dock = DockStyle.None;
            // Solta do topo, a barra precisa dizer como acompanha a janela. Sem isso o Windows Forms falhava ao
            // reorganizar a janela (NullReferenceException a cada atualização da barra de estado, em tela cheia).
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _bar.SetBounds(0, 0, ClientSize.Width, _bar.PreferredSize.Height);
            _bar.BringToFront();
            _reveal.Start();
        }
        else
        {
            _reveal.Stop();
            // De volta ao fundo da pilha, a barra é encaixada antes da imagem e não fica por cima dela.
            _bar.SendToBack();
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _bar.Dock = DockStyle.Top;
            _bar.Visible = true;
            _statusBar.Visible = true;
        }
        ResumeLayout(true);
    }

    private void CloseScreenWindows()
    {
        if (!_allScreens) return;
        _allScreens = false;
        _eachWindow.Checked = false;
        if (!_closing)
        {
            _view.ClearCrop();
            Text = $"{_session.HostName} ({_session.Target}) - Ponte {Updater.Current}";
            if (!_fullScreen)
            {
                SetBarsFloating(false);
                MinimumSize = new Size(520, 360);
                if (_sizeBeforeWindows is { } size && WindowState == FormWindowState.Normal) ClientSize = size;
            }
            _sizeBeforeWindows = null;
        }
        foreach (var window in _screenWindows.ToArray()) window.Close();
        _screenWindows.Clear();
    }

    private void PrepareVideoRuntime()
    {
        if (_preparingRuntime || _config is null || !_session.HostHasVideo) return;
        _preparingRuntime = true;
        string name = VideoOptions.CodecName(_videoOptions.Codec);
        _localVideoNote = $"Baixando o componente de vídeo para o {name} (só na primeira vez)...";
        RefreshStatsText();
        var config = _config;
        _ = Task.Run(async () =>
        {
            string note = "";
            try
            {
                await VideoRuntime.EnsureAvailableAsync(config, percent => Post(() =>
                {
                    _localVideoNote = $"Baixando o componente de vídeo para o {name}: {percent}%";
                    RefreshStatsText();
                }), _closed.Token);
            }
            catch (PonteException ex) { note = $"{name} indisponível neste computador: {ex.Message}"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { note = $"{name} indisponível neste computador: {ex.Message}"; }
            catch (OperationCanceledException) { return; }
            Post(() =>
            {
                _preparingRuntime = false;
                _localVideoNote = note;
                RefreshStatsText();
                if (note.Length == 0) Apply();
            });
        });
    }

    private void SetVideoOptions(VideoOptions options)
    {
        if (!_session.HostHasVideo || _closing) return;
        _videoOptions = options.Normalize();
        ProfileChanged();
        Apply();
    }

    private void SelectProfile(string key)
    {
        if (_closing) return;
        _profile = key;
        var profile = _config?.GetProfile(key) ?? StreamProfile.Original(key);
        _quality = profile.Quality;
        _videoOptions = profile.Video!;
        if (_config is not null) _config.Profile = key;
        SaveConfig();
        UpdateVideoMenu();
        Apply();
    }

    private void RestoreProfile()
    {
        if (_closing) return;
        _config?.Profiles.Remove(_profile);
        SelectProfile(_profile);
    }

    // A qualidade ou o vídeo mudou: vira o novo conteúdo do perfil em uso, para esta e as próximas conexões.
    private void ProfileChanged()
    {
        if (_config is not null)
        {
            _config.Profile = _profile;
            _config.Profiles[_profile] = new StreamProfile(_quality, _videoOptions);
        }
        SaveConfig();
        UpdateVideoMenu();
    }

    private void SaveConfig()
    {
        if (_config is null) return;
        try { _config.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Os ajustes valem nesta sessão, mas não foi possível salvá-los para as próximas conexões.",
                "Perfil - Ponte", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UpdateVideoMenu()
    {
        string profileName = StreamProfile.NameOf(_profile);
        _profileButton.Text = $"Perfil: {profileName}";
        foreach (var (key, item) in _profileItems) item.Checked = key == _profile;
        bool changed = _config?.Profiles.ContainsKey(_profile) == true && _config.GetProfile(_profile) != StreamProfile.Original(_profile).Normalize();
        _restoreProfile.Text = $"Voltar \"{profileName}\" ao original";
        _restoreProfile.Enabled = changed;
        foreach (var (value, item) in _qualityItems) item.Checked = _quality == value;
        foreach (var (value, item) in _fpsItems) item.Checked = _videoOptions.Fps == value;
        foreach (var (value, item) in _codecItems) item.Checked = _videoOptions.Codec == value;
        _chroma444.Checked = _videoOptions.Chroma444;
        _ultraLowLatency.Checked = _videoOptions.UltraLowLatency;
        _bitrate.Text = $"Bitrate: {_videoOptions.BitrateMbps} Mbps...";
    }

    private void ShowVideoSettings()
    {
        if (!_session.HostHasVideo || _closing) return;
        using var dialog = new VideoSettingsDialog(_videoOptions, _videoStatusText);
        _videoDialog = dialog;
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK) SetVideoOptions(dialog.Options);
        }
        finally { _videoDialog = null; }
        _view.Focus();
    }

    private void OnVideoStatusChanged(string status) => Post(() =>
    {
        if (_closing) return;
        _videoStatusText = status;
        RefreshStatsText();
        _videoDialog?.SetVideoStatus(status);
    });

    private void RefreshStatsText()
    {
        string video = _localVideoNote.Length > 0 ? _localVideoNote
            : string.IsNullOrWhiteSpace(_videoStatusText) ? "Aguardando o status de vídeo" : _videoStatusText;
        if (!_session.HostHasVideo) video = VideoUpdateRequired;
        _stats.Text = $"{_performanceText}   ·   {video}";
        _stats.ToolTipText = $"{_performanceText}\n{video}";
    }

    private void UpdateStats()
    {
        _ = _session.PingAsync();
        // A miniatura da lista de computadores: logo que a tela aparece e, depois, de minuto em minuto.
        if (_thumbnail is not null && (!_hasThumbnail || ++_seconds % 60 == 0) && _session.SaveThumbnail(_thumbnail) && !_hasThumbnail)
        {
            _hasThumbnail = true;
            ThumbnailSaved?.Invoke();
        }
        long frames = Interlocked.Read(ref _session.Frames), bytes = _session.BytesReceived, paints = _view.Paints, paintTicks = _view.PaintTicks;
        long now = Stopwatch.GetTimestamp();
        double elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(_lastStatsAt, now).TotalSeconds);
        string size;
        lock (_session.CanvasLock) size = _session.Canvas is { } c ? $"{c.Width} x {c.Height}   " : "";
        double drawn = (paints - _lastPaints) / elapsed;
        double paintMs = paints > _lastPaints ? (paintTicks - _lastPaintTicks) * 1000.0 / Stopwatch.Frequency / (paints - _lastPaints) : 0;
        _performanceText = $"{size}{(frames - _lastFrames) / elapsed:N0} FPS recebidos, {drawn:N0} na tela   {(bytes - _lastBytes) * 8 / (1000.0 * elapsed):N0} kbit/s";
        PaintMilliseconds = paintMs;
        if (++_qualityTicks % 2 == 0 && _automaticQualityItem.Checked && !_session.Reconnecting &&
            _automaticQuality.Observe(_session.RoundTripMilliseconds, paintMs)) Apply();
        _lastPaints = paints;
        _lastPaintTicks = paintTicks;
        _videoStatusText = _session.VideoStatus;
        RefreshStatsText();
        _lastFrames = frames;
        _lastBytes = bytes;
        _lastStatsAt = now;
    }

    // Tela cheia de verdade: sem borda, sem barra de estado e sem barra de comandos ocupando lugar. A barra
    // de comandos passa a flutuar sobre a imagem e só aparece com o mouse no alto da tela.
    private void ToggleFullScreen(ToolStripButton button)
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _previousState = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            SetBarsFloating(true);
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _previousState;
            // Em "Cada tela em uma janela" as barras continuam flutuando.
            SetBarsFloating(_allScreens);
        }
        button.Text = _fullScreen ? "Sair da tela cheia" : "Tela cheia";
        if (_fullScreen) _status.Text = "Tela cheia: leve o mouse ao alto da tela para ver os comandos.";
        _view.Focus();
    }

    private void RevealBar()
    {
        if (!(_fullScreen || _allScreens) || !IsHandleCreated) return;
        var area = RectangleToScreen(ClientRectangle);
        var mouse = Cursor.Position;
        bool menuOpen = _bar.Items.OfType<ToolStripDropDownItem>().Any(i => i.DropDown.Visible);
        // Na janela comum, uma faixa um pouco maior no alto: logo acima está a barra de título.
        int edge = _fullScreen ? 2 : LogicalToDeviceUnits(6);
        if (!_bar.Visible && area.Contains(mouse) && mouse.Y <= area.Top + edge)
        {
            _bar.SetBounds(0, 0, ClientSize.Width, _bar.PreferredSize.Height);
            _bar.Visible = true;
            _bar.BringToFront();
        }
        else if (_bar.Visible && !menuOpen && mouse.Y > area.Top + _bar.Height + LogicalToDeviceUnits(40))
        {
            _bar.Visible = false;
            _view.Focus();
        }
    }

    private async Task SendFileAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Enviar arquivo para o outro computador" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string name = Path.GetFileName(dialog.FileName);
        try
        {
            var progress = new Progress<int>(p => _status.Text = $"Enviando {name}: {p} %");
            await _session.SendFileAsync(dialog.FileName, progress);
            _status.Text = $"{name} enviado. No outro computador ele fica em Downloads\\Ponte.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            _status.Text = $"Não foi possível enviar {name}.";
        }
        _view.Focus();
    }

    private void SaveImage()
    {
        using var dialog = new SaveFileDialog { Filter = "Imagem PNG (*.png)|*.png", FileName = $"{_session.HostName}-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            lock (_session.CanvasLock) _session.Canvas?.Save(dialog.FileName, ImageFormat.Png);
            _status.Text = $"Imagem salva em {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            _status.Text = "Não foi possível salvar a imagem.";
        }
    }

    private void OnSessionClosed(string reason) => Post(() =>
    {
        if (_closing) return;
        _closing = true;
        if (reason.Length > 0) MessageBox.Show(this, reason, "Ponte", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    });

    /// <summary>Grava em arquivo o que a janela está mostrando (usado pelo modo de verificação).</summary>
    public void SaveWindowImage(string path) => Ui.SaveImage(this, path);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        CloseScreenWindows();
        _closed.Cancel();
        _timer.Dispose();
        _reveal.Dispose();
        _playing = false;
        _player.Dispose();
        if (_thumbnail is not null) _session.SaveThumbnail(_thumbnail);
        _session.FrameUpdated -= _view.RequestRepaint;
        _session.VideoStatusChanged -= OnVideoStatusChanged;
        _ = _session.DisposeAsync().AsTask();
        base.OnFormClosed(e);
    }
}

/// <summary>
/// Uma das outras telas do computador acessado, em "Cada tela em uma janela": só a imagem daquela tela, numa janela
/// de qualquer tamanho. Mouse e teclado vão pela mesma sessão da janela principal.
/// </summary>
internal sealed class ScreenWindow : Form
{
    private readonly ViewerSession _session;
    private readonly RemoteView _view;

    public ScreenWindow(ViewerSession session, RectangleF crop, double wholeAspect, string title)
    {
        _session = session;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = title;
        Icon = Ui.MakeIcon();
        MinimumSize = new Size(160, 120);
        _view = new RemoteView(session) { Dock = DockStyle.Fill };
        _view.SetCrop(crop, wholeAspect);
        Controls.Add(_view);
        session.FrameUpdated += _view.RequestRepaint;
        session.PointerChanged += OnPointerChanged;
        Shown += (_, _) => _view.Focus();
    }

    private void OnPointerChanged(byte[] payload)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => _view.SetPointer(payload)); }
        catch (InvalidOperationException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _session.FrameUpdated -= _view.RequestRepaint;
        _session.PointerChanged -= OnPointerChanged;
        base.OnFormClosed(e);
    }
}
