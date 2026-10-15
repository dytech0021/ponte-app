using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ponte;

/// <summary>
/// Teste de ponta a ponta numa máquina só: sobe o lado controlado em 127.0.0.1, conecta o lado
/// visualizador e confere autenticação, imagem, mouse, teclado, arquivos e área de transferência.
/// Com --relay, o caminho passa pelo servidor de retransmissão e a conexão é feita pelo ID.
/// </summary>
internal static partial class SelfTest
{
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static async Task<int> RunAsync(string dir, string? relay, bool onlyVideo = false, bool onlyAccount = false, bool onlyBroadcast = false)
    {
        Directory.CreateDirectory(dir);
        SessionLog.Folder = dir;
        File.Delete(SessionLog.FilePath);
        var log = new StringBuilder();
        bool allOk = true;
        // Além do resultado final, cada linha vai na hora para um arquivo à parte: se o teste travar, dá para ver onde.
        string progress = Path.Combine(dir, "andamento.txt");
        File.WriteAllText(progress, "");
        void Check(string name, bool ok, string detail = "", bool skipped = false)
        {
            if (!skipped) allOk &= ok;
            string line = $"[{(skipped ? "NÃO VERIFICADO" : ok ? "OK" : "FALHA")}] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}";
            log.AppendLine(line);
            File.AppendAllText(progress, line + Environment.NewLine);
        }

        try
        {
            if (onlyAccount) await CheckAccountAsync(dir, Check);
            else if (onlyBroadcast) await CheckBroadcastOnlyAsync(dir, relay, Check);
            else await RunChecksAsync(dir, relay, Check, onlyVideo);
        }
        catch (Exception ex)
        {
            Check("execução do teste", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        log.AppendLine(allOk ? "RESULTADO: tudo certo" : "RESULTADO: houve falha");
        File.WriteAllText(Path.Combine(dir, "autoteste.txt"), log.ToString(), new UTF8Encoding(true));
        return allOk ? 0 : 1;
    }

    private delegate void Checker(string name, bool ok, string detail = "", bool skipped = false);

    private static async Task RunChecksAsync(string dir, string? relay, Checker check, bool onlyVideo)
    {
        var ct = CancellationToken.None;
        string received = Path.Combine(dir, "recebidos-no-controlado"), receivedBack = Path.Combine(dir, "recebidos-no-visualizador");

        var hostConfig = AppConfig.Load(Path.Combine(dir, "perfil-controlado"));
        hostConfig.ListenPort = FreePort();
        hostConfig.DirectEnabled = true;
        hostConfig.RelayServer = relay ?? "";
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        hostConfig.SetPassword(password);

        // A pasta pode ser de uma rodada anterior: nada de autorização antiga interferindo.
        hostConfig.Trusted.Clear();

        var viewerConfig = AppConfig.Load(Path.Combine(dir, "perfil-visualizador"));
        viewerConfig.RelayServer = relay ?? "";
        viewerConfig.KnownHosts.Clear();
        viewerConfig.Saved.Clear();

        using var host = new HostService(hostConfig) { ReceiveDir = received };
        host.Start(IPAddress.Loopback);
        string target = $"127.0.0.1:{hostConfig.ListenPort}";
        if (relay is not null)
        {
            for (int i = 0; i < 100 && !host.RelayOnline; i++) await Task.Delay(100);
            check("registro do ID no servidor de retransmissão", host.RelayOnline, host.RelayStatus);
            target = hostConfig.Id;
        }
        check("caminho testado", true, relay is null ? $"direto, {target}" : $"por ID {Security.FormatId(target)} via {relay}");
        check("modo de execução", true, $"versão {Updater.Current}, {(Elevation.IsElevated ? "como administrador" : "sem administrador")}");

        // Bolinha de online dos salvos: o controlado aberto aparece online (pelo IP e, com servidor, pelo ID); uma
        // porta e um ID sem ninguém aparecem offline.
        int closedPort = FreePort();
        var probe = new List<SavedEntry>
        {
            new() { Key = $"ip:127.0.0.1:{hostConfig.ListenPort}", Address = $"127.0.0.1:{hostConfig.ListenPort}" },
            new() { Key = $"ip:127.0.0.1:{closedPort}", Address = $"127.0.0.1:{closedPort}" },
        };
        if (relay is not null)
        {
            probe.Add(new() { Key = "id:" + hostConfig.Id, Address = hostConfig.Id });
            probe.Add(new() { Key = "id:100000000", Address = "100000000" });
        }
        var seen = await Presence.CheckAsync(viewerConfig, probe, ct);
        bool Is(string key, bool expected) => seen.TryGetValue(key, out bool on) && on == expected;
        check("bolinha de online: Ponte aberto aparece online e ausente, offline",
            Is(probe[0].Key, true) && Is(probe[1].Key, false) && (relay is null || (Is(probe[2].Key, true) && Is(probe[3].Key, false))),
            string.Join(", ", probe.Select(p => $"{p.Key}={(seen.TryGetValue(p.Key, out bool on) ? on ? "online" : "offline" : "sem resposta")}")));

        // Início com o Windows: grava, lê e remove uma entrada de teste, sem tocar na do Ponte de uso.
        const string testEntry = "Ponte-autoteste", testCommand = "\"C:\\teste\\Ponte.exe\" --inicio";
        Autostart.Write(testEntry, null);
        bool created = Autostart.Write(testEntry, testCommand);
        string? stored = Autostart.Read(testEntry);
        Autostart.Write(testEntry, null);
        check("início com o Windows grava e remove a entrada", created && stored == testCommand && Autostart.Read(testEntry) is null);

        // QR Code do aplicativo para celular: as matrizes têm de ser as de uma implementação de referência
        // (biblioteca qrcode do Python, modo byte, nível M), comparadas pelo SHA-256 dos módulos.
        var qrCases = new (string Text, int Mask, string Sha)[]
        {
            ("http://192.168.100.200:7413/Ponte.apk", 0, "8000e241130d4f3ccc476568c81a3c7e7f05585fa2cf3baa11b6c0fdac3e2a99"),
            (new string('A', 120) + " fim do texto com versão 8", 5, "4d273c2662ec914dbfb78400a9967def3445d077c96ddc362e832d4dac644912"),
            (new string('Z', 190), 0, "c071dd59ee6aafd34d2925898bc0166fff0b96c27c6bb351e93d2eabe6a194b8"),
        };
        var qrWrong = qrCases.Where(c => QrHash(QrCode.Encode(c.Text, c.Mask)) != c.Sha).Select(c => $"{c.Text.Length} letras, máscara {c.Mask}").ToList();
        check("QR Code igual ao da implementação de referência", qrWrong.Count == 0, string.Join("; ", qrWrong));
        check("arquivo que não é APK não passa pela conferência de assinatura", AndroidApp.SignerDigest(RandomNumberGenerator.GetBytes(4096)) is null);
        if (!onlyVideo) await CheckAccountAsync(dir, check);
        if (relay is not null && !onlyVideo) await CheckBroadcastAsync(host, hostConfig, check);
        if (relay is not null) await CheckAndroidAppAsync(viewerConfig, check);

        Task<ViewerSession> Connect(string? typed) => ViewerSession.ConnectAsync(viewerConfig, target,
            () => Task.FromResult(typed), () => Task.FromResult(true), receivedBack, ct);

        async Task<string> Refused(string? typed)
        {
            try
            {
                await (await Connect(typed)).DisposeAsync();
                return "";
            }
            catch (PonteException ex) { return ex.Message; }
        }

        // 1. Autenticação
        string reason = await Refused("senha-errada");
        check("senha errada é recusada", reason.Length > 0, reason);

        host.AskPermission = _ => Task.FromResult(Permission.Refuse);
        reason = await Refused("");
        check("pedido sem senha recusado na tela não entra", reason.Length > 0, reason);

        host.AskPermission = _ => Task.FromResult(Permission.Once);
        var prompted = await Connect("");
        check("pedido sem senha aceito na tela entra", true, prompted.HostName);
        check("aceite simples não deixa o computador autorizado", !prompted.Paired && hostConfig.TrustedSnapshot().Length == 0);
        prompted.Start();
        await prompted.DisposeAsync();
        await WaitAsync(() => host.Current is null);
        await WaitAsync(() => File.Exists(SessionLog.FilePath) && File.ReadAllText(SessionLog.FilePath).Contains("controlado: fim"));
        string[] sessionLines = File.Exists(SessionLog.FilePath) ? File.ReadAllLines(SessionLog.FilePath) : [];
        check("registro de sessões anota início, fim e motivo dos dois lados",
            sessionLines.Any(l => l.Contains("controlado: começou")) && sessionLines.Any(l => l.Contains("acessando: começou")) &&
            sessionLines.Any(l => l.Contains("controlado: fim") && l.Contains("o outro computador encerrou")) &&
            sessionLines.Any(l => l.Contains("acessando: fim") && l.Contains("encerrada neste computador")),
            string.Join(" | ", sessionLines.Select(l => l.Length > 160 ? l[..160] : l)));

        // 2. Sessão com senha
        var session = await Connect(password);
        check("senha correta entra", true, session.HostName);
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FrameUpdated += () => firstFrame.TrySetResult();
        var pointer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PointerChanged += payload => pointer.TrySetResult(payload);
        (int X, int Y, bool Inside)? reported = null;
        session.PointerMoved += (x, y, inside) => reported = (x, y, inside);
        long soundSamples = 0;
        int soundPeak = 0;
        session.SoundReceived += (samples, _, _) =>
        {
            Interlocked.Add(ref soundSamples, samples.Length);
            foreach (short sample in samples) soundPeak = Math.Max(soundPeak, Math.Abs((int)sample));
        };
        var watch = Stopwatch.StartNew();
        session.Start();
        bool gotFrame = await Task.WhenAny(firstFrame.Task, Task.Delay(15000)) == firstFrame.Task;
        check("primeira imagem recebida", gotFrame, $"{watch.ElapsedMilliseconds} ms, {session.BytesReceived / 1024} KB");

        var primary = Screen.PrimaryScreen!.Bounds;
        lock (session.CanvasLock)
        {
            var canvas = session.Canvas;
            check("imagem tem o tamanho da tela", canvas is not null && canvas.Width == primary.Width && canvas.Height == primary.Height,
                canvas is null ? "sem imagem" : $"{canvas.Width} x {canvas.Height}");
            canvas?.Save(Path.Combine(dir, "quadro.png"), ImageFormat.Png);
        }

        if (onlyVideo)
        {
            await CheckVideoAsync(session, viewerConfig, relay, dir, check);
            await session.DisposeAsync();
            return;
        }

        reason = await Refused(password);
        check("segunda sessão simultânea é recusada", reason.Length > 0, reason);

        // O ponteiro do mouse vem à parte da imagem: logo no começo e, depois, só quando muda de forma.
        byte[]? shape = await Task.WhenAny(pointer.Task, Task.Delay(5000)) == pointer.Task ? pointer.Task.Result : null;
        using (var remotePointer = shape is null ? null : RemotePointer.FromPayload(shape))
        {
            check("forma do ponteiro do mouse chega ao visualizador", shape is not null && (remotePointer is not null || shape[0] == 0),
                shape is null ? "não chegou" : remotePointer is null ? "escondido no momento"
                    : $"{remotePointer.Size.Width} x {remotePointer.Size.Height}, ponto ativo em {remotePointer.HotSpot.X},{remotePointer.HotSpot.Y}");
        }
        if (shape is { Length: > 5 }) File.WriteAllBytes(Path.Combine(dir, "ponteiro.png"), shape[5..]);

        // As outras formas comuns, entre elas a barra de texto, que no Windows não tem cor própria: inverte a de baixo.
        var shapes = new (string Name, Cursor Cursor)[] { ("texto", Cursors.IBeam), ("mao", Cursors.Hand), ("redimensionar", Cursors.SizeNWSE), ("cruz", Cursors.Cross) };
        var broken = new List<string>();
        foreach (var (name, cursor) in shapes)
        {
            byte[]? encoded = PointerWatcher.Encode(cursor.Handle);
            using var decoded = encoded is null ? null : RemotePointer.FromPayload(encoded);
            if (decoded is null) broken.Add(name);
            else File.WriteAllBytes(Path.Combine(dir, $"ponteiro-{name}.png"), encoded![5..]);
        }
        check("ponteiros de texto, mão, redimensionar e cruz viram imagem", broken.Count == 0, string.Join(", ", broken));

        string thumbnail = Path.Combine(dir, "miniatura.jpg");
        bool thumbnailOk = session.SaveThumbnail(thumbnail);
        if (thumbnailOk)
        {
            using var small = Image.FromFile(thumbnail);
            thumbnailOk = small.Width == 320 && small.Height == 180;
        }
        check("miniatura da tela é gravada", thumbnailOk);

        // 3. Mouse: move o cursor de verdade, confere e devolve ao lugar.
        var notice = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Notice += text => notice.TrySetResult(text);
        Native.GetCursorPos(out var before);
        int tx = primary.Width / 2 + 37, ty = primary.Height / 2 + 21;
        await session.SendMouseAsync((int)Math.Round(tx * 65535.0 / (primary.Width - 1)), (int)Math.Round(ty * 65535.0 / (primary.Height - 1)), 0, 0);
        await Task.Delay(400);
        if (InputInjector.LastBlocked)
        {
            // Com uma janela de administrador em primeiro plano o Windows recusa a entrada; o que dá para
            // conferir nessa hora é se o visualizador foi avisado.
            bool warned = await Task.WhenAny(notice.Task, Task.Delay(3000)) == notice.Task;
            check("mouse e teclado remotos", false, "há uma janela de administrador em primeiro plano e o Windows bloqueia a entrada", skipped: true);
            check("bloqueio de entrada é avisado ao visualizador", warned);
        }
        else
        {
            Native.GetCursorPos(out var after);
            check("mouse remoto move o cursor", Math.Abs(after.X - (primary.X + tx)) <= 2 && Math.Abs(after.Y - (primary.Y + ty)) <= 2,
                $"pedido {primary.X + tx},{primary.Y + ty}; ficou em {after.X},{after.Y}");

            // A posição do ponteiro volta ao visualizador, para o celular desenhá-lo onde ele está.
            await WaitAsync(() => reported is { Inside: true } r && (int)Math.Round(r.X * (primary.Width - 1) / 65535.0) == after.X - primary.X, 20);
            var told = reported;
            check("posição do ponteiro chega ao visualizador", told is { Inside: true } &&
                    (int)Math.Round(told.Value.X * (primary.Width - 1) / 65535.0) == after.X - primary.X &&
                    (int)Math.Round(told.Value.Y * (primary.Height - 1) / 65535.0) == after.Y - primary.Y,
                told is { } t ? $"{t.X},{t.Y} na escala de 0 a 65535" : "não chegou");

            // Precisão: o ponto da janela da sessão tem de virar o pixel exato de lá, com a imagem no
            // tamanho real e com ela reduzida (que é quando as contas arredondam).
            var misses = new List<string>();
            foreach (double zoom in new[] { 1.0, 0.61 })
            {
                var view = new Size((int)(primary.Width * zoom), (int)(primary.Height * zoom));
                foreach (var (fx, fy) in new[] { (0.137, 0.211), (0.5, 0.5), (0.731, 0.389), (0.903, 0.847) })
                {
                    var local = new Point((int)(view.Width * fx), (int)(view.Height * fy));
                    var sent = RemoteView.ToRemote(local, view, primary.Width, primary.Height);
                    // O pixel de lá que fica debaixo do centro do pixel daqui, na área que a imagem ocupa de fato.
                    var shown = RemoteView.Fit(view, primary.Width, primary.Height);
                    int wantX = primary.X + (int)Math.Floor((local.X - shown.X + 0.5) * primary.Width / shown.Width);
                    int wantY = primary.Y + (int)Math.Floor((local.Y - shown.Y + 0.5) * primary.Height / shown.Height);
                    await session.SendMouseAsync(sent.X, sent.Y, 0, 0);
                    await Task.Delay(120);
                    Native.GetCursorPos(out var landed);
                    if (landed.X != wantX || landed.Y != wantY) misses.Add($"{zoom:0.##}x: pedido {wantX},{wantY}, ficou em {landed.X},{landed.Y}");
                }
            }
            check("mouse remoto cai no pixel exato", misses.Count == 0, string.Join("; ", misses));

            // Arrastar: apertar na barra de título de uma janela, mover e soltar tem de levar a janela junto.
            var (dragged, dragDetail) = await DragTestWindowAsync(session, primary);
            check("arrastar uma janela pela barra de título leva a janela junto", dragged, dragDetail);

            // "Tocar duas vezes e arrastar" do celular: um clique e, logo depois, o botão apertado alguns
            // pixels adiante. Tem de arrastar a janela, e não virar clique duplo (que a maximizaria). Com o
            // botão descendo no mesmo ponto do clique, o Windows maximiza: foi o defeito da 1.5.3.
            var (tapDragged, tapDetail) = await DragTestWindowAsync(session, primary, tapFirst: true, nudge: 10);
            check("tocar duas vezes e arrastar leva a janela sem maximizar", tapDragged, tapDetail);
            Native.SetCursorPos(before.X, before.Y);

            // 4. Teclado: F24 não existe em teclado comum, então não interfere em nenhum programa aberto.
            const int F24 = 0x87;
            await session.SendKeyAsync(F24, false, false);
            await Task.Delay(300);
            bool down = (Native.GetAsyncKeyState(F24) & 0x8000) != 0;
            await session.SendKeyAsync(F24, true, false);
            await Task.Delay(300);
            bool up = (Native.GetAsyncKeyState(F24) & 0x8000) == 0;
            check("tecla remota é pressionada e solta", down && up, $"pressionada={down}, solta={up}");

            // Texto pronto, como o aplicativo do celular manda: acentos, símbolo, par substituto e Enter.
            const string typedText = "Ponte: ação, çãé € \U0001F600\n";
            string injected = await CaptureInjectedTextAsync(() => session.SendTextAsync(typedText));
            check("texto remoto é digitado caractere a caractere", injected == typedText, $"{injected.Length} de {typedText.Length} caracteres");
        }

        // Som: primeiro a compressão, que é só conta; depois a captura de verdade, com um tom baixo e curto.
        check("som comprimido volta parecido com o original", AdpcmKeepsSound(out string soundDetail), soundDetail);
        check("som comprimido confere com o vetor de referência", AdpcmMatchesReference());
        int shownScreen = Math.Max(0, Array.FindIndex(session.Screens, s => s.Primary));
        await session.SendSettingsAsync(shownScreen, 60, sound: true);
        await WaitAsync(() => host.Current is { SoundOn: true, SoundProblem: "" });
        if (host.Current is not { SoundOn: true, SoundProblem: "" })
        {
            check("som do computador chega ao visualizador", false, "este computador não tem saída de som em uso: " + host.Current?.SoundProblem, skipped: true);
        }
        else
        {
            using (var player = new AudioPlayer())
            {
                player.Play(Tone(180, 60, 1.0), AudioCodec.Rate, AudioCodec.Channels);
                await WaitAsync(() => Volatile.Read(ref soundPeak) > 1, 30);
            }
            bool heard = soundPeak > 1;
            check("som do computador chega ao visualizador", heard,
                heard ? $"{Interlocked.Read(ref soundSamples) / AudioCodec.Channels} amostras" : "nada foi capturado; a saída de som deve estar muda", skipped: !heard);
        }
        await session.SendSettingsAsync(shownScreen, 60, sound: false);
        check("som para quando o visualizador o dispensa", await WaitAsync(() => host.Current is { SoundOn: false }));

        // 4b. Vídeo: H.264, H.265, 4:4:4 e a volta ao modo de sempre
        await CheckVideoAsync(session, viewerConfig, relay, dir, check);

        // 5. Arquivos nos dois sentidos
        byte[] data = RandomNumberGenerator.GetBytes(300_000);
        string source = Path.Combine(dir, "envio.bin");
        File.WriteAllBytes(source, data);

        var arrived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.FileReceived += path => arrived.TrySetResult(path);
        await session.SendFileAsync(source, null);
        string? there = await Task.WhenAny(arrived.Task, Task.Delay(10000)) == arrived.Task ? arrived.Task.Result : null;
        check("arquivo do visualizador chega ao controlado", there is not null && File.ReadAllBytes(there).AsSpan().SequenceEqual(data), there ?? "não chegou");

        var returned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FileReceived += path => returned.TrySetResult(path);
        await host.Current!.SendFileAsync(source, null);
        string? back = await Task.WhenAny(returned.Task, Task.Delay(10000)) == returned.Task ? returned.Task.Result : null;
        check("arquivo do controlado chega ao visualizador", back is not null && File.ReadAllBytes(back).AsSpan().SequenceEqual(data), back ?? "não chegou");

        // 6. Área de transferência: confere a entrega do texto sem mexer na área de transferência real.
        var clip = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ClipboardReceived += text => clip.TrySetResult(text);
        const string sample = "Ponte: texto de teste, com acentuação";
        await session.SendClipboardAsync(sample);
        string? got = await Task.WhenAny(clip.Task, Task.Delay(5000)) == clip.Task ? clip.Task.Result : null;
        check("texto copiado chega ao outro lado", got == sample, got ?? "não chegou");

        // 7. Encerramento
        await session.DisposeAsync();
        check("sessão termina no computador controlado", await WaitAsync(() => host.Current is null));

        // 8. Computador autorizado: entra sem senha e sem aceite, e só ele.
        check("entrar com a senha, sem estar salvo, não autoriza", hostConfig.TrustedSnapshot().Length == 0);
        host.AskPermission = _ => Task.FromResult(Permission.Refuse);
        var strangerConfig = AppConfig.Load(Path.Combine(dir, "perfil-estranho"));
        strangerConfig.RelayServer = relay ?? "";
        strangerConfig.KnownHosts.Clear();

        check("visualizador de versão anterior ainda entra com a senha", await LegacyViewerEntersAsync(strangerConfig, target, password, ct));
        await WaitAsync(() => host.Current is null);

        // typed nulo desiste se a senha for pedida, então só entra quem a autorização deixar passar.
        async Task<(bool Entered, bool Paired, string Reason)> Try(AppConfig config, string? typed)
        {
            try
            {
                var attempt = await ViewerSession.ConnectAsync(config, target, () => Task.FromResult(typed), () => Task.FromResult(true), receivedBack, ct);
                bool paired = attempt.Paired;
                await attempt.DisposeAsync();
                await WaitAsync(() => host.Current is null);
                return (true, paired, "");
            }
            catch (PonteException ex) { return (false, false, ex.Message); }
        }

        viewerConfig.SaveComputer(target, "autoteste", true);
        var first = await Try(viewerConfig, password);
        check("computador salvo que entra com a senha fica autorizado", first is { Entered: true, Paired: true } && hostConfig.TrustedSnapshot().Length == 1, first.Reason);

        var direct = await Try(viewerConfig, null);
        check("computador autorizado entra sem senha e sem aceite", direct is { Entered: true, Paired: true }, direct.Reason);

        var stranger = await Try(strangerConfig, "");
        check("outro computador continua precisando de aceite", !stranger.Entered, stranger.Reason);

        check("chave pública alheia sem a chave privada não entra", await ForgedKeyRefusedAsync(strangerConfig, viewerConfig, target, ct));

        hostConfig.RemoveTrusted(hostConfig.TrustedSnapshot().Select(t => t.KeyHash));
        var revoked = await Try(viewerConfig, null);
        check("autorização removida deixa de valer", !revoked.Entered, revoked.Reason);

        host.AskPermission = _ => Task.FromResult(Permission.Always);
        var always = await Try(viewerConfig, "");
        host.AskPermission = _ => Task.FromResult(Permission.Refuse);
        var next = await Try(viewerConfig, null);
        check("\"sempre permitir\" no aceite autoriza as próximas conexões", always is { Entered: true, Paired: true } && next.Entered, always.Reason + next.Reason);
    }

    // Observa, no nível do sistema, o que o controlado injetou ao receber um texto, e retém essas teclas:
    // assim o teste não digita nada no programa que a pessoa estiver usando. Só teclas injetadas são
    // retidas; o que ela digitar de verdade no meio do teste passa normalmente.
    private static async Task<string> CaptureInjectedTextAsync(Func<Task> send)
    {
        var captured = new StringBuilder();
        var hooked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool done = false;
        Native.LowLevelKeyboardProc callback = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var key = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                char? typed = key.vkCode switch { Native.VK_PACKET => (char)key.scanCode, 0x0D => '\n', 0x09 => '\t', _ => null };
                if ((key.flags & Native.LLKHF_INJECTED) != 0 && typed is { } c)
                {
                    if ((int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN) lock (captured) captured.Append(c);
                    return 1;
                }
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        // O gancho de teclado só funciona numa thread com fila de mensagens.
        var thread = new Thread(() =>
        {
            IntPtr hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, callback, Native.GetModuleHandleW(null), 0);
            hooked.TrySetResult(hook != IntPtr.Zero);
            if (hook == IntPtr.Zero) return;
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) => { if (Volatile.Read(ref done)) Application.ExitThread(); };
            timer.Start();
            Application.Run();
            Native.UnhookWindowsHookEx(hook);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!await hooked.Task) return "";
        await send();
        // Espera a digitação parar de chegar.
        for (int quiet = 0, last = -1, i = 0; i < 60 && quiet < 6; i++)
        {
            await Task.Delay(50);
            int now;
            lock (captured) now = captured.Length;
            quiet = now == last && now > 0 ? quiet + 1 : 0;
            last = now;
        }
        Volatile.Write(ref done, true);
        thread.Join(2000);
        GC.KeepAlive(callback);
        lock (captured) return captured.ToString();
    }

    private static string QrHash(bool[,] modules)
    {
        var bits = new StringBuilder(modules.Length);
        foreach (bool m in modules) bits.Append(m ? '1' : '0');
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(bits.ToString()))).ToLowerInvariant();
    }

    // O aplicativo para Android pelo caminho de verdade: baixado do servidor, conferido (tamanho, resumo e
    // chave que o assinou) e entregue pelo servidor da rede local, como o celular o recebe.
    private static async Task CheckAndroidAppAsync(AppConfig config, Checker check)
    {
        AndroidApp.Package package;
        try
        {
            package = await AndroidApp.DownloadAsync(config, CancellationToken.None);
        }
        catch (PonteException ex)
        {
            check("aplicativo para Android baixado do servidor e assinado pela chave do Ponte", false, ex.Message);
            return;
        }
        check("aplicativo para Android baixado do servidor e assinado pela chave do Ponte", true, $"versão {package.Version}, {package.Apk.Length / 1024} KB");

        using var server = new ApkServer(package);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            byte[] served = await http.GetByteArrayAsync($"http://127.0.0.1:{server.Port}/Ponte.apk");
            string page = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/");
            check("o celular baixa o aplicativo pela rede local", served.AsSpan().SequenceEqual(package.Apk) && page.Contains("href=\"/Ponte.apk\""),
                $"porta {server.Port}, {served.Length / 1024} KB entregues");
        }
        catch (HttpRequestException ex)
        {
            check("o celular baixa o aplicativo pela rede local", false, ex.Message);
        }
    }

    // Abre uma janela de teste por cima de tudo, aperta o botão na barra de título dela pela sessão, move
    // em passos e solta, como faz quem arrasta pelo celular ou pelo outro computador. Devolve se a janela
    // andou exatamente o que o mouse andou, sem maximizar. Com tapFirst, antes vem um clique no mesmo
    // ponto, como no "tocar duas vezes e arrastar" do celular, e o botão desce nudge pixels adiante, na
    // direção do arraste.
    private static async Task<(bool Moved, string Detail)> DragTestWindowAsync(ViewerSession session, Rectangle primary, bool tapFirst = false, int nudge = 0)
    {
        var shown = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "Ponte - teste de arrastar", StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false,
                Bounds = new Rectangle(primary.X + 120, primary.Y + 120, 420, 180), Icon = Ui.MakeIcon(),
            };
            form.Shown += (_, _) => shown.TrySetResult(form);
            Application.Run(form);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var window = await shown.Task;
        await Task.Delay(300);

        var (start, clientTop) = window.Invoke(() => (window.Bounds, window.RectangleToScreen(window.ClientRectangle).Top));
        // Meio da barra de título, longe dos botões de minimizar e fechar.
        var grab = new Point(start.X + 80, (start.Y + clientTop) / 2);
        const int moveX = 150, moveY = 80;
        int Nx(int x) => (int)Math.Round((x - primary.X) * 65535.0 / (primary.Width - 1));
        int Ny(int y) => (int)Math.Round((y - primary.Y) * 65535.0 / (primary.Height - 1));

        await session.SendMouseAsync(Nx(grab.X), Ny(grab.Y), 0, 0);
        await Task.Delay(150);
        if (tapFirst)
        {
            await session.SendMouseAsync(Nx(grab.X), Ny(grab.Y), 1, 0);
            await session.SendMouseAsync(Nx(grab.X), Ny(grab.Y), 2, 0);
            await Task.Delay(120);
        }
        double length = Math.Sqrt(moveX * moveX + moveY * moveY);
        var press = new Point(grab.X + (int)Math.Round(nudge * moveX / length), grab.Y + (int)Math.Round(nudge * moveY / length));
        await session.SendMouseAsync(Nx(press.X), Ny(press.Y), 1, 0);
        await Task.Delay(tapFirst ? 60 : 150);
        for (int i = 1; i <= 10; i++)
        {
            await session.SendMouseAsync(Nx(press.X + moveX * i / 10), Ny(press.Y + moveY * i / 10), 0, 0);
            await Task.Delay(40);
        }
        await session.SendMouseAsync(Nx(press.X + moveX), Ny(press.Y + moveY), 2, 0);
        await Task.Delay(400);

        var (end, state) = window.Invoke(() => (window.Bounds, window.WindowState));
        window.BeginInvoke(window.Close);
        thread.Join(2000);
        int dx = end.X - start.X, dy = end.Y - start.Y;
        if (state == FormWindowState.Maximized) return (false, "a janela foi maximizada (o Windows entendeu clique duplo)");
        return (Math.Abs(dx - moveX) <= 2 && Math.Abs(dy - moveY) <= 2, $"a janela andou {dx},{dy}; o mouse andou {moveX},{moveY}");
    }

    // Até a 1.1 o visualizador não mandava chave nem assinatura: tem de continuar entrando pela senha, sem ficar autorizado.
    private static async Task<bool> LegacyViewerEntersAsync(AppConfig config, string target, string password, CancellationToken ct)
    {
        var (ssl, remote) = await Transport.ConnectAsync(config, target, ct);
        await using var channel = new Channel(ssl);
        await channel.SendAsync(Msg.Hello, """{"Name":"antigo","User":"antigo","Version":"1.0"}"""u8.ToArray(), ct);
        var challenge = await channel.ExpectAsync<ChallengeMsg>(Msg.Challenge, ct);
        byte[] verifier = Security.DeriveVerifier(password, Convert.FromBase64String(challenge.Salt), challenge.Iterations);
        string proof = Convert.ToBase64String(Security.Proof(verifier, Convert.FromBase64String(challenge.Nonce), Security.CertificateHash(remote)));
        await channel.SendAsync(Msg.AuthResponse, Encoding.UTF8.GetBytes($$"""{"Proof":"{{proof}}"}"""), ct);
        var result = await channel.ExpectAsync<AuthResultMsg>(Msg.AuthResult, ct);
        return result is { Ok: true, Paired: false };
    }

    // Apresenta a chave pública de um computador autorizado, mas assina com outra: tem de ser tratado como desconhecido.
    private static async Task<bool> ForgedKeyRefusedAsync(AppConfig attacker, AppConfig victim, string target, CancellationToken ct)
    {
        var (ssl, remote) = await Transport.ConnectAsync(attacker, target, ct);
        await using var channel = new Channel(ssl);
        string victimKey = Convert.ToBase64String(victim.Certificate.PublicKey.ExportSubjectPublicKeyInfo());
        await channel.SendJsonAsync(Msg.Hello, new HelloMsg("falso", "falso", "1.0", victimKey), ct);
        var challenge = await channel.ExpectAsync<ChallengeMsg>(Msg.Challenge, ct);
        string signature = Security.SignPairing(attacker.Certificate, Convert.FromBase64String(challenge.Nonce), Security.CertificateHash(remote));
        await channel.SendJsonAsync(Msg.AuthResponse, new AuthResponseMsg(null, signature, true), ct);
        var result = await channel.ExpectAsync<AuthResultMsg>(Msg.AuthResult, ct);
        // Trusted verdadeiro confirma que o teste passou mesmo pelo caminho do computador autorizado.
        return challenge.Trusted && !result.Ok;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int tenths = 50)
    {
        for (int i = 0; i < tenths && !condition(); i++) await Task.Delay(100);
        return condition();
    }

    private static short[] Tone(double hertz, int amplitude, double seconds)
    {
        var samples = new short[(int)(AudioCodec.Rate * seconds) * AudioCodec.Channels];
        for (int i = 0; i < samples.Length; i += 2)
            samples[i] = samples[i + 1] = (short)(amplitude * Math.Sin(2 * Math.PI * hertz * (i / 2) / AudioCodec.Rate));
        return samples;
    }

    // Os mesmos bytes e as mesmas amostras estão no teste do aplicativo Android (CoreTest): se as duas
    // implementações conferem com este vetor, uma entende o que a outra produz.
    private static bool AdpcmMatchesReference()
    {
        short[] source =
        {
            0, 0, 1000, -1000, 5000, -3000, 12000, -9000, 32767, -32768, 20000, -20000,
            3000, 2500, -4000, 7000, -15000, 15000, -32768, 32767, -100, 100, 0, 0,
        };
        short[] decoded =
        {
            0, 0, 11, -11, 41, -41, 104, -104, 240, -240, 533, -533,
            1164, 98, -193, 1455, -3103, 4365, -9339, 10601, 467, 795, -838, -510,
        };
        const string encoded = "020280BB0000000000000000000000F7F7F7F7F7777F7F7FD588";
        byte[] payload = AudioCodec.Encode(source, 48000, false, new AudioCodec.State[AudioCodec.Channels]);
        bool first = Convert.ToHexString(payload) == encoded &&
                     AudioCodec.Decode(payload, out int rate, out int channels) is { } back && back.AsSpan().SequenceEqual(decoded) && rate == 48000 && channels == 2;
        // Um pedaço do meio de uma sessão: o estado de cada canal vem na mensagem, e não parte do zero.
        bool second = AudioCodec.Decode(Convert.FromHexString("020244AC00002EFB280029094D008784"), out rate, out _) is { } middle &&
                      middle.AsSpan().SequenceEqual(new short[] { -603, 910, 211, -395 }) && rate == 44100;
        return first && second;
    }

    // Comprime e descomprime meio segundo de som em pedaços de 20 ms, como na sessão, e mede quanto se perdeu.
    private static bool AdpcmKeepsSound(out string detail)
    {
        short[] original = Tone(440, 9000, 0.5);
        for (int i = 0; i < original.Length; i += 2)
            original[i + 1] = (short)(original[i + 1] / 2 + 3000 * Math.Sin(2 * Math.PI * 1500 * (i / 2) / AudioCodec.Rate));
        var state = new AudioCodec.State[AudioCodec.Channels];
        int chunk = AudioCodec.Rate / 50 * AudioCodec.Channels, bytes = 0;
        double signal = 0, noise = 0;
        for (int at = 0; at + chunk <= original.Length; at += chunk)
        {
            byte[] payload = AudioCodec.Encode(original.AsSpan(at, chunk), AudioCodec.Rate, false, state);
            bytes += payload.Length;
            if (AudioCodec.Decode(payload, out int rate, out int channels) is not { } back || back.Length != chunk || rate != AudioCodec.Rate || channels != AudioCodec.Channels)
            {
                detail = "o pedaço não voltou inteiro";
                return false;
            }
            for (int i = 0; i < chunk; i++)
            {
                signal += (double)original[at + i] * original[at + i];
                noise += (double)(original[at + i] - back[i]) * (original[at + i] - back[i]);
            }
        }
        byte[] exact = AudioCodec.Encode(original.AsSpan(0, chunk), AudioCodec.Rate, true, state);
        bool lossless = AudioCodec.Decode(exact, out _, out _) is { } same && same.AsSpan().SequenceEqual(original.AsSpan(0, chunk));
        double ratio = 10 * Math.Log10(signal / Math.Max(1, noise));
        detail = $"sinal/ruído de {ratio:0} dB a {bytes * 8 * 2 / 1000} kbit/s; sem compressão {(lossless ? "volta idêntico" : "NÃO volta idêntico")}";
        return ratio >= 25 && lossless;
    }
}
