namespace Ponte;

/// <summary>
/// Entrar na conta (a mesma do FilaBox), com e-mail e senha ou com o Google. É opcional: sem conta, o Ponte
/// funciona igual, só que a lista de computadores salvos fica apenas neste aparelho.
/// </summary>
internal sealed class AccountDialog : Form
{
    private readonly TextBox _email = new() { Location = new Point(110, 92), Size = new Size(294, 23) };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Location = new Point(110, 124), Size = new Size(294, 23) };
    private readonly Label _status;
    private readonly Button _enter, _google;
    private CancellationTokenSource? _working;

    /// <summary>A conta em que se entrou, quando a janela fecha com OK.</summary>
    public Account? Account { get; private set; }

    public AccountDialog()
    {
        Ui.Prepare(this, "Conta - Ponte", 420, 330);
        Ui.Text(this, "Entre com a sua conta do FilaBox", 16, 14, 388).Font = new Font(Font, FontStyle.Bold);
        Ui.Text(this, "Os computadores que você salvar ficam na conta e aparecem em qualquer aparelho em que você entrar. " +
                      "Cada conta vê só os próprios. Entrar é opcional.", 16, 38, 388, 48).ForeColor = SystemColors.GrayText;
        Ui.Text(this, "E-mail", 16, 95, 90);
        Ui.Text(this, "Senha", 16, 127, 90);
        Controls.Add(_email);
        Controls.Add(_password);
        _enter = Ui.Button(this, "Entrar", 110, 158, 110);
        _google = Ui.Button(this, "Entrar com o Google", 228, 158, 176);
        var create = Link("Criar conta no FilaBox", 16, 200, () => Ponte.Account.OpenSite("/login"));
        var forgot = Link("Esqueci a senha", 216, 200, () => Ponte.Account.OpenSite("/recuperar-senha"));
        _status = Ui.Text(this, "", 16, 230, 388, 54);
        var cancel = Ui.Button(this, "Cancelar", 314, 290, 90, DialogResult.Cancel);
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
            Account = await signIn(working.Token);
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
