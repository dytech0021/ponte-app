namespace Ponte;

/// <summary>
/// Transmitir a tela: quem abre o link assiste ao vivo, com o som, sem controle nenhum. A transmissão dura
/// enquanto esta janela estiver aberta (pode ficar minimizada).
/// </summary>
internal sealed class BroadcastDialog : Form
{
    private static readonly (string Name, int Height, int Fps)[] Qualities =
    {
        ("1080p · 60 FPS (recomendado para jogos)", 1080, 60),
        ("1080p · 30 FPS", 1080, 30),
        ("720p · 60 FPS (internet mais lenta)", 720, 60),
        ("720p · 30 FPS", 720, 30),
    };

    private readonly HostService _host;
    private readonly AppConfig _config;
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 98), Size = new Size(300, 23) };
    private readonly ComboBox _screen = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 132), Size = new Size(300, 23) };
    private readonly CheckBox _sound = new() { Text = "Transmitir o som do computador", Location = new Point(16, 166), Size = new Size(400, 24) };
    private readonly Button _start, _copy;
    private readonly Label _state, _upload;
    private readonly TextBox _link = new() { Location = new Point(16, 262), Size = new Size(392, 23), ReadOnly = true };
    private readonly PictureBox _qr = new() { Location = new Point(424, 196), Size = new Size(136, 136), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Visible = false };
    private Broadcast? _broadcast;
    private CancellationTokenSource? _starting;
    private bool _closingNow;

    public BroadcastDialog(HostService host, AppConfig config)
    {
        _host = host;
        _config = config;
        Ui.Prepare(this, "Transmitir a tela - Ponte", 576, 400);
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        var title = Ui.Text(this, "Transmita a tela para quem tiver o link", 16, 14, 544, 24);
        title.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        title.ForeColor = Ui.Accent;
        Ui.Text(this, "Quem abrir o link assiste à tela deste computador ao vivo, com o som, mas não controla nada. Feito para " +
                      "jogos. Abre no navegador de qualquer celular ou computador, sem instalar nada.", 16, 44, 544, 44);

        Ui.Text(this, "Qualidade", 16, 101, 90);
        foreach (var q in Qualities) _quality.Items.Add(q.Name);
        var saved = config.Broadcast;
        _quality.SelectedIndex = Math.Max(0, Array.FindIndex(Qualities, q => q.Height == saved.Height && q.Fps == saved.Fps));
        Controls.Add(_quality);

        Ui.Text(this, "Monitor", 16, 135, 90);
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
            _screen.Items.Add($"Monitor {i + 1}{(screens[i].Primary ? " (principal)" : "")} · {screens[i].Bounds.Width}×{screens[i].Bounds.Height}");
        _screen.SelectedIndex = Math.Max(0, Array.FindIndex(screens, s => s.Primary));
        _screen.Enabled = screens.Length > 1;
        Controls.Add(_screen);

        _sound.Checked = saved.Sound;
        Controls.Add(_sound);

        _start = Ui.Button(this, "Começar a transmissão", 16, 196, 200);
        _start.Height = 32;
        _start.Click += async (_, _) => await ToggleAsync();
        AcceptButton = _start;
        _state = Ui.Text(this, "", 16, 236, 392, 22);
        _state.Font = new Font(Font, FontStyle.Bold);
        Controls.Add(_link);
        _link.Visible = false;
        _copy = Ui.Button(this, "Copiar link", 16, 294, 120);
        _copy.Visible = false;
        _copy.Click += (_, _) =>
        {
            if (_link.Text.Length == 0) return;
            try { Clipboard.SetText(_link.Text); _state.Text = "Link copiado. Mande para quem vai assistir."; }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        Controls.Add(_qr);
        _upload = Ui.Text(this, "", 16, 340, 544, 40);
        _upload.ForeColor = SystemColors.GrayText;
        var close = Ui.Button(this, "Fechar", 470, 360, 90);
        close.Click += (_, _) => Close();
        _quality.SelectedIndexChanged += (_, _) => ShowUpload();
        ShowUpload();
    }

    private BroadcastOptions Chosen()
    {
        var q = Qualities[Math.Max(0, _quality.SelectedIndex)];
        return new BroadcastOptions(q.Height, q.Fps, _sound.Checked, _screen.SelectedIndex);
    }

    private void ShowUpload()
    {
        var o = Chosen();
        double mbits = (o.VideoKbps + (o.Sound ? 128 : 0)) / 1000.0 * 1.2;
        _upload.Text = $"Precisa de uns {mbits:0} Mbit/s de envio na internet deste computador ({o.Name}). " +
                       "Se a imagem travar para quem assiste, escolha 720p.";
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private async Task ToggleAsync()
    {
        if (_broadcast is not null || _starting is not null)
        {
            await StopAsync();
            return;
        }
        var options = Chosen();
        _config.Broadcast = options with { Screen = -1 };
        try { _config.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        SetOptionsEnabled(false);
        _start.Text = "Cancelar";
        _state.ForeColor = SystemColors.ControlText;
        _state.Text = "Preparando...";
        var starting = _starting = new CancellationTokenSource();
        try
        {
            _broadcast = await Broadcast.StartAsync(_host, _config, options, text => Post(() => { if (_starting == starting) _state.Text = text; }), starting.Token);
        }
        catch (Exception ex) when (ex is PonteException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            _starting = null;
            if (IsDisposed) return;
            SetOptionsEnabled(true);
            _start.Text = "Começar a transmissão";
            _state.ForeColor = ex is OperationCanceledException ? SystemColors.ControlText : Ui.Danger;
            _state.Text = ex is OperationCanceledException ? "" : ex.Message;
            return;
        }
        _starting = null;
        if (IsDisposed || _closingNow)
        {
            await _broadcast.DisposeAsync();
            return;
        }
        _broadcast.Changed += () => Post(ShowState);
        _start.Text = "Parar a transmissão";
        _link.Text = _broadcast.Link;
        _link.Visible = _copy.Visible = _qr.Visible = true;
        _qr.Image?.Dispose();
        _qr.Image = AndroidAppDialog.Render(QrCode.Encode(_broadcast.Link));
        ShowState();
    }

    private void ShowState()
    {
        if (_broadcast is not { } b) return;
        string viewers = b.Viewers switch
        {
            < 0 => "",
            0 => " · ninguém assistindo ainda",
            1 => " · 1 pessoa assistindo",
            int n => $" · {n} pessoas assistindo",
        };
        _state.ForeColor = b.Live ? Ui.Accent : SystemColors.ControlText;
        _state.Text = b.Live ? $"● Ao vivo{viewers}" : b.Status;
        Text = b.Live ? $"Ao vivo{viewers} - Ponte" : "Transmitir a tela - Ponte";
    }

    private async Task StopAsync()
    {
        _starting?.Cancel();
        var broadcast = _broadcast;
        _broadcast = null;
        if (broadcast is not null)
        {
            _start.Enabled = false;
            _state.Text = "Encerrando...";
            await broadcast.DisposeAsync();
        }
        if (IsDisposed) return;
        _start.Enabled = true;
        _start.Text = "Começar a transmissão";
        _state.ForeColor = SystemColors.ControlText;
        _state.Text = broadcast is null ? "" : "Transmissão encerrada. O link deixou de valer.";
        _link.Visible = _copy.Visible = _qr.Visible = false;
        Text = "Transmitir a tela - Ponte";
        SetOptionsEnabled(true);
    }

    private void SetOptionsEnabled(bool enabled)
    {
        _quality.Enabled = _sound.Enabled = enabled;
        _screen.Enabled = enabled && Screen.AllScreens.Length > 1;
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (_closingNow || (_broadcast is null && _starting is null)) return;
        if (e.CloseReason is not CloseReason.UserClosing)
        {
            // Fechando o Ponte ou o Windows: encerra já, sem perguntar, para o FFmpeg não ficar transmitindo sozinho.
            _closingNow = true;
            _starting?.Cancel();
            var running = _broadcast;
            _broadcast = null;
            if (running is not null) Task.Run(() => running.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4));
            return;
        }
        var answer = MessageBox.Show(this, "A transmissão acontece enquanto esta janela estiver aberta. Parar a transmissão agora?",
            "Ponte", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _closingNow = true;
        await StopAsync();
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
