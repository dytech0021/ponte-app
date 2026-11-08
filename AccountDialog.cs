namespace Ponte;

/// <summary>
/// Entrar na conta (a mesma do FilaBox), com e-mail e senha ou com o Google. Desde a 1.17.0 é obrigatório: só contas
/// liberadas pelo dono do Ponte acessam ou são acessadas. Ao entrar, a janela já pega o bilhete da conta no servidor do
/// Ponte; conta fora da lista de liberadas não fica neste aparelho.
/// </summary>
internal sealed class AccountDialog : Form
{
    private readonly TextBox _email = new() { Location = new Point(110, 92), Size = new Size(294, 23) };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Location = new Point(110, 124), Size = new Size(294, 23) };
    private readonly Label _status;
    private readonly Button _enter, _google;
    private CancellationTokenSource? _working;

    private readonly AppConfig _config;

    /// <summary>A conta em que se entrou, quando a janela fecha com OK.</summary>
    public Account? Account { get; private set; }
    /// <summary>O bilhete da conta liberada, preso à chave deste aparelho.</summary>
    public string IssuedTicket { get; private set; } = "";
    /// <summary>A conta é administradora da lista de liberadas.</summary>
    public bool Admin { get; private set; }

    public AccountDialog(AppConfig config, bool required = false)
    {
        _config = config;
        Ui.Prepare(this, "Conta - Ponte", 420, 330);
        Ui.Text(this, required ? "Entre na sua conta para usar o Ponte" : "Entre com a sua conta do FilaBox", 16, 14, 388).Font = new Font(Font, FontStyle.Bold);
        Ui.Text(this, "O Ponte só funciona com uma conta liberada pelo dono do Ponte, dos dois lados: em quem acessa e no aparelho " +
                      "acessado. A conta é a mesma do FilaBox, e os computadores salvos ficam nela.", 16, 38, 388, 48).ForeColor = SystemColors.GrayText;
        Ui.Text(this, "E-mail", 16, 95, 90);
        Ui.Text(this, "Senha", 16, 127, 90);
        Controls.Add(_email);
        Controls.Add(_password);
        _enter = Ui.Button(this, "Entrar", 110, 158, 110);
        _google = Ui.Button(this, "Entrar com o Google", 228, 158, 176);
        var create = Link("Criar conta no FilaBox", 16, 200, () => Ponte.Account.OpenSite("/login"));
        var forgot = Link("Esqueci a senha", 216, 200, () => Ponte.Account.OpenSite("/recuperar-senha"));
        _status = Ui.Text(this, "", 16, 230, 388, 54);
        var cancel = Ui.Button(this, required ? "Agora não" : "Cancelar", 314, 290, 90, DialogResult.Cancel);
        AcceptButton = _enter;
        CancelButton = cancel;

        _enter.Click += async (_, _) =>
        {
            if (_email.Text.Trim().Length == 0 || _password.Text.Length == 0)
            {
                _status.Text = "Preencha o e-mail e a senha.";
                return;
            }
            string email = _email.Text, password = _password.Text;
            await RunAsync("Entrando...", ct => Ponte.Account.SignInAsync(email, password, ct));
        };
        _google.Click += async (_, _) =>
            await RunAsync("Continue no navegador que abriu. Depois de entrar no Google, volte para cá.", Ponte.Account.SignInWithGoogleAsync);
        FormClosed += (_, _) => _working?.Cancel();
    }

    private LinkLabel Link(string text, int x, int y, Action open)
    {
        var link = new LinkLabel { Text = text, Location = new Point(x, y), AutoSize = true, LinkColor = Ui.Accent };
        link.LinkClicked += (_, _) => open();
        Controls.Add(link);
        return link;
    }

    private async Task RunAsync(string waiting, Func<CancellationToken, Task<Account>> signIn)
    {
        _working?.Cancel();
        var working = _working = new CancellationTokenSource();
        _enter.Enabled = _google.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = waiting;
        try
        {
            var account = await signIn(working.Token);
            if (!IsDisposed) _status.Text = "Conferindo se a conta está liberada para usar o Ponte...";
            try
            {
                IssuedTicket = await Ponte.Ticket.RequestAsync(_config, await account.AccessTokenAsync(working.Token), working.Token);
            }
            catch (Exception ex) when (ex is Ponte.Ticket.NotReleasedException or PonteException)
            {
                // Conta fora da lista (ou sem como conferir agora): não fica gravada neste aparelho.
                await account.SignOutAsync();
                throw new PonteException(ex.Message);
            }
            try { Admin = (await account.StatusAsync(working.Token)).Admin; }
            catch (PonteException) { }
            Account = account;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (PonteException ex)
        {
            if (IsDisposed) return;
            _status.ForeColor = Ui.Danger;
            _status.Text = ex.Message;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!IsDisposed) _enter.Enabled = _google.Enabled = true;
        }
    }
}

/// <summary>Só para o administrador: quem pode usar o Ponte (1.17.0). Tirar alguém da lista vale quando o bilhete dele vencer (até 14 dias).</summary>
internal sealed class ReleasedDialog : Form
{
    private readonly Account _account;
    private readonly ListBox _list = new() { Location = new Point(16, 40), Size = new Size(388, 170), IntegralHeight = false };
    private readonly TextBox _email = new() { Location = new Point(16, 222), Size = new Size(268, 23), PlaceholderText = "e-mail da conta a liberar" };
    private readonly Label _status;
    private readonly Button _add, _remove;
    private List<Account.Released> _rows = new();

    public ReleasedDialog(Account account)
    {
        _account = account;
        Ui.Prepare(this, "Contas liberadas - Ponte", 420, 340);
        Ui.Text(this, "Só estas contas usam o Ponte (para acessar e para serem acessadas).", 16, 14, 388);
        Controls.Add(_list);
        Controls.Add(_email);
        _add = Ui.Button(this, "Liberar", 292, 221, 112);
        _remove = Ui.Button(this, "Tirar da lista", 16, 254, 130);
        _status = Ui.Text(this, "Carregando...", 154, 252, 250, 40);
        var close = Ui.Button(this, "Fechar", 314, 298, 90, DialogResult.Cancel);
        CancelButton = close;
        AcceptButton = _add;
        _add.Click += async (_, _) =>
        {
            string email = _email.Text.Trim();
            if (email.Length == 0) { _status.Text = "Digite o e-mail."; return; }
            if (await RunAsync(ct => _account.ReleaseAsync(email, ct), $"{email} liberado. A pessoa já pode entrar no Ponte com essa conta.")) _email.Clear();
        };
        _remove.Click += async (_, _) =>
        {
            if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _rows.Count) { _status.Text = "Escolha uma conta na lista."; return; }
            string email = _rows[_list.SelectedIndex].Email;
            if (MessageBox.Show(this, $"Tirar {email} da lista?\n\nOs aparelhos dessa conta param de funcionar quando o bilhete deles vencer (em até 14 dias) ou quando saírem da conta.",
                    "Contas liberadas", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await RunAsync(ct => _account.UnreleaseAsync(email, ct), $"{email} saiu da lista.");
        };
        Shown += async (_, _) => await RunAsync(_ => Task.CompletedTask, "");
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task> action, string done)
    {
        _add.Enabled = _remove.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        try
        {
            await action(CancellationToken.None);
            _rows = await _account.ReleasedAsync(CancellationToken.None);
            if (IsDisposed) return true;
            _list.Items.Clear();
            foreach (var row in _rows)
                _list.Items.Add($"{row.Email}{(row.Admin ? "  (administrador)" : "")}  ·  desde {row.When.ToLocalTime():dd/MM/yyyy}");
            _status.Text = done.Length > 0 ? done : $"{_rows.Count} conta(s) liberada(s).";
            return true;
        }
        catch (PonteException ex)
        {
            if (IsDisposed) return false;
            _status.ForeColor = Ui.Danger;
            _status.Text = ex.Message;
            return false;
        }
        finally
        {
            if (!IsDisposed) _add.Enabled = _remove.Enabled = true;
        }
    }
}
