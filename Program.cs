using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ponte;

internal static class Program
{
    /// <summary>
    /// Sem argumentos abre o programa. Opções:
    ///   --perfil pasta                    usa outra pasta de configuração (outro ID)
    ///   --tema claro|escuro               usa esse tema nesta execução, sem mudar a configuração
    ///   --sem-elevar                      não tenta abrir como administrador
    ///   --elevar                          abre como administrador mesmo que a configuração diga que não
    ///   --inicio                          abertura feita pelo Windows na inicialização: minimizado e sem pedir administrador
    ///   --autoteste pasta [--relay h:p]   testa os dois lados nesta máquina e grava o resultado na pasta
    ///                                     (--so-video, --so-conta ou --so-transmissao: só essa parte)
    ///   --captura-ui pasta                grava a imagem da janela principal e das secundárias e sai
    ///   --demo pasta                      abre uma sessão com esta própria máquina, grava a imagem e sai
    ///   --atualizar-agora log [--relay h:p]   procura, confere e instala a atualização sem abrir janela
    ///   --assistir link [--gravar-imagem arquivo]   só a janela de assistir a uma transmissão de tela
    /// De quem publica: --gerar-chave-atualizacao chave fonte.cs | --assinar-atualizacao exe chave manifesto | --empacotar-video ffmpeg.exe pacote.br
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // O teste de partida que a versão anterior faz antes de se trocar por esta responde antes de qualquer coisa.
        // Aplicar o tema avisa todas as janelas abertas da troca de cores e espera o aviso chegar; a janela do Ponte
        // antigo está parada esperando este teste terminar, e no modo escuro do Windows um ficava esperando o outro:
        // a atualização falhava sempre ("não respondeu ao teste de partida").
        if (args.Contains("--verificar")) return 0;

        ApplicationConfiguration.Initialize();
        if ((Arg(args, "--autoteste") ?? Arg(args, "--demo") ?? Arg(args, "--captura-ui")) is { } outputDir)
        {
            // Rodando sem ninguém olhando, um erro inesperado não pode parar numa janela de aviso: fica gravado e o programa sai.
            // Precisa vir antes do tema: com o Windows no modo escuro, aplicar o tema já cria uma janela, e depois
            // disso o Windows Forms recusa a troca (o autoteste fechava na partida).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    Directory.CreateDirectory(outputDir);
                    File.WriteAllText(Path.Combine(outputDir, "erro.txt"), e.ExceptionObject.ToString());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                Environment.Exit(3);
            };
        }
        else
        {
            // No uso normal, um erro inesperado fica registrado em erros.txt, com a linha do código, e o Ponte segue
            // aberto: a janela padrão do Windows Forms oferecia fechar o programa inteiro (e com ele a sessão).
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                ErrorLog.Write(e.Exception);
                e.SetObserved();
            };
        }
        // O tema vale para todas as janelas e precisa estar escolhido antes de a primeira existir.
        Ui.ApplyTheme(Arg(args, "--tema") ?? AppConfig.SavedTheme(Arg(args, "--perfil")));

        if (Arg(args, "--gerar-chave-atualizacao") is { } keyFile) return Updater.GenerateKey(keyFile, Arg(args, keyFile) ?? "UpdateKey.cs");
        if (Arg(args, "--assinar-atualizacao") is { } exeToSign)
        {
            string key = Arg(args, exeToSign) ?? "", manifest = Arg(args, key) ?? "";
            return Updater.Sign(exeToSign, key, manifest);
        }
        if (Arg(args, "--empacotar-video") is { } ffmpeg) return VideoRuntime.Pack(ffmpeg, Arg(args, ffmpeg) ?? VideoRuntime.PackageName);

        // Os modos de verificação só elevam se pedido de forma explícita, para poderem rodar sem interação.
        bool testMode = new[] { "--autoteste", "--demo", "--captura-ui", "--atualizar-agora", "--pos-atualizacao", "--janela-teste" }.Any(args.Contains);
        if (!Elevation.IsElevated && !args.Contains("--ja-elevou") && !args.Contains("--sem-elevar"))
        {
            // Na inicialização do Windows não há quem responda ao aviso de administrador, e esperar por ele
            // deixaria o computador sem o Ponte: nesse caso só eleva se o Windows eleva sem avisar. (Com a tarefa
            // agendada de Autostart, o Ponte já chega aqui como administrador.)
            bool wanted = args.Contains("--elevar") ||
                          (!testMode && (!args.Contains(Autostart.Argument) || Elevation.ElevatesSilently) &&
                           Elevation.CanElevateSameUser && AppConfig.Load(Arg(args, "--perfil")).RunElevated);
            if (wanted && Elevation.TryRelaunchElevated(args.Where(a => a != "--elevar").Append("--ja-elevou"))) return 0;
        }

        if (Arg(args, "--pos-atualizacao") is { } afterLog)
        {
            Updater.Cleanup();
            File.AppendAllText(afterLog, $"em execução depois de atualizar: {Updater.Current}{Environment.NewLine}", new UTF8Encoding(false));
            return 0;
        }
        if (Arg(args, "--atualizar-agora") is { } updateLog) return UpdateNow(updateLog, args);
        if (Arg(args, "--janela-teste") is { } seconds) return TestWindow(int.TryParse(seconds, out int s) ? Math.Clamp(s, 5, 300) : 30);
        if (Arg(args, "--autoteste") is { } testDir)
            return Task.Run(() => SelfTest.RunAsync(testDir, Arg(args, "--relay"), args.Contains("--so-video"), args.Contains("--so-conta"), args.Contains("--so-transmissao"))).GetAwaiter().GetResult();
        if (Arg(args, "--demo") is { } demoDir) return Demo(demoDir, args);

        var config = AppConfig.Load(Arg(args, "--perfil"));
        if (Arg(args, "--assistir") is { } watch) return Watch(config, watch, Arg(args, "--gravar-imagem"));
        // Um perfil à parte (testes) anota as sessões na pasta dele, sem misturar com as do Ponte de uso.
        if (Arg(args, "--perfil") is not null) SessionLog.Folder = config.Dir;

        // Duas cópias com o mesmo perfil disputariam a porta e o registro do ID. Depois de uma
        // atualização a cópia anterior ainda está fechando, por isso a espera.
        using var single = new Mutex(false, "Local\\Ponte-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config.Dir.ToLowerInvariant())))[..16]);
        bool owned;
        try { owned = single.WaitOne(TimeSpan.FromSeconds(args.Contains("--atualizado") || args.Contains("--aguardar") ? 15 : 0)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Com o início junto com o Windows, o comum é o Ponte já estar minimizado: traz a janela dele à frente.
            if (!args.Contains(Autostart.Argument) && !ShowRunningCopy())
                MessageBox.Show("O Ponte já está aberto neste computador.", "Ponte", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Updater.Cleanup();
        var host = new HostService(config);
        host.Start();
        using var form = new MainForm(config, host, args);
        if (Arg(args, "--captura-ui") is { } captureDir)
        {
            form.Shown += async (_, _) =>
            {
                // Dá tempo de as bolinhas de online chegarem: no Windows, uma porta fechada leva uns 2 s para recusar.
                await Task.Delay(3500);
                Directory.CreateDirectory(captureDir);
                form.SaveWindowImage(Path.Combine(captureDir, "principal.png"));
                // As janelas secundárias entram na captura sem depender de alguém clicar até elas.
                var dialogs = new (string File, Form Dialog)[]
                {
                    ("pedido.png", new AcceptDialog(new IncomingRequest("NOTEBOOK-CASA", "maria", "pelo servidor de retransmissão", config.Id, true))),
                    ("salvar.png", new SaveComputerDialog("", "", true, false)),
                    ("senha.png", new PasswordPromptDialog("123 456 789", true)),
                    ("autorizados.png", new TrustedDialog(config)),
                    ("configuracoes.png", new SettingsDialog(config, false)),
                    ("celular.png", new AndroidAppDialog(config)),
                    ("conta.png", new AccountDialog()),
                    ("transmissao.png", new BroadcastDialog(host, config)),
                    ("video.png", new VideoSettingsDialog(new VideoOptions(60, "h264"), "H.264 pela GPU (AMD) · 60 FPS alvo · 4:2:0 · captura DXGI")),
                    ("sessao-ativa.png", new SessionIndicator(new HostSession(new Channel(Stream.Null),
                        new HelloMsg("NOTEBOOK-CASA", "maria", Updater.Current.ToString(), null, Caps.Files), "pelo servidor de retransmissão", "", true, config)) { WindowState = FormWindowState.Normal }),
                };
                foreach (var (file, dialog) in dialogs)
                {
                    dialog.Show(form);
                    await Task.Delay(300);
                    // A janela do aplicativo para celular baixa o APK antes de mostrar o QR Code.
                    for (int i = 0; i < 150 && dialog is AndroidAppDialog { Ready: false }; i++) await Task.Delay(200);
                    Ui.SaveImage(dialog, Path.Combine(captureDir, file));
                    dialog.Close();
                    dialog.Dispose();
                }
                form.Close();
            };
        }
        Application.Run(form);
        single.ReleaseMutex();
        return 0;
    }

    // Pede à cópia aberta que mostre a janela, mesmo escondida no ícone perto do relógio (aí ela não tem janela
    // principal visível para trazer à frente). Falso quando não há outra cópia do Ponte.
    private static bool ShowRunningCopy()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var others = System.Diagnostics.Process.GetProcessesByName(self.ProcessName)
            .Where(p => p.Id != self.Id).Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        if (others.Count == 0 || MainForm.ShowMessage == 0) return false;
        bool asked = false;
        // A própria janela trata o pedido e se põe à frente; as outras janelas da cópia ignoram a mensagem.
        Native.AllowSetForegroundWindow(-1);
        Native.EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint pid);
            if (others.Contains(pid)) asked |= Native.PostMessageW(window, MainForm.ShowMessage, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return asked;
    }

    // Janela que fica na frente por alguns segundos. Aberta com --elevar, reproduz a situação de um programa
    // de administrador em primeiro plano, que é quando o Windows bloqueia a entrada de um programa comum.
    // Só a janela de assistir a uma transmissão, sem o resto do Ponte. Com arquivo, grava a imagem dela depois de a
    // transmissão aparecer (ou de 20 s) e fecha: é assim que a verificação confere o que se vê.
    private static int Watch(AppConfig config, string link, string? image)
    {
        (string Server, string Token)? stream;
        try { stream = StreamViewerForm.Parse(link, config); }
        catch (PonteException ex)
        {
            if (image is null) MessageBox.Show(ex.Message, "Ponte", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        if (stream is not { } s)
        {
            if (image is null) MessageBox.Show("Esse não é um link de transmissão do Ponte.", "Ponte", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        using var form = new StreamViewerForm(config, s.Server, s.Token);
        if (image is not null)
        {
            form.Shown += async (_, _) =>
            {
                for (int i = 0; i < 40 && !form.Watching; i++) await Task.Delay(500);
                await Task.Delay(3000);
                Ui.SaveImage(form, image);
                form.Close();
            };
        }
        Application.Run(form);
        return form.Watching ? 0 : 2;
    }

    private static int TestWindow(int seconds)
    {
        using var form = new Form
        {
            Text = "Ponte - janela de teste" + (Elevation.IsElevated ? " (administrador)" : ""),
            Size = new Size(760, 460), StartPosition = FormStartPosition.CenterScreen, TopMost = true, Icon = Ui.MakeIcon(),
        };
        form.Controls.Add(new Label
        {
            Text = $"Janela de teste do Ponte, {(Elevation.IsElevated ? "aberta como administrador" : "sem administrador")}.\nFecha sozinha em {seconds} segundos.",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        });
        using var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) => form.Close();
        form.Shown += (_, _) =>
        {
            // O Windows só entrega o primeiro plano a quem acabou de receber entrada; um toque no Alt resolve.
            InputInjector.Key(0x12, false, false);
            InputInjector.Key(0x12, true, false);
            Native.SetForegroundWindow(form.Handle);
            form.Activate();
            timer.Start();
        };
        Application.Run(form);
        return 0;
    }

    // Atualização sem janela: mesma checagem de assinatura do programa, com o resultado gravado num arquivo.
    private static int UpdateNow(string log, string[] args)
    {
        void Write(string line) => File.AppendAllText(log, line + Environment.NewLine, new UTF8Encoding(false));
        var config = AppConfig.Load(Arg(args, "--perfil"));
        if (Arg(args, "--relay") is { } relay) config.RelayServer = relay;
        Write($"versão em execução: {Updater.Current}; servidor: {config.RelayServer}");
        try
        {
            var manifest = Task.Run(() => Updater.CheckAsync(config, CancellationToken.None)).GetAwaiter().GetResult();
            if (manifest is null)
            {
                Write("sem atualização: esta já é a versão mais nova que o servidor oferece");
                return 0;
            }
            string path = Task.Run(() => Updater.DownloadAsync(config, manifest, CancellationToken.None)).GetAwaiter().GetResult();
            Updater.Apply(path, manifest, new[] { "--pos-atualizacao", log, "--sem-elevar" });
            Write($"atualizado de {Updater.Current} para {manifest.Version}");
            return 0;
        }
        catch (PonteException ex)
        {
            Write("RECUSADO: " + ex.Message);
            return 2;
        }
    }

    // Sessão de demonstração: o "outro computador" é esta mesma máquina, só em 127.0.0.1 e com senha descartável.
    private static int Demo(string dir, string[] args)
    {
        Directory.CreateDirectory(dir);
        var hostConfig = AppConfig.Load(Path.Combine(dir, "perfil-host"));
        hostConfig.ListenPort = SelfTest.FreePort();
        hostConfig.DirectEnabled = true;
        hostConfig.RelayServer = "";
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        hostConfig.SetPassword(password);
        string received = Path.Combine(dir, "recebidos");
        using var host = new HostService(hostConfig) { ReceiveDir = received };
        host.Start(IPAddress.Loopback);

        var viewerConfig = AppConfig.Load(Path.Combine(dir, "perfil-visualizador"));
        ViewerSession session;
        try
        {
            session = Task.Run(() => ViewerSession.ConnectAsync(viewerConfig, $"127.0.0.1:{hostConfig.ListenPort}",
                () => Task.FromResult<string?>(password), () => Task.FromResult(true), received, CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (PonteException ex)
        {
            File.WriteAllText(Path.Combine(dir, "erro.txt"), ex.Message);
            return 1;
        }

        // Com --video h264|h265 a demonstração liga o vídeo e anota, depois de alguns segundos, quantos quadros
        // chegaram e quantos foram desenhados na janela.
        string? codec = Arg(args, "--video");
        if (codec is not null)
        {
            viewerConfig.Profile = StreamProfile.Default;
            viewerConfig.Profiles[StreamProfile.Default] = new StreamProfile(60, new VideoOptions(60, codec, Chroma444: args.Contains("--444")));
        }
        using var form = new ViewerForm(session, codec is null ? null : viewerConfig);
        form.Shown += async (_, _) =>
        {
            await Task.Delay(codec is null ? 4000 : 9000);
            if (codec is not null)
                File.WriteAllText(Path.Combine(dir, "desenho.txt"), $"{form.StatsText}\r\ndesenho: {form.PaintMilliseconds:N1} ms por quadro\r\n");
            form.SaveWindowImage(Path.Combine(dir, "sessao.png"));
            form.Close();
        };
        Application.Run(form);
        return 0;
    }

    /// <summary>Valor que vem logo depois de um argumento (ou de um valor anterior, para argumentos em sequência).</summary>
    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
