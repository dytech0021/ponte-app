using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ponte;

/// <summary>Janela principal: à esquerda o endereço deste computador, à direita a conexão a outro.</summary>
internal sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly HostService _host;
    private readonly List<ViewerForm> _viewers = new();
    private readonly Label _relayStatus, _passwordState, _trustedState, _status;
    private readonly TextBox _target;
    private readonly Button _connect, _edit, _remove;
    private readonly ComputerList _computers;
    private readonly Label _version;
    private readonly string[] _args;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 15_000 };
    private SessionIndicator? _indicator;
    private string _lastRemoteClipboard = "";
    private UpdateManifest? _pendingUpdate;
    private string _pendingFile = "";
    private bool _checkingUpdate, _restarting, _closed, _applyingUpdate;
    // Versão nova pronta enquanto alguém usa o Ponte: o aviso e o botão "Atualizar agora".
    private readonly Button _updateNow;
    private string _offeredVersion = "";
    private bool _balloonOffersUpdate;
    private readonly List<StreamViewerForm> _streams = new();
    private readonly NotifyIcon _tray;
    private BroadcastDialog? _broadcastDialog;
    private readonly Button _accountButton;
    private Account? _account;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private long _lastSync;
    // Aberto pela inicialização do Windows: a janela nem aparece, fica só o ícone perto do relógio.
    private bool _startHidden;

    /// <summary>Mensagem com que outra cópia do Ponte (aberta pelo atalho) pede para esta mostrar a janela.</summary>
    public static readonly int ShowMessage = Native.RegisterWindowMessageW("Ponte-mostrar-janela");

    public MainForm(AppConfig config, HostService host, string[] args)
    {
        _config = config;
        _host = host;
        _args = args;

        SuspendLayout();
        Ui.Prepare(this, $"Ponte {Updater.Current}{(Elevation.IsElevated ? " (administrador)" : "")}", 760, 572);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;

        var local = new GroupBox { Text = "Este computador", Location = new Point(12, 12), Size = new Size(320, 470) };
        Ui.Text(local, "Seu endereço", 16, 26, 288);
        var id = Ui.Text(local, Security.FormatId(config.Id), 14, 46, 290, 46);
        id.Font = new Font(Font.FontFamily, 22F, FontStyle.Bold);
        id.ForeColor = Ui.Accent;
        var copy = Ui.Button(local, "Copiar", 16, 98, 80);
        copy.Click += (_, _) => SetClipboard(config.Id);
        Ui.Text(local, "Na mesma rede, use:", 16, 140, 288);
        var address = Ui.Text(local, LocalAddresses(), 16, 160, 288);
        address.ForeColor = Ui.Accent;
        address.AutoEllipsis = true;
        _relayStatus = Ui.Text(local, "", 16, 188, 288, 36);
        _relayStatus.ForeColor = SystemColors.GrayText;
        _passwordState = Ui.Text(local, "", 16, 232, 288);
        var password = Ui.Button(local, "Senha de acesso...", 16, 256, 150);
        password.Click += (_, _) => ChangePassword();
        _trustedState = Ui.Text(local, "", 16, 304, 288);
        var trusted = Ui.Button(local, "Computadores autorizados...", 16, 328, 190);
        trusted.Click += (_, _) =>
        {
            using var dialog = new TrustedDialog(_config);
            dialog.ShowDialog(this);
            UpdateStatus();
        };
        Ui.Text(local, "Celular e transmissão:", 16, 382, 288);
        var phone = Ui.Button(local, "Aplicativo para celular...", 16, 406, 170);
        phone.Click += (_, _) =>
        {
            using var dialog = new AndroidAppDialog(_config);
            dialog.ShowDialog(this);
        };
        // Transmitir a tela (jogos): quem tem o link só assiste. A janela fica aberta enquanto a transmissão durar.
        var broadcast = Ui.Button(local, "Transmitir a tela...", 192, 406, 112);
        broadcast.Click += (_, _) =>
        {
            if (_broadcastDialog is { IsDisposed: false } open)
            {
                if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
                open.Activate();
                return;
            }
            _broadcastDialog = new BroadcastDialog(_host, _config);
            _broadcastDialog.FormClosed += (_, _) => _broadcastDialog = null;
            _broadcastDialog.Show(this);
        };

        var remote = new GroupBox { Text = "Computador remoto", Location = new Point(344, 12), Size = new Size(404, 470) };
        Ui.Text(remote, "Endereço (ID ou IP)", 16, 26, 372);
        _target = new TextBox { Location = new Point(16, 48), Size = new Size(372, 29), Font = new Font(Font.FontFamily, 12F) };
        remote.Controls.Add(_target);
        _connect = Ui.Button(remote, "Conectar", 16, 88, 110);
        _connect.Height = 30;
        _connect.Click += async (_, _) => await ConnectAsync(_target.Text);
        // Conta (opcional): a lista de computadores salvos acompanha a pessoa em qualquer aparelho.
        _accountButton = Ui.Button(remote, "", 188, 88, 200);
        _accountButton.Height = 30;
        _accountButton.AutoEllipsis = true;
        _accountButton.Click += (_, _) => AccountClicked();

        _computers = new ComputerList { Location = new Point(16, 132), Size = new Size(372, 290) };
        _computers.Activated += async entry => await ConnectAsync(entry.Address);
        _computers.SelectedIndexChanged += (_, _) =>
        {
            if (SelectedAddress is { } address) _target.Text = address;
            UpdateListButtons();
        };
        remote.Controls.Add(_computers);
        var save = Ui.Button(remote, "Salvar...", 16, 430, 90);
        save.Click += (_, _) => SaveComputer();
        _edit = Ui.Button(remote, "Editar...", 114, 430, 90);
        _edit.Click += (_, _) => EditComputer();
        _remove = Ui.Button(remote, "Remover", 212, 430, 90);
        _remove.Click += (_, _) => RemoveComputer();

        _status = Ui.Text(this, "", 14, 490, 734, 34);
        _version = Ui.Text(this, "", 14, 538, 424);
        _version.ForeColor = SystemColors.GrayText;
        _version.AutoEllipsis = true;
        _updateNow = Ui.Button(this, "Atualizar agora", 318, 532, 120);
        _updateNow.Visible = false;
        _updateNow.Click += (_, _) => UpdateNow();
        if (!Elevation.IsElevated)
        {
            // Sem isso o Windows bloqueia mouse e teclado em janelas de programas abertos como administrador.
            var elevate = Ui.Button(this, "Abrir como administrador", 446, 532, 174);
            elevate.Click += (_, _) => RestartElevated();
        }
        var settings = Ui.Button(this, "Configurações...", 628, 532, 120);
        settings.Click += (_, _) =>
        {
            using var dialog = new SettingsDialog(_config, Autostart.Managed(_args));
            if (dialog.ShowDialog(this) == DialogResult.OK) _status.Text = "Configurações salvas. Feche e abra o Ponte para aplicar.";
        };

        Controls.Add(local);
        Controls.Add(remote);
        AcceptButton = _connect;
        ResumeLayout(false);


        _host.AskPermission = AskPermissionAsync;
        _host.StatusChanged += () => Post(UpdateStatus);
        _host.SessionStarted += session => Post(() => OnSessionStarted(session));
        _host.SessionEnded += session => Post(() => OnSessionEnded(session));
        _host.FileReceived += path => Post(() => _status.Text = $"Arquivo recebido: {path}");
        _host.ClipboardReceived += text => Post(() => SetClipboard(text));
        UpdateStatus();
        RefreshComputers();
        _account = Account.Restore(_config);
        if (_account is not null) _account.TokenChanged += StoreAccount;
        UpdateAccountButton();
        if (_account is not null) _ = SyncAccountAsync();

        ShowVersion(args.Contains("--atualizado") ? "atualizado agora" : "");
        if (!Elevation.IsElevated)
            _status.Text = "Sem administrador: quem acessar este computador não consegue clicar em programas abertos como administrador.";
        _updateTimer.Tick += async (_, _) =>
        {
            // A primeira checagem é logo depois de abrir; as seguintes, a cada meia hora. Com uma versão nova já
            // baixada, confere a cada minuto se já dá para instalar sem atrapalhar ninguém.
            if (_pendingUpdate is not null)
            {
                _updateTimer.Interval = 60 * 1000;
                await ApplyUpdateAsync(force: false);
                return;
            }
            _updateTimer.Interval = 30 * 60 * 1000;
            await CheckForUpdateAsync();
        };
        if (_config.AutoUpdate && Updater.CanSelfUpdate) _updateTimer.Start();
        _presenceTimer.Tick += (_, _) => _ = RefreshPresenceAsync(force: true);
        _presenceTimer.Start();
        Shown += (_, _) => _ = RefreshPresenceAsync(force: true);
        Activated += (_, _) => _ = RefreshPresenceAsync();

        // Início com o Windows: acompanha a configuração a cada abertura, e o que passa a valer é avisado.
        if (Autostart.Managed(args) && Autostart.Apply(_config) is { } started)
            _status.Text = started;
        _startHidden = args.Contains(Autostart.Argument);
        _tray = CreateTrayIcon();
    }

    // ---------- administrador e atualização ----------

    private void ShowVersion(string note)
    {
        string mode = Elevation.IsElevated ? "como administrador" : "sem administrador";
        _version.Text = $"Ponte {Updater.Current}, {mode}{(note.Length > 0 ? " - " + note : "")}";
    }

    // Ninguém conectado, nada sendo transmitido ou assistido.
    private bool Idle => _host.Current is null && _viewers.Count == 0 && _streams.Count == 0 && _broadcastDialog is not { Active: true };

    // Alguém usando a janela do Ponte agora: a atualização pergunta em vez de reiniciar sozinha.
    private bool InUse => Visible && WindowState != FormWindowState.Minimized && Native.IdleTime < TimeSpan.FromMinutes(10);

    private IEnumerable<string> RestartArgs => _args.Where(a => a is not ("--atualizado" or "--aguardar" or "--ja-elevou" or "--elevar"));

    private void RestartElevated()
    {
        if (!Idle)
        {
            _status.Text = "Encerre a sessão em andamento antes de reabrir como administrador.";
            return;
        }
        // A cópia nova espera esta fechar (--aguardar faz ela aguardar a vez).
        if (!Elevation.TryRelaunchElevated(RestartArgs.Append("--aguardar").Append("--ja-elevou")))
        {
            _status.Text = "O Windows não abriu o Ponte como administrador (pedido recusado ou conta sem essa permissão).";
            return;
        }
        _restarting = true;
        Close();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_checkingUpdate || _restarting || _pendingUpdate is not null) return;
        _checkingUpdate = true;
        try
        {
            var manifest = await Updater.CheckAsync(_config, CancellationToken.None);
            if (manifest is null)
            {
                ShowVersion("versão mais recente");
                return;
            }
            ShowVersion($"baixando a versão {manifest.Version}");
            string file = await Updater.DownloadAsync(_config, manifest, CancellationToken.None);
            // O teste de partida da versão nova roda fora da janela, que continua respondendo: com ela parada
            // esperando, a versão nova (até a 1.6.1) travava ao aplicar o tema, que avisa todas as janelas da troca
            // de cores e espera o aviso chegar.
            ShowVersion($"conferindo a versão {manifest.Version}");
            await Task.Run(() => Updater.Verify(file, manifest));
            _pendingFile = file;
            _pendingUpdate = manifest;
            await ApplyUpdateAsync(force: false);
        }
        catch (Exception ex) when (ex is PonteException or IOException or UnauthorizedAccessException)
        {
            // Falha de atualização não atrapalha o uso: fica registrada na linha de versão e tenta de novo em 5 minutos.
            ShowVersion((ex is PonteException ? ex.Message : "não foi possível gravar a atualização nesta pasta") + " Nova tentativa em 5 minutos.");
            _updateTimer.Stop();
            _updateTimer.Interval = 5 * 60 * 1000;
            _updateTimer.Start();
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    /// <summary>
    /// Troca pela versão nova e reabre. Sozinha ([force] falso), só quando ninguém está conectado nem usando a janela
    /// (os computadores acessados de longe continuam se atualizando sem ninguém por perto); senão, oferece o botão
    /// "Atualizar agora" e o aviso perto do relógio.
    /// </summary>
    private async Task ApplyUpdateAsync(bool force)
    {
        if (_pendingUpdate is not { } update || _restarting || _applyingUpdate) return;
        if (!force && (!Idle || InUse))
        {
            OfferUpdate(update);
            return;
        }
        _applyingUpdate = true;
        try
        {
            // Dá à janela a chance de mostrar o aviso antes de fechar.
            ShowVersion($"instalando a versão {update.Version}");
            await Task.Yield();
            Updater.Replace(_pendingFile, RestartArgs.Append("--atualizado"));
            _restarting = true;
            Close();
        }
        catch (PonteException ex)
        {
            _pendingUpdate = null;
            _updateNow.Visible = false;
            _version.Width = 424;
            ShowVersion(ex.Message + " Nova tentativa em 5 minutos.");
            _updateTimer.Stop();
            _updateTimer.Interval = 5 * 60 * 1000;
            _updateTimer.Start();
        }
        finally
        {
            _applyingUpdate = false;
        }
    }

    private void OfferUpdate(UpdateManifest update)
    {
        _updateNow.Visible = true;
        _version.Width = 296;
        ShowVersion($"versão {update.Version} pronta");
        _status.Text = $"Chegou a versão {update.Version} do Ponte. Clique em \"Atualizar agora\": o Ponte fecha e abre de novo já atualizado.";
        // O aviso perto do relógio aparece uma vez por versão; o botão fica na janela até a troca.
        if (_offeredVersion == update.Version) return;
        _offeredVersion = update.Version;
        _balloonOffersUpdate = true;
        _tray.ShowBalloonTip(15_000, "Nova versão do Ponte",
            $"A versão {update.Version} está pronta. Clique aqui para atualizar agora: o Ponte fecha e abre de novo atualizado.", ToolTipIcon.Info);
    }

    private async void UpdateNow()
    {
        if (_pendingUpdate is not { } update || _restarting || _applyingUpdate) return;
        if (!Idle)
        {
            var ending = new List<string>();
            if (_host.Current is { } session) ending.Add($"a sessão de {session.PeerName} neste computador");
            if (_viewers.Count > 0) ending.Add(_viewers.Count == 1 ? "o acesso ao outro computador" : $"os {_viewers.Count} acessos a outros computadores");
            if (_broadcastDialog is { Active: true }) ending.Add("a transmissão da tela");
            if (_streams.Count > 0) ending.Add("a transmissão que você está assistindo");
            string list = ending.Count == 1 ? ending[0] : string.Join(", ", ending.Take(ending.Count - 1)) + " e " + ending[^1];
            ShowFromTray();
            if (MessageBox.Show(this, $"Atualizar agora encerra {list}. O Ponte fecha e abre de novo na versão {update.Version}.\n\nAtualizar agora?",
                    "Ponte", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }
        await ApplyUpdateAsync(force: true);
    }

    // ---------- este computador sendo acessado ----------

    private Task<Permission> AskPermissionAsync(IncomingRequest request)
    {
        var decision = new TaskCompletionSource<Permission>();
        bool posted = Post(() =>
        {
            // Com a janela principal escondida no ícone, o pedido ficaria escondido junto com ela.
            ShowFromTray();
            using var dialog = new AcceptDialog(request);
            bool accepted = dialog.ShowDialog(this) == DialogResult.Yes;
            decision.TrySetResult(!accepted ? Permission.Refuse : dialog.Always ? Permission.Always : Permission.Once);
        });
        if (!posted) decision.TrySetResult(Permission.Refuse);
        return decision.Task;
    }

    private void OnSessionStarted(HostSession session)
    {
        _indicator?.Finish();
        _indicator = new SessionIndicator(session);
        _indicator.Show();
        _status.Text = $"{session.PeerName} está controlando este computador (entrou por {session.Access}).";
        UpdateStatus();
    }

    private void OnSessionEnded(HostSession session)
    {
        _indicator?.Finish();
        _indicator = null;
        _status.Text = $"Sessão de {session.PeerName} encerrada às {DateTime.Now:HH:mm} (começou às {session.Started:HH:mm}, entrou por {session.Access}).";
        _ = ApplyUpdateAsync(force: false);
    }

    private void ChangePassword()
    {
        using var dialog = new SetPasswordDialog(_config.HasPassword);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _config.SetPassword(dialog.NewPassword);

        // Quem ficou autorizado usando a senha antiga só continua entrando direto se o dono confirmar.
        var byPassword = _config.TrustedSnapshot().Where(t => t.ViaPassword).ToArray();
        if (byPassword.Length > 0 && MessageBox.Show(this,
                "Estes computadores ficaram autorizados a entrar direto usando a senha anterior:\n\n" +
                string.Join("\n", byPassword.Select(t => $"{t.Name}  ({Security.FormatId(t.Id)})")) +
                "\n\nManter a autorização deles?", "Ponte", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            _config.RemoveTrusted(byPassword.Select(t => t.KeyHash));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        _relayStatus.Text = string.Join(" ", new[] { _host.RelayStatus, _host.DirectStatus }.Where(s => s.Length > 0));
        int trusted = _config.TrustedSnapshot().Length;
        _passwordState.Text = _config.HasPassword
            ? "Senha de acesso: definida"
            : trusted > 0 ? "Sem senha: os demais precisam ser aceitos aqui." : "Sem senha: toda conexão precisa ser aceita aqui.";
        _trustedState.Text = trusted switch
        {
            0 => "Nenhum computador entra sem aprovação.",
            1 => "1 computador entra sem aprovação.",
            _ => $"{trusted} computadores entram sem aprovação.",
        };
    }

    private string LocalAddresses()
    {
        if (!_config.DirectEnabled) return "conexões diretas desativadas";
        var list = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => _config.ListenPort == Transport.DefaultDirectPort ? a.Address.ToString() : $"{a.Address}:{_config.ListenPort}")
            .Distinct().Take(3).ToList();
        return list.Count > 0 ? string.Join("  ou  ", list) : "sem rede no momento";
    }

    // ---------- este computador acessando outro ----------

    private async Task ConnectAsync(string target)
    {
        target = target.Trim();
        if (target.Length == 0 || !_connect.Enabled) return;
        // Link (ou código) de uma transmissão de tela: assiste aqui dentro, em vez de no navegador.
        try
        {
            if (StreamViewerForm.Parse(target, _config) is { } stream)
            {
                var watch = new StreamViewerForm(_config, stream.Server, stream.Token);
                _streams.Add(watch);
                watch.FormClosed += (_, _) =>
                {
                    _streams.Remove(watch);
                    _ = ApplyUpdateAsync(force: false);
                };
                watch.Show();
                _status.Text = "Assistindo à transmissão em outra janela.";
                return;
            }
        }
        catch (PonteException ex)
        {
            _status.Text = ex.Message;
            return;
        }
        _connect.Enabled = false;
        var saved = _config.FindSaved(AppConfig.Target(target).Key);
        bool wanted = saved?.AutoAccess == true, wasPaired = saved?.Paired == true;
        _status.Text = wasPaired ? "Conectando..." : "Conectando... Se o outro computador não tiver senha, alguém precisa aceitar o pedido lá.";
        try
        {
            var session = await ViewerSession.ConnectAsync(_config, target, () => AskPassword(target, wanted), ConfirmNewIdentity, _host.ReceiveDir, CancellationToken.None);
            var form = new ViewerForm(session, _config);
            session.ClipboardReceived += text => Post(() => { if (form.ClipboardSync) SetClipboard(text); });
            _viewers.Add(form);
            form.ThumbnailSaved += RefreshComputers;
            form.FormClosed += (_, _) =>
            {
                _viewers.Remove(form);
                if (!_closed) RefreshComputers();
                _ = ApplyUpdateAsync(force: false);
            };
            form.Show();
            _status.Text = session.Paired
                ? wasPaired ? "" : "Este computador ficou autorizado lá: as próximas conexões entram direto, sem aprovação."
                : wanted ? "Ainda sem autorização para entrar direto. Lá, marque \"Sempre permitir este computador\" no pedido de conexão, ou entre uma vez com a senha de acesso." : "";
            RefreshComputers();
        }
        catch (PonteException ex)
        {
            _status.Text = "";
            MessageBox.Show(this, ex.Message, "Ponte", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _connect.Enabled = true;
        }
    }

    private Task<string?> AskPassword(string target, bool remember)
    {
        using var dialog = new PasswordPromptDialog(target, remember);
        var result = dialog.ShowDialog(this);
        return Task.FromResult(result == DialogResult.Cancel ? null : dialog.Password);
    }

    private Task<bool> ConfirmNewIdentity()
    {
        var answer = MessageBox.Show(this,
            "A identidade deste computador mudou desde a última conexão. Isso acontece quando o Ponte foi reinstalado lá, mas também pode indicar que outro equipamento está respondendo no lugar dele.\n\nConectar assim mesmo?",
            "Ponte", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        return Task.FromResult(answer == DialogResult.Yes);
    }

    // ---------- computadores salvos e recentes ----------

    private SavedEntry? SelectedSaved => _computers.Selected?.Source as SavedEntry;

    private RecentEntry? SelectedRecent => _computers.Selected?.Source as RecentEntry;

    private string? SelectedAddress => _computers.Selected?.Address;

    private void UpdateListButtons()
    {
        _edit.Enabled = SelectedSaved is not null;
        _remove.Enabled = _computers.Selected is not null;
    }

    private void RefreshComputers()
    {
        string? selected = SelectedAddress;
        Image Thumbnail(string key) => _computers.LoadThumbnail(_config.ThumbnailPath(key));

        var items = new List<object>();
        foreach (var s in _config.Saved.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (items.Count == 0) items.Add(new ComputerList.Header("Computadores salvos"));
            items.Add(new ComputerList.Entry(s, s.Name.Length > 0 ? s.Name : s.Address, s.Address, s.Paired ? "entra direto" : "pede aprovação",
                s.Paired
                    ? "O outro computador autorizou este: a conexão entra sem senha e sem aprovação."
                    : s.AutoAccess ? "Ainda falta o outro computador autorizar este uma vez." : "Salvo sem pedido de acesso direto.",
                Thumbnail(s.Key), s.Key));
        }
        // Um computador salvo não precisa aparecer de novo entre os recentes.
        var savedKeys = _config.Saved.Select(s => s.Key).ToHashSet();
        var recent = _config.Recent.Where(r => !savedKeys.Contains(AppConfig.Target(r.Address).Key)).ToArray();
        if (recent.Length > 0) items.Add(new ComputerList.Header("Conexões recentes"));
        foreach (var r in recent)
            items.Add(new ComputerList.Entry(r, r.Name, r.Address, $"{r.When:dd/MM HH:mm}", "", Thumbnail(AppConfig.Target(r.Address).Key)));

        _computers.Fill(items);
        // A lista é refeita com uma sessão aberta (a miniatura acabou de chegar); a seleção não pode se perder.
        _computers.SelectedItem = items.OfType<ComputerList.Entry>().FirstOrDefault(i => i.Address == selected);
        UpdateListButtons();
        _ = RefreshPresenceAsync();
    }

    // ---------- online / offline ----------

    private readonly System.Windows.Forms.Timer _presenceTimer = new() { Interval = 30_000 };
    private bool _checkingPresence;
    private long _lastPresence;

    /// <summary>
    /// Bolinha verde ou vermelha nos salvos. Confere ao abrir a janela, quando a lista muda e a cada 30 s enquanto a
    /// janela está à vista; escondida perto do relógio, não gasta rede com isso.
    /// </summary>
    private async Task RefreshPresenceAsync(bool force = false)
    {
        if (_checkingPresence || _closed || !Visible || WindowState == FormWindowState.Minimized) return;
        if (!force && _lastPresence != 0 && Stopwatch.GetElapsedTime(_lastPresence).TotalSeconds < 10) return;
        var saved = _config.Saved.ToList();
        if (saved.Count == 0) return;
        _checkingPresence = true;
        _lastPresence = Stopwatch.GetTimestamp();
        try
        {
            var online = await Presence.CheckAsync(_config, saved, CancellationToken.None);
            if (!_closed) _computers.SetOnline(online);
        }
        finally { _checkingPresence = false; }
    }

    // Salva o recente selecionado ou, sem seleção, o endereço que estiver digitado.
    private void SaveComputer()
    {
        var recent = SelectedRecent;
        using var dialog = new SaveComputerDialog(recent?.Address ?? (SelectedSaved is null ? _target.Text.Trim() : ""), recent?.Name ?? "", true, false);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _config.SaveComputer(dialog.Address, dialog.ComputerName, dialog.AutoAccess);
        RefreshComputers();
        _ = SyncAccountAsync();
    }

    private void EditComputer()
    {
        if (SelectedSaved is not { } saved) return;
        using var dialog = new SaveComputerDialog(saved.Address, saved.Name, saved.AutoAccess, true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _config.SaveComputer(saved.Address, dialog.ComputerName, dialog.AutoAccess);
        RefreshComputers();
        _ = SyncAccountAsync();
    }

    private void RemoveComputer()
    {
        if (SelectedSaved is { } saved)
        {
            string note = saved.Paired ? "\n\nA autorização continua valendo no outro computador até ser removida lá, em \"Computadores autorizados\"." : "";
            if (MessageBox.Show(this, $"Remover {(saved.Name.Length > 0 ? saved.Name : saved.Address)} dos computadores salvos?{note}", "Ponte",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _config.RemoveSaved(saved.Key);
        }
        else if (SelectedRecent is { } recent)
        {
            _config.RemoveRecent(recent.Address);
        }
        RefreshComputers();
        _ = SyncAccountAsync();
    }

    // ---------- área de transferência ----------

    // ---------- conta ----------

    private void UpdateAccountButton() =>
        _accountButton.Text = _account is null ? "Entrar na conta..." : $"Conta: {_account.Email}";

    private void AccountClicked()
    {
        if (_account is null)
        {
            using var dialog = new AccountDialog();
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Account is not { } account) return;
            _account = account;
            _account.TokenChanged += StoreAccount;
            StoreAccount(account);
            UpdateAccountButton();
            _ = SyncAccountAsync();
            return;
        }
        var menu = new ContextMenuStrip();
        menu.Items.Add("Sincronizar a lista agora", null, (_, _) => _ = SyncAccountAsync());
        menu.Items.Add("Criar ou gerenciar a conta no FilaBox", null, (_, _) => Account.OpenSite("/login"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair da conta neste computador", null, (_, _) => _ = SignOutAsync());
        menu.Show(_accountButton, new Point(0, _accountButton.Height));
    }

    private void StoreAccount(Account account)
    {
        account.Store(_config);
        try { _config.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task SyncAccountAsync()
    {
        if (_account is not { } account || !await _syncGate.WaitAsync(0)) return;
        try
        {
            _lastSync = Stopwatch.GetTimestamp();
            var (added, removed) = await _config.SyncAccountAsync(account, CancellationToken.None);
            if (_closed) return;
            RefreshComputers();
            if (added + removed > 0)
                _status.Text = $"Lista da conta atualizada: {added} computador(es) chegaram, {removed} saíram.";
        }
        catch (PonteException ex)
        {
            if (_closed) return;
            _status.Text = "Conta: " + ex.Message;
            // A conta saiu deste aparelho (senha trocada, sessão encerrada em outro lugar): volta ao modo sem conta.
            if (ex.Message.StartsWith("A conta saiu")) await SignOutAsync();
        }
        finally { _syncGate.Release(); }
    }

    private async Task SignOutAsync()
    {
        if (_account is not { } account) return;
        _account = null;
        account.TokenChanged -= StoreAccount;
        _config.SignOutAccount();
        UpdateAccountButton();
        RefreshComputers();
        _status.Text = "Você saiu da conta neste computador. Os computadores dela não aparecem mais aqui.";
        await account.SignOutAsync();
    }

    // ---------- ícone na área de notificação ----------

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir o Ponte", null, (_, _) => ShowFromTray());
        menu.Items.Add("Copiar o ID deste computador", null, (_, _) =>
        {
            try { Clipboard.SetText(Security.FormatId(_config.Id)); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Fechar o Ponte", null, (_, _) => Close());
        string mode = Elevation.IsElevated ? " (administrador)" : "";
        var tray = new NotifyIcon
        {
            Icon = Ui.MakeIcon(), ContextMenuStrip = menu, Visible = true,
            Text = $"Ponte {Updater.Current}{mode} · ID {Security.FormatId(_config.Id)}",
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        tray.BalloonTipClicked += (_, _) =>
        {
            if (_balloonOffersUpdate) UpdateNow();
            else ShowFromTray();
        };
        tray.BalloonTipClosed += (_, _) => _balloonOffersUpdate = false;
        if (_args.Contains("--atualizado"))
            tray.ShowBalloonTip(8_000, "Ponte atualizado", $"O Ponte foi atualizado para a versão {Updater.Current}.", ToolTipIcon.Info);
        return tray;
    }

    private void ShowFromTray()
    {
        // Ao abrir a janela, a lista é conferida com a conta (no máximo uma vez por minuto).
        if (_account is not null && Stopwatch.GetElapsedTime(_lastSync).TotalSeconds > 60) _ = SyncAccountAsync();
        _startHidden = false;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && value)
        {
            // A janela existe (para o Ponte funcionar e receber avisos), só não aparece.
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    // Minimizar também guarda o Ponte no ícone perto do relógio, sem botão na barra de tarefas.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Visible && !_closed) Hide();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Uma cópia sem administrador (aberta pelo atalho) também pode pedir para esta mostrar a janela.
        Native.ChangeWindowMessageFilterEx(Handle, ShowMessage, 1, IntPtr.Zero);
        Native.AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_CLIPBOARDUPDATE) OnLocalClipboardChanged();
        if (m.Msg == ShowMessage && ShowMessage != 0) ShowFromTray();
        base.WndProc(ref m);
    }

    private void OnLocalClipboardChanged()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (ExternalException) { return; }
        // O que acabou de chegar do outro lado não volta para lá.
        if (text.Length == 0 || text.Length > 100_000 || text == _lastRemoteClipboard) return;

        if (_host.Current is { } hosted) Forget(hosted.SendClipboardAsync(text));
        foreach (var viewer in _viewers.Where(v => v.ClipboardSync)) _ = viewer.Session.SendClipboardAsync(text);
    }

    private void SetClipboard(string text)
    {
        if (text.Length == 0) return;
        _lastRemoteClipboard = text;
        try { Clipboard.SetText(text); }
        catch (ExternalException) { }
    }

    private static async void Forget(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private bool Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        try
        {
            BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Grava em arquivo o que a janela está mostrando (usado pelo modo de verificação).</summary>
    public void SaveWindowImage(string path) => Ui.SaveImage(this, path);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_restarting && _host.Current is not null && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, "Há uma sessão ativa neste computador. Fechar o Ponte encerra a sessão. Continuar?", "Ponte",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        _tray.Visible = false;
        _tray.Dispose();
        _updateTimer.Dispose();
        _presenceTimer.Dispose();
        if (IsHandleCreated) Native.RemoveClipboardFormatListener(Handle);
        foreach (var viewer in _viewers.ToArray()) viewer.Close();
        foreach (var watch in _streams.ToArray()) watch.Close();
        _indicator?.Finish();
        _host.Dispose();
        base.OnFormClosed(e);
    }
}
