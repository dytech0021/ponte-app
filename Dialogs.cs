using System.Drawing.Drawing2D;

namespace Ponte;

internal static class Ui
{
    public static bool Dark => Application.IsDarkModeEnabled;

    // No modo noturno as cores de destaque clareiam, para manter o contraste sobre o fundo escuro.
    public static Color Accent => Dark ? Color.FromArgb(79, 195, 212) : Color.FromArgb(0, 105, 120);
    public static Color Danger => Dark ? Color.FromArgb(255, 138, 128) : Color.Firebrick;

    /// <summary>Escolhe o tema de todas as janelas. Precisa ser chamado antes de a primeira janela existir.</summary>
    /// <param name="theme">"claro", "escuro" (o modo noturno) ou qualquer outra coisa para acompanhar o Windows.</param>
    public static void ApplyTheme(string theme) => Application.SetColorMode(theme switch
    {
        "escuro" => SystemColorMode.Dark,
        "claro" => SystemColorMode.Classic,
        _ => SystemColorMode.System,
    });

    public static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var deck = new Pen(Accent, 4);
            g.DrawArc(deck, 3, 9, 26, 26, 200, 140);
            g.DrawLine(deck, 2, 23, 30, 23);
            using var pillar = new Pen(Accent, 3);
            g.DrawLine(pillar, 9, 15, 9, 29);
            g.DrawLine(pillar, 23, 15, 23, 29);
        }
        IntPtr handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone();
        Native.DestroyIcon(handle);
        return icon;
    }

    public static void Prepare(Form form, string title, int width, int height)
    {
        form.AutoScaleDimensions = new SizeF(7F, 15F);
        form.AutoScaleMode = AutoScaleMode.Font;
        form.Text = title;
        form.Icon = MakeIcon();
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = form.MinimizeBox = false;
        form.StartPosition = FormStartPosition.CenterParent;
        form.ClientSize = new Size(width, height);
    }

    public static Label Text(Control parent, string text, int x, int y, int w, int h = 20)
    {
        var label = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h) };
        parent.Controls.Add(label);
        return label;
    }

    public static Button Button(Control parent, string text, int x, int y, int w, DialogResult result = DialogResult.None)
    {
        var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 28), DialogResult = result };
        parent.Controls.Add(button);
        return button;
    }

    /// <summary>Grava em arquivo o que a janela está mostrando (usado pelos modos de verificação).</summary>
    public static void SaveImage(Form form, string path)
    {
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

/// <summary>Pedido de conexão que chega: só entra quem a pessoa na frente do computador aceitar.</summary>
internal sealed class AcceptDialog : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly CheckBox _always = new() { Text = "Sempre permitir este computador, sem perguntar de novo" };
    private int _seconds = 30;

    /// <summary>A pessoa escolheu autorizar este computador também para as próximas vezes.</summary>
    public bool Always => _always.Checked;

    public AcceptDialog(IncomingRequest request)
    {
        // Só dá para autorizar de vez quem provou ter uma chave própria; versões antigas do Ponte não mandam.
        int extra = request.CanRemember ? 74 : 0;
        Ui.Prepare(this, "Pedido de conexão - Ponte", 400, 170 + extra);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Ui.Text(this, $"{request.Name} quer ver e controlar este computador.", 16, 16, 368, 22).Font = new Font(Font, FontStyle.Bold);
        Ui.Text(this, $"Usuário: {request.User}", 16, 46, 368);
        Ui.Text(this, $"Origem: {request.Remote}", 16, 68, 368);
        if (request.CanRemember)
        {
            Ui.Text(this, $"Endereço dele: {Security.FormatId(request.Id)}", 16, 90, 368);
            _always.Location = new Point(16, 116);
            _always.Size = new Size(368, 24);
            Controls.Add(_always);
            Ui.Text(this, "Marcado, ele entra a qualquer hora, mesmo sem ninguém aqui.", 34, 140, 350).ForeColor = SystemColors.GrayText;
        }
        var countdown = Ui.Text(this, "", 16, 96 + extra, 368);
        countdown.ForeColor = SystemColors.GrayText;

        var accept = Ui.Button(this, "Aceitar", 196, 128 + extra, 90, DialogResult.Yes);
        var reject = Ui.Button(this, "Recusar", 294, 128 + extra, 90, DialogResult.No);
        AcceptButton = null;
        CancelButton = reject;
        ActiveControl = reject;

        void Tick()
        {
            countdown.Text = $"Sem resposta, o pedido é recusado em {_seconds} s.";
            if (_seconds-- <= 0) DialogResult = DialogResult.No;
        }
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }
}

/// <summary>Senha pedida ao conectar. Deixar em branco pede permissão a quem está no outro computador.</summary>
internal sealed class PasswordPromptDialog : Form
{
    private readonly TextBox _box = new() { UseSystemPasswordChar = true, Location = new Point(16, 82), Size = new Size(348, 23) };

    /// <summary>Senha digitada; vazio quando o usuário preferiu pedir permissão.</summary>
    public string Password { get; private set; } = "";

    /// <param name="remember">O destino está salvo para entrar direto: a senha só é pedida desta vez.</param>
    public PasswordPromptDialog(string target, bool remember)
    {
        Ui.Prepare(this, "Conectar - Ponte", 380, 166);
        Ui.Text(this, $"Senha de acesso de {target}:", 16, 16, 348);
        Ui.Text(this, remember
            ? "Entrando com a senha, este computador fica autorizado lá e as próximas conexões entram direto."
            : "Sem a senha, você pode pedir que alguém aceite do outro lado.", 16, 38, 348, 36).ForeColor = SystemColors.GrayText;
        Controls.Add(_box);
        var ok = Ui.Button(this, "Conectar", 16, 122, 90, DialogResult.OK);
        var ask = Ui.Button(this, "Pedir permissão", 114, 122, 130, DialogResult.Retry);
        var cancel = Ui.Button(this, "Cancelar", 274, 122, 90, DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) => Password = _box.Text;
        ask.Click += (_, _) => Password = "";
    }
}

/// <summary>Define ou remove a senha de acesso sem acompanhamento.</summary>
internal sealed class SetPasswordDialog : Form
{
    public string? NewPassword { get; private set; }

    public SetPasswordDialog(bool hasPassword)
    {
        Ui.Prepare(this, "Senha de acesso - Ponte", 400, 214);
        Ui.Text(this, "Com uma senha, quem a souber entra sem ninguém precisar aceitar. Use pelo menos 8 caracteres e não repita senha de outro lugar.", 16, 14, 368, 50);
        Ui.Text(this, "Nova senha", 16, 72, 110);
        var first = new TextBox { UseSystemPasswordChar = true, Location = new Point(130, 70), Size = new Size(254, 23) };
        Ui.Text(this, "Repita a senha", 16, 104, 110);
        var second = new TextBox { UseSystemPasswordChar = true, Location = new Point(130, 102), Size = new Size(254, 23) };
        Controls.Add(first);
        Controls.Add(second);
        var error = Ui.Text(this, "", 16, 134, 368);
        error.ForeColor = Ui.Danger;

        var save = Ui.Button(this, "Salvar", 196, 170, 90);
        var cancel = Ui.Button(this, "Cancelar", 294, 170, 90, DialogResult.Cancel);
        var remove = Ui.Button(this, "Remover senha", 16, 170, 120);
        remove.Enabled = hasPassword;
        AcceptButton = save;
        CancelButton = cancel;

        save.Click += (_, _) =>
        {
            if (first.Text.Length < 8) { error.Text = "A senha precisa ter pelo menos 8 caracteres."; return; }
            if (first.Text != second.Text) { error.Text = "As duas senhas não são iguais."; return; }
            NewPassword = first.Text;
            DialogResult = DialogResult.OK;
        };
        remove.Click += (_, _) =>
        {
            NewPassword = "";
            DialogResult = DialogResult.OK;
        };
    }
}

/// <summary>Guarda um computador na lista de quem acessa, com um nome para reconhecê-lo.</summary>
internal sealed class SaveComputerDialog : Form
{
    public string Address { get; private set; }
    public string ComputerName { get; private set; }
    public bool AutoAccess { get; private set; }

    /// <param name="editing">Num computador já salvo o endereço não muda; para trocar, remove-se e salva-se outro.</param>
    public SaveComputerDialog(string address, string name, bool autoAccess, bool editing)
    {
        Address = address;
        ComputerName = name;
        AutoAccess = autoAccess;

        Ui.Prepare(this, editing ? "Editar computador - Ponte" : "Salvar computador - Ponte", 440, 250);
        Ui.Text(this, "Nome", 16, 19, 130);
        var nameBox = new TextBox { Text = name, MaxLength = 40, PlaceholderText = "Escritório, Casa, Loja...", Location = new Point(150, 16), Size = new Size(274, 23) };
        Ui.Text(this, "Endereço (ID ou IP)", 16, 51, 130);
        var addressBox = new TextBox { Text = address, MaxLength = 80, ReadOnly = editing, Location = new Point(150, 48), Size = new Size(274, 23) };
        var auto = new CheckBox { Text = "Entrar sem aprovação nas próximas vezes", Checked = autoAccess, Location = new Point(16, 86), Size = new Size(408, 24) };
        Controls.Add(nameBox);
        Controls.Add(addressBox);
        Controls.Add(auto);
        Ui.Text(this, "O outro computador precisa autorizar este uma vez: alguém marca \"Sempre permitir este computador\" no pedido que aparece lá, ou você entra com a senha de acesso dele. Depois disso a conexão entra direto.",
            34, 112, 390, 68).ForeColor = SystemColors.GrayText;
        var error = Ui.Text(this, "", 16, 182, 408);
        error.ForeColor = Ui.Danger;

        var save = Ui.Button(this, "Salvar", 236, 208, 90);
        var cancel = Ui.Button(this, "Cancelar", 334, 208, 90, DialogResult.Cancel);
        AcceptButton = save;
        CancelButton = cancel;
        ActiveControl = nameBox;
        save.Click += (_, _) =>
        {
            string typed = addressBox.Text.Trim();
            if (typed.Length == 0) { error.Text = "Informe o endereço do computador (ID de 9 dígitos ou IP)."; return; }
            if (typed.All(c => char.IsDigit(c) || c is ' ' or '-') && !Transport.IsId(typed, out _)) { error.Text = "O ID tem 9 dígitos."; return; }
            Address = typed;
            ComputerName = nameBox.Text.Trim();
            AutoAccess = auto.Checked;
            DialogResult = DialogResult.OK;
        };
    }
}

/// <summary>Computadores que entram neste sem senha e sem aceite. Tirar um daqui vale a partir da conexão seguinte.</summary>
internal sealed class TrustedDialog : Form
{
    public TrustedDialog(AppConfig config)
    {
        Ui.Prepare(this, "Computadores autorizados - Ponte", 640, 330);
        Ui.Text(this, "Estes computadores entram aqui sem senha e sem ninguém precisar aceitar. O aviso de sessão ativa continua aparecendo na tela enquanto durar o acesso.", 16, 12, 608, 36);
        var list = new ListView
        {
            Location = new Point(16, 54), Size = new Size(608, 222), View = View.Details, FullRowSelect = true,
            HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        list.Columns.Add("Computador", 150);
        list.Columns.Add("Usuário", 110);
        list.Columns.Add("Endereço", 90);
        list.Columns.Add("Autorizado", 150);
        list.Columns.Add("Último acesso", 100);
        Controls.Add(list);

        var remove = Ui.Button(this, "Remover autorização", 16, 288, 150);
        var close = Ui.Button(this, "Fechar", 534, 288, 90, DialogResult.Cancel);
        CancelButton = close;

        void Fill()
        {
            list.Items.Clear();
            foreach (var t in config.TrustedSnapshot().OrderByDescending(t => t.LastUsed))
            {
                var item = new ListViewItem(new[]
                {
                    t.Name, t.User, Security.FormatId(t.Id),
                    $"{t.Added:dd/MM/yyyy}, {(t.ViaPassword ? "pela senha" : "aceito aqui")}", $"{t.LastUsed:dd/MM HH:mm}",
                }) { Tag = t.KeyHash };
                list.Items.Add(item);
            }
            remove.Enabled = false;
        }
        list.SelectedIndexChanged += (_, _) => remove.Enabled = list.SelectedItems.Count > 0;
        remove.Click += (_, _) =>
        {
            config.RemoveTrusted(list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!));
            Fill();
        };
        Fill();
    }
}

internal sealed class SettingsDialog : Form
{
    /// <param name="manageAutostart">Falso na cópia de desenvolvimento e nos perfis de teste, que não entram na inicialização do Windows.</param>
    public SettingsDialog(AppConfig config, bool manageAutostart)
    {
        Ui.Prepare(this, "Configurações - Ponte", 440, 416);
        Ui.Text(this, "Servidor de retransmissão (necessário para conectar por ID pela internet)", 16, 14, 408);
        var relay = new TextBox { Text = config.RelayServer, PlaceholderText = "exemplo.com:7412", Location = new Point(16, 38), Size = new Size(408, 23) };
        Controls.Add(relay);
        Ui.Text(this, "Deixe em branco para usar só conexões diretas por IP.", 16, 64, 408).ForeColor = SystemColors.GrayText;

        Ui.Text(this, "Chave do servidor", 16, 95, 130);
        var key = new TextBox { Text = config.RelayKey, Location = new Point(150, 92), Size = new Size(274, 23) };
        Controls.Add(key);

        var direct = new CheckBox { Text = "Aceitar conexões diretas nesta porta:", Checked = config.DirectEnabled, Location = new Point(16, 130), Size = new Size(250, 24) };
        var port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = Math.Clamp(config.ListenPort, 1024, 65535), Location = new Point(270, 130), Size = new Size(90, 23) };
        Controls.Add(direct);
        Controls.Add(port);
        var elevated = new CheckBox { Text = "Abrir como administrador, para controlar programas abertos como administrador", Checked = config.RunElevated, Location = new Point(16, 164), Size = new Size(408, 38) };
        var update = new CheckBox { Text = "Atualizar o Ponte automaticamente", Checked = config.AutoUpdate, Location = new Point(16, 204), Size = new Size(408, 24) };
        var startup = new CheckBox { Text = "Iniciar o Ponte com o Windows, minimizado", Checked = config.StartWithWindows && manageAutostart,
            Enabled = manageAutostart, Location = new Point(16, 232), Size = new Size(408, 24) };
        Controls.Add(startup);
        var sound = new CheckBox { Text = "Transmitir o som deste computador a quem o acessa", Checked = config.ShareSound, Location = new Point(16, 264), Size = new Size(408, 24) };
        Controls.Add(elevated);
        Controls.Add(update);
        Controls.Add(sound);

        Ui.Text(this, "Aparência", 16, 305, 130);
        var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(150, 302), Size = new Size(274, 23) };
        string[] themes = { "auto", "claro", "escuro" };
        theme.Items.AddRange(new object[] { "Acompanhar o Windows", "Clara", "Escura (modo noturno)" });
        theme.SelectedIndex = Math.Max(0, Array.IndexOf(themes, config.Theme));
        Controls.Add(theme);
        Ui.Text(this, "As mudanças valem na próxima vez que o Ponte for aberto.", 16, 338, 408).ForeColor = SystemColors.GrayText;

        var save = Ui.Button(this, "Salvar", 236, 372, 90);
        var cancel = Ui.Button(this, "Cancelar", 334, 372, 90, DialogResult.Cancel);
        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) =>
        {
            config.RelayServer = relay.Text.Trim();
            config.RelayKey = key.Text.Trim();
            config.DirectEnabled = direct.Checked;
            config.ListenPort = (int)port.Value;
            config.RunElevated = elevated.Checked;
            config.AutoUpdate = update.Checked;
            if (manageAutostart) {
                config.StartWithWindows = startup.Checked;
                Autostart.Apply(config);
            }
            config.ShareSound = sound.Checked;
            config.Theme = themes[theme.SelectedIndex];
            config.Save();
            DialogResult = DialogResult.OK;
        };
    }
}

/// <summary>
/// Aviso que fica sempre visível enquanto alguém controla este computador, com o botão para encerrar.
/// Não há modo oculto: sessão ativa significa este painel na tela.
/// </summary>
internal sealed class SessionIndicator : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private string _fileStatus = "";
    private bool _finished;

    public SessionIndicator(HostSession session)
    {
        Ui.Prepare(this, "Ponte - sessão ativa", 330, 122);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        // Abre minimizado na barra de tarefas: quem está no computador continua vendo que há uma sessão e abre o
        // aviso para encerrá-la, mas ele não cobre a tela (nem a imagem de quem acessa).
        ShowInTaskbar = true;
        WindowState = FormWindowState.Minimized;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);

        var title = Ui.Text(this, $"{session.PeerName} está controlando este computador", 12, 10, 306, 20);
        title.Font = new Font(Font, FontStyle.Bold);
        title.ForeColor = Ui.Accent;
        title.AutoEllipsis = true;
        Ui.Text(this, $"{session.PeerUser}, {session.Remote}, desde {session.Started:HH:mm}", 12, 32, 306).AutoEllipsis = true;
        // Uma linha para o que está saindo deste computador além da tela: o som e os arquivos enviados daqui.
        var note = Ui.Text(this, "", 12, 54, 306);
        note.ForeColor = SystemColors.GrayText;
        note.AutoEllipsis = true;
        var stop = Ui.Button(this, "Encerrar sessão", 12, 82, 130);
        stop.Click += (_, _) => session.Stop();
        var send = Ui.Button(this, "Enviar arquivo...", 150, 82, 130);
        send.Visible = session.PeerReceivesFiles;
        send.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = $"Enviar arquivo para {session.PeerName}" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string name = Path.GetFileName(dialog.FileName);
            send.Enabled = false;
            try
            {
                await session.SendFileAsync(dialog.FileName, new Progress<int>(p => _fileStatus = $"Enviando {name}: {p} %"));
                _fileStatus = $"{name} enviado. Lá ele fica em Downloads\\Ponte.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
            {
                _fileStatus = $"Não foi possível enviar {name}.";
            }
            if (!IsDisposed) send.Enabled = true;
        };

        void Refresh() => note.Text = _fileStatus.Length > 0 ? _fileStatus
            : !session.SoundOn ? ""
            : session.SoundProblem.Length > 0 ? "Som pedido, mas este computador não tem saída de som."
            : "O som deste computador está sendo transmitido.";
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
        // O X só minimiza: o aviso some de vez quando a sessão termina (ou pelo "Encerrar sessão").
        FormClosing += (_, e) =>
        {
            if (_finished || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            WindowState = FormWindowState.Minimized;
        };
    }

    /// <summary>Fecha o aviso de vez, no fim da sessão.</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }

    // Aparece sem roubar o foco de quem está trabalhando.
    protected override bool ShowWithoutActivation => true;
}
