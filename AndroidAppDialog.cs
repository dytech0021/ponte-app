namespace Ponte;

/// <summary>
/// Leva o aplicativo para Android ao celular: baixa e confere o APK, mostra um QR Code com o link na rede
/// local (o celular baixa direto deste computador) e permite salvar o arquivo.
/// </summary>
internal sealed class AndroidAppDialog : Form
{
    private readonly AppConfig _config;
    private readonly PictureBox _qr = new() { Location = new Point(16, 92), Size = new Size(232, 232), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private readonly Label _state;
    private readonly TextBox _link = new() { Location = new Point(264, 128), Size = new Size(296, 23), ReadOnly = true };
    private readonly Label _others;
    private readonly Button _copy, _save, _retry;
    private readonly CancellationTokenSource _cts = new();
    private AndroidApp.Package? _package;
    private ApkServer? _server;

    /// <summary>Terminou de tentar: o QR Code está na tela ou o motivo de não estar.</summary>
    public bool Ready { get; private set; }

    public AndroidAppDialog(AppConfig config)
    {
        _config = config;
        Ui.Prepare(this, "Aplicativo para celular - Ponte", 576, 520);
        var title = Ui.Text(this, "Acesse seus computadores pelo celular", 16, 14, 544, 24);
        title.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        title.ForeColor = Ui.Accent;
        Ui.Text(this, "Aponte a câmera do celular Android para o código: abre uma página para baixar o aplicativo. O celular precisa estar na mesma rede Wi-Fi deste computador.",
            16, 44, 544, 40);
        Controls.Add(_qr);

        _state = Ui.Text(this, "Baixando o aplicativo do servidor...", 264, 92, 296, 34);
        Controls.Add(_link);
        _copy = Ui.Button(this, "Copiar link", 264, 158, 110);
        _copy.Click += (_, _) => { if (_link.Text.Length > 0) Clipboard.SetText(_link.Text); };
        _others = Ui.Text(this, "", 264, 194, 296, 60);
        _others.ForeColor = SystemColors.GrayText;
        Ui.Text(this, "No celular, depois de baixar, abra o arquivo e permita a instalação quando o Android pedir. Daí em diante o aplicativo se atualiza sozinho.",
            264, 258, 296, 66).ForeColor = SystemColors.GrayText;

        // iPhone (ou qualquer aparelho sem o Ponte): a versão pelo navegador, servida junto com o servidor de retransmissão.
        if (WebAddress(config) is { } web)
        {
            Controls.Add(new PictureBox
            {
                Location = new Point(16, 338), Size = new Size(120, 120), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White,
                Image = Render(QrCode.Encode(web)),
            });
            var iphone = Ui.Text(this, "No iPhone", 148, 338, 412, 22);
            iphone.Font = new Font(Font, FontStyle.Bold);
            Ui.Text(this, "Aponte a câmera do iPhone para este código: o Ponte abre no Safari (serve também para qualquer celular ou " +
                          "computador sem o Ponte). Para virar um aplicativo, toque em Compartilhar e depois em \"Adicionar à Tela de Início\".",
                148, 362, 412, 66).ForeColor = SystemColors.GrayText;
            var link = new LinkLabel { Text = web, Location = new Point(148, 432), AutoSize = true, LinkColor = Ui.Accent };
            link.LinkClicked += (_, _) => Clipboard.SetText(web);
            Controls.Add(link);
            Ui.Text(this, "(clique para copiar)", 148 + TextRenderer.MeasureText(web, link.Font).Width + 4, 432, 160).ForeColor = SystemColors.GrayText;
        }
        _save = Ui.Button(this, "Salvar o arquivo...", 16, 478, 150);
        _save.Click += (_, _) => SaveApk();
        _retry = Ui.Button(this, "Tentar de novo", 174, 478, 120);
        _retry.Click += async (_, _) => await LoadAsync();
        var close = Ui.Button(this, "Fechar", 470, 478, 90, DialogResult.Cancel);
        CancelButton = close;

        _copy.Enabled = _save.Enabled = false;
        _retry.Visible = false;
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Ready = false;
        try { await TryLoadAsync(); }
        finally { Ready = true; }
    }

    private async Task TryLoadAsync()
    {
        _retry.Visible = false;
        _state.Text = "Baixando o aplicativo do servidor...";
        try
        {
            _package ??= await AndroidApp.DownloadAsync(_config, _cts.Token);
        }
        catch (PonteException ex)
        {
            if (IsDisposed) return;
            _state.Text = ex.Message;
            _retry.Visible = true;
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (IsDisposed) return;
        _save.Enabled = true;

        try
        {
            _server ??= new ApkServer(_package);
        }
        catch (PonteException ex)
        {
            _state.Text = $"Versão {_package.Version} pronta, mas sem link para o celular: {ex.Message} Use \"Salvar o arquivo...\".";
            return;
        }

        var addresses = AndroidApp.LocalAddresses();
        if (addresses.Count == 0)
        {
            _state.Text = $"Versão {_package.Version} pronta, mas este computador não está em nenhuma rede local. Use \"Salvar o arquivo...\".";
            return;
        }
        string Link(System.Net.IPAddress address) => $"http://{address}{(_server.Port == 80 ? "" : ":" + _server.Port)}/";
        _link.Text = Link(addresses[0]);
        _copy.Enabled = true;
        _qr.Image?.Dispose();
        _qr.Image = Render(QrCode.Encode(_link.Text));
        _state.Text = $"Versão {_package.Version}, conferida. Pronto para o celular baixar.";
        _others.Text = (addresses.Count > 1 ? "Se não abrir, tente: " + string.Join("  ", addresses.Skip(1).Take(2).Select(Link)) + "\n" : "") +
                       "Se o celular não abrir o link, o Firewall do Windows pode estar bloqueando: use \"Salvar o arquivo...\".";
    }

    /// <summary>
    /// Endereço do Ponte no navegador: fica no mesmo servidor que a retransmissão, em https://servidor/ponte/.
    /// Sem servidor configurado (ou com um IP, que não tem certificado), não há.
    /// </summary>
    public static string? WebAddress(AppConfig config)
    {
        if (config.RelayServer.Length == 0) return null;
        string host = Transport.ParseEndpoint(config.RelayServer, Transport.DefaultRelayPort).host;
        return System.Net.IPAddress.TryParse(host, out _) || !host.Contains('.') ? null : $"https://{host}/ponte/";
    }

    // QR Code em preto e branco, com a margem de 4 módulos que a norma pede.
    internal static Bitmap Render(bool[,] modules)
    {
        const int border = 4, scale = 8;
        int size = modules.GetLength(0), side = (size + border * 2) * scale;
        var bitmap = new Bitmap(side, side);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (modules[y, x]) g.FillRectangle(Brushes.Black, (x + border) * scale, (y + border) * scale, scale, scale);
        return bitmap;
    }

    private void SaveApk()
    {
        if (_package is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "Aplicativo Android (*.apk)|*.apk", FileName = "Ponte.apk",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, _package.Apk);
            _state.Text = $"Salvo em {dialog.FileName}. Leve o arquivo para o celular (cabo, nuvem ou mensagem) e abra-o lá.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _state.Text = "Não foi possível salvar o arquivo nessa pasta.";
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _server?.Dispose();
        _qr.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
