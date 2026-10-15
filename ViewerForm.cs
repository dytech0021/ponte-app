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

    public RemoteView(ViewerSession session)
    {
        _session = session;
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
    public static (int X, int Y) ToRemote(Point p, Size view, int w, int h)
    {
        var dest = Fit(view, w, h);
        // Primeiro o pixel de lá que está debaixo do ponteiro daqui (pelo centro do pixel daqui); depois a
        // escala de 0 a 65535 da mensagem, arredondada, que o outro lado desfaz sem perder o pixel.
        int px = Math.Clamp((int)Math.Floor((p.X - dest.X + 0.5) * w / dest.Width), 0, w - 1);
        int py = Math.Clamp((int)Math.Floor((p.Y - dest.Y + 0.5) * h / dest.Height), 0, h - 1);
        return ((int)Math.Round(px * 65535.0 / Math.Max(1, w - 1)), (int)Math.Round(py * 65535.0 / Math.Max(1, h - 1)));
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
            var dest = Fit(canvas.Width, canvas.Height);
            e.Graphics.InterpolationMode = dest.Width == canvas.Width ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(canvas, dest);
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
        (nx, ny) = ToRemote(p, ClientSize, w, h);
        return true;
    }

    private void SendMouse(Point p, int action, int wheel = 0)
    {
        if (Map(p, out int nx, out int ny)) _ = _session.SendMouseAsync(nx, ny, action, wheel);
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
                bool up = msg.Msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
                bool extended = (((long)msg.LParam >> 24) & 1) != 0;
                if (up) _keysDown.Remove(vk);
                else _keysDown.Add(vk);
                _ = _session.SendKeyAsync(vk, up, extended);
                return true;
            case Native.WM_CHAR or Native.WM_DEADCHAR or Native.WM_SYSCHAR or Native.WM_SYSDEADCHAR:
                return true;
        }
        return base.PreProcessMessage(ref msg);
    }

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
            screens.DropDownItems.Add($"Tela {s.Index + 1}  ({s.Width} x {s.Height}){(s.Primary ? "  principal" : "")}", null, (_, _) => { _screen = index; Apply(); });
        }
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
        keys.DropDownItems.Add("Alt + Tab", null, (_, _) => _view.SendCombo(0x12, 0x09));
        keys.DropDownItems.Add("Tecla Windows", null, (_, _) => _view.SendCombo(0x5B));
        keys.DropDownItems.Add("Windows + R (Executar)", null, (_, _) => _view.SendCombo(0x5B, 0x52));
        keys.DropDownItems.Add("Windows + D (Área de trabalho)", null, (_, _) => _view.SendCombo(0x5B, 0x44));
        keys.DropDownItems.Add("Ctrl + Shift + Esc (Gerenciador de Tarefas)", null, (_, _) => _view.SendCombo(0x11, 0x10, 0x1B));
        keys.DropDownItems.Add("Print Screen", null, (_, _) => _view.SendCombo(0x2C));

        var full = new ToolStripButton("Tela cheia") { DisplayStyle = ToolStripItemDisplayStyle.Text };
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
        bar.Items.AddRange(new ToolStripItem[] { screens, _profileButton, quality, _video, keys, new ToolStripSeparator(), full, file, shot, _clipboard, _sound, end });

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
        };
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void Apply()
    {
        if (!_started || _closing) return;
        var video = _videoOptions;
        // O H.264/H.265 só é pedido quando este computador já tem como decodificá-lo; até lá segue o compatível.
        if (video.Codec != "jpeg" && !VideoRuntime.Present)
        {
            video = video with { Codec = "jpeg" };
            PrepareVideoRuntime();
        }
        _ = _session.SendSettingsAsync(_screen, _quality, _playing, _session.HostHasVideo ? video : null);
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
        SuspendLayout();
        if (_fullScreen)
        {
            _previousState = WindowState;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
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
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _previousState;
            // De volta ao fundo da pilha, a barra é encaixada antes da imagem e não fica por cima dela.
            _bar.SendToBack();
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _bar.Dock = DockStyle.Top;
            _bar.Visible = true;
            _statusBar.Visible = true;
        }
        ResumeLayout(true);
        button.Text = _fullScreen ? "Sair da tela cheia" : "Tela cheia";
        if (_fullScreen) _status.Text = "Tela cheia: leve o mouse ao alto da tela para ver os comandos.";
        _view.Focus();
    }

    private void RevealBar()
    {
        if (!_fullScreen || !IsHandleCreated) return;
        var area = RectangleToScreen(ClientRectangle);
        var mouse = Cursor.Position;
        bool menuOpen = _bar.Items.OfType<ToolStripDropDownItem>().Any(i => i.DropDown.Visible);
        if (!_bar.Visible && area.Contains(mouse) && mouse.Y <= area.Top + 2)
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
