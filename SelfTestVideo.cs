using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Ponte;

// Parte de vídeo do autoteste: liga H.264 e H.265 na sessão aberta e confere, pela tela de verdade, os
// quadros por segundo, as cores, o 4:4:4 e o atraso entre a tela mudar e a imagem mudar do lado de cá.
internal static partial class SelfTest
{
    private static async Task CheckVideoAsync(ViewerSession session, AppConfig viewerConfig, string? relay, string dir, Checker check)
    {
        if (!session.HostHasVideo)
        {
            check("opções de vídeo oferecidas pelo outro computador", false, "o lado controlado não anunciou vídeo");
            return;
        }

        // O componente de vídeo: do servidor, como os computadores o recebem, ou o que já estiver aqui.
        if (relay is not null)
        {
            long? size = await RemoteSizeAsync(viewerConfig, "VIDEO", VideoRuntime.Sha256);
            check("componente de vídeo disponível no servidor", size is > 1_000_000, size is { } s ? $"{s / (1024 * 1024)} MB comprimido" : "o servidor não tem o pacote");
        }
        bool had = VideoRuntime.Present;
        var watch = Stopwatch.StartNew();
        try
        {
            await VideoRuntime.EnsureAvailableAsync(viewerConfig, null, CancellationToken.None);
            VideoRuntime.VerifiedPath();
            check("componente de vídeo pronto e conferido", true, had ? "já estava neste computador" : $"baixado e conferido em {watch.Elapsed.TotalSeconds:N0} s");
        }
        catch (Exception ex) when (ex is PonteException or IOException)
        {
            check("componente de vídeo pronto e conferido", false, ex.Message, skipped: relay is null);
            return;
        }

        int screen = Math.Max(0, Array.FindIndex(session.Screens, s => s.Primary));
        var primary = Screen.PrimaryScreen!.Bounds;
        using var pattern = await VideoPattern.ShowAsync(primary);
        var probe = new FrameProbe(session);
        session.FrameUpdated += probe.OnFrame;
        string? ended = null;
        session.Closed += reason => ended = reason.Length > 0 ? reason : "encerrada";
        try
        {
            var modes = new (string Name, VideoOptions Options)[]
            {
                ("H.264 a 60 FPS", new VideoOptions(60, "h264")),
                ("H.265 a 60 FPS", new VideoOptions(60, "h265")),
                ("H.264 a 120 FPS com ultra baixa latência", new VideoOptions(120, "h264", UltraLowLatency: true)),
            };
            foreach (var (name, options) in modes)
            {
                if (ended is not null)
                {
                    check("a sessão continua aberta durante as trocas de vídeo", false, ended);
                    return;
                }
                pattern.BringToTop();
                int since = session.VideoStatusVersion;
                await session.SendSettingsAsync(screen, 60, false, options);
                string status = await WaitStatusAsync(session, since, s => s.Contains("pela GPU") || s.Contains("indisponível"), 40);
                if (!status.Contains("pela GPU"))
                {
                    // Computador sem placa de vídeo que codifique: o modo compatível continua, e isso não é defeito do Ponte.
                    check($"{name} pela GPU", false, status.Length > 0 ? status : "o vídeo não começou", skipped: status.Contains("indisponível"));
                    continue;
                }
                // Só sai quadro quando a tela muda: para medir os quadros por segundo, a janela de teste fica animada.
                pattern.Animate(true);
                await Task.Delay(1500);
                var (fps, kbps) = await MeasureRateAsync(session, 3);
                var moving = await MeasureLatencyAsync(pattern, probe, primary);
                pattern.Animate(false);
                await Task.Delay(500);
                check($"{name} pela GPU", fps >= Math.Min(options.Fps, 60) * 0.4,
                    $"{status}; {fps:N0} FPS recebidos com a tela mexendo, {kbps / 1000.0:N1} Mbit/s");
                if (pattern.CoveredBy() is { } cover)
                {
                    // Outro programa sempre por cima (o app do Claude pode ficar assim) tapa parte da janela de teste:
                    // o que a imagem mostra ali não diz nada sobre o vídeo.
                    check($"{name}: cores e atraso", false, $"a janela de teste está coberta por {cover}", skipped: true);
                    continue;
                }
                CheckPicture(session, pattern, primary, name, check, chroma444: false, dir);
                var latency = await MeasureLatencyAsync(pattern, probe, primary);
                // As medidas só valem se o vídeo continuou o tempo todo (e não caiu para o modo compatível no meio).
                bool still = session.VideoStatus == status;
                check($"{name}: atraso entre a tela mudar e a imagem mudar aqui", still && latency.Count >= 5 && Median(latency) < 250,
                    still ? $"tela parada: {LatencyText(latency)}; tela mexendo: {LatencyText(moving)}" : "o vídeo parou no meio: " + session.VideoStatus);
            }

            // 4:4:4: com placa que codifica, vai pela GPU; sem ela, PNG sem perdas. Nos dois casos, colunas de 1 pixel
            // vermelhas e azuis têm de chegar separadas (no 4:2:0 viram roxo).
            if (ended is not null)
            {
                check("a sessão continua aberta durante as trocas de vídeo", false, ended);
                return;
            }
            pattern.BringToTop();
            int fullSince = session.VideoStatusVersion;
            await session.SendSettingsAsync(screen, 60, false, new VideoOptions(60, "h264", Chroma444: true));
            string full = await WaitStatusAsync(session, fullSince, s => s.Contains("4:4:4") && !s.StartsWith("Procurando") && !s.StartsWith("Iniciando"), 40);
            await Task.Delay(2000);
            check("Chroma 4:4:4", full.Contains("4:4:4"), full);
            if (pattern.CoveredBy() is { } covered)
                check("4:4:4: colunas de 1 pixel em cores diferentes chegam separadas", false, $"a janela de teste está coberta por {covered}", skipped: true);
            else
                CheckPicture(session, pattern, primary, "4:4:4", check, chroma444: true, dir);

            // De volta ao modo de sempre, que continua sendo o padrão.
            pattern.BringToTop();
            int backSince = session.VideoStatusVersion;
            await session.SendSettingsAsync(screen, 60, false, new VideoOptions());
            string back = await WaitStatusAsync(session, backSince, s => s.StartsWith("JPEG"), 20);
            long before = Interlocked.Read(ref session.Frames);
            await pattern.SetFlashAsync(Color.Orange);
            bool flowing = await WaitAsync(() => Interlocked.Read(ref session.Frames) > before, 50);
            string jpegLatency = pattern.CoveredBy() is { } hidden ? $"não medido (janela de teste coberta por {hidden})"
                : LatencyText(await MeasureLatencyAsync(pattern, probe, primary));
            check("volta ao JPEG de sempre", back.StartsWith("JPEG") && flowing, $"{back}; atraso no JPEG: {jpegLatency}");
        }
        finally
        {
            session.FrameUpdated -= probe.OnFrame;
        }
    }

    private static async Task<long?> RemoteSizeAsync(AppConfig config, string command, string argument)
    {
        try
        {
            var (host, port) = Transport.ParseEndpoint(config.RelayServer, Transport.DefaultRelayPort);
            using var tcp = await Transport.ConnectTcpAsync(host, port, CancellationToken.None);
            var raw = tcp.GetStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Transport.WriteLineAsync(raw, Transport.RelayLine(config, command, argument), timeout.Token);
            string reply = await Transport.ReadLineAsync(raw, timeout.Token);
            return reply.StartsWith("OK ") && long.TryParse(reply.AsSpan(3), out long size) ? size : null;
        }
        catch (Exception ex) when (ex is PonteException or IOException or OperationCanceledException or System.Net.Sockets.SocketException) { return null; }
    }

    // Espera um status posterior a "since" que satisfaça "done"; devolve o último que chegou.
    private static async Task<string> WaitStatusAsync(ViewerSession session, int since, Func<string, bool> done, int seconds)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            if (session.VideoStatusVersion > since && done(session.VideoStatus)) return session.VideoStatus;
            await Task.Delay(100);
        }
        return session.VideoStatus;
    }

    private static async Task<(double Fps, double Kbps)> MeasureRateAsync(ViewerSession session, int seconds)
    {
        long frames = Interlocked.Read(ref session.Frames), bytes = session.BytesReceived;
        var watch = Stopwatch.StartNew();
        await Task.Delay(seconds * 1000);
        double elapsed = watch.Elapsed.TotalSeconds;
        return ((Interlocked.Read(ref session.Frames) - frames) / elapsed, (session.BytesReceived - bytes) * 8 / 1000.0 / elapsed);
    }

    // Cores dos blocos (tolerância da compressão) e colunas de 1 pixel alternando vermelho e azul.
    private static void CheckPicture(ViewerSession session, VideoPattern pattern, Rectangle primary, string name, Checker check, bool chroma444, string dir)
    {
        int worst = 0, separated = 0, columns = 0;
        string worstDetail = "";
        lock (session.CanvasLock)
        {
            if (session.Canvas is not { } canvas)
            {
                check($"{name}: cores chegam certas", false, "sem imagem");
                return;
            }
            canvas.Save(Path.Combine(dir, $"video-{name.Split(' ')[0].Replace(":", "")}.png"), ImageFormat.Png);
            for (int i = 0; i < VideoPattern.Blocks.Length; i++)
            {
                var at = pattern.BlockCenter(i);
                var got = canvas.GetPixel(at.X - primary.X, at.Y - primary.Y);
                var want = VideoPattern.Blocks[i];
                int diff = Math.Max(Math.Abs(got.R - want.R), Math.Max(Math.Abs(got.G - want.G), Math.Abs(got.B - want.B)));
                if (diff > worst) { worst = diff; worstDetail = $"esperava {want.R},{want.G},{want.B}, veio {got.R},{got.G},{got.B}"; }
            }
            var row = pattern.StripesRow;
            for (int x = row.Left + 4; x < row.Right - 4; x++, columns++)
            {
                var got = canvas.GetPixel(x - primary.X, row.Y - primary.Y);
                bool red = (x - row.Left) % 2 == 0;
                if (red ? got.R - got.B >= 80 : got.B - got.R >= 80) separated++;
            }
        }
        if (!chroma444) check($"{name}: cores chegam certas", worst <= 24, worst == 0 ? "iguais" : $"maior diferença {worst} de 255 ({worstDetail})");
        else check("4:4:4: colunas de 1 pixel em cores diferentes chegam separadas", separated >= columns * 9 / 10,
            $"{separated} de {columns} colunas; cores dos blocos com diferença máxima de {worst}");
    }

    private static async Task<List<double>> MeasureLatencyAsync(VideoPattern pattern, FrameProbe probe, Rectangle primary)
    {
        var results = new List<double>();
        var at = pattern.FlashCenter;
        bool white = false;
        for (int i = 0; i < 12; i++)
        {
            white = !white;
            var color = white ? Color.White : Color.Black;
            var seen = probe.Expect(new Point(at.X - primary.X, at.Y - primary.Y), color);
            long shown = await pattern.SetFlashAsync(color);
            if (await Task.WhenAny(seen, Task.Delay(2000)) == seen && i > 0)
                results.Add(Math.Max(0, Stopwatch.GetElapsedTime(shown, seen.Result).TotalMilliseconds));
            await Task.Delay(120 + Random.Shared.Next(120));
        }
        return results;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return double.PositiveInfinity;
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static string LatencyText(List<double> values) =>
        values.Count == 0 ? "a mudança não chegou" : $"mediana {Median(values):N0} ms, máxima {values.Max():N0} ms em {values.Count} medidas";

    /// <summary>
    /// Olha um pixel da imagem recebida a cada quadro e avisa quando a mudança fica visível: a cor passou da
    /// metade do caminho até a esperada. (O vídeo comprimido mostra a mudança logo e refina nos quadros seguintes.)
    /// </summary>
    private sealed class FrameProbe(ViewerSession session)
    {
        private TaskCompletionSource<long>? _waiting;
        private Point _point;
        private Color _color;

        public Task<long> Expect(Point point, Color color)
        {
            var waiting = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (this) { _point = point; _color = color; _waiting = waiting; }
            return waiting.Task;
        }

        public void OnFrame()
        {
            long now = Stopwatch.GetTimestamp();
            TaskCompletionSource<long>? waiting;
            Point point;
            Color color;
            lock (this) { waiting = _waiting; point = _point; color = _color; }
            if (waiting is null) return;
            Color got;
            lock (session.CanvasLock)
            {
                if (session.Canvas is not { } canvas || point.X >= canvas.Width || point.Y >= canvas.Height) return;
                got = canvas.GetPixel(point.X, point.Y);
            }
            if (Math.Abs(got.R - color.R) + Math.Abs(got.G - color.G) + Math.Abs(got.B - color.B) < 3 * 128)
            {
                lock (this) { if (_waiting == waiting) _waiting = null; }
                waiting.TrySetResult(now);
            }
        }
    }

    /// <summary>Janela de teste por cima de tudo: blocos de cor, colunas de 1 pixel e um quadrado que pisca.</summary>
    private sealed class VideoPattern : IDisposable
    {
        public static readonly Color[] Blocks =
        {
            Color.FromArgb(255, 0, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 0, 255), Color.FromArgb(255, 255, 255), Color.FromArgb(0, 0, 0),
            Color.FromArgb(128, 128, 128), Color.FromArgb(255, 255, 0), Color.FromArgb(255, 0, 255), Color.FromArgb(0, 255, 255), Color.FromArgb(200, 120, 40),
        };
        private static readonly Rectangle Stripes = new(10, 80, 280, 200), Flash = new(320, 80, 260, 200), Motion = new(10, 286, 570, 10);
        private readonly Form _form;
        private readonly Thread _thread;
        private Color _flash = Color.Black;
        private volatile bool _animating;
        private int _tick;

        private VideoPattern(Form form, Thread thread) { _form = form; _thread = thread; }

        public static async Task<VideoPattern> ShowAsync(Rectangle primary)
        {
            var shown = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
            VideoPattern? pattern = null;
            var thread = new Thread(() =>
            {
                using var form = new PatternForm
                {
                    Text = "Ponte - teste de vídeo", FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                    TopMost = true, ShowInTaskbar = false, BackColor = Color.FromArgb(40, 40, 40),
                    Bounds = new Rectangle(primary.X + 100, primary.Y + 100, 600, 300),
                };
                form.Paint += (_, e) => pattern?.Draw(e.Graphics);
                form.Shown += (_, _) => shown.TrySetResult(form);
                Application.Run(form);
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            var window = await shown.Task;
            pattern = new VideoPattern(window, thread);
            window.Invoke(window.Refresh);
            await Task.Delay(500);
            return pattern;
        }

        private Point ToScreen(int x, int y) => _form.Invoke(() => _form.PointToScreen(new Point(x, y)));

        public Point BlockCenter(int i) => ToScreen(10 + i * 58 + 25, 10 + 25);
        public Point FlashCenter => ToScreen(Flash.X + Flash.Width / 2, Flash.Y + Flash.Height / 2);
        /// <summary>Linha do meio das colunas, em coordenadas de tela.</summary>
        public Rectangle StripesRow
        {
            get
            {
                var origin = ToScreen(Stripes.X, Stripes.Y + Stripes.Height / 2);
                return new Rectangle(origin.X, origin.Y, Stripes.Width, 1);
            }
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>Põe a janela de teste por cima também das outras que ficam sempre na frente, sem ativá-la.</summary>
        public void BringToTop() => _form.Invoke(() =>
            SetWindowPos(_form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040));

        /// <summary>O programa cuja janela tapa algum ponto que o teste olha, ou null se a janela de teste está à vista.</summary>
        public string? CoveredBy()
        {
            var points = Enumerable.Range(0, Blocks.Length).Select(BlockCenter).Append(FlashCenter).Append(StripesRow.Location).ToList();
            IntPtr own = _form.Invoke(() => _form.Handle);
            foreach (var point in points)
            {
                IntPtr top = GetAncestor(WindowFromPoint(point), 2);
                if (top == own || top == IntPtr.Zero) continue;
                GetWindowThreadProcessId(top, out uint pid);
                try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; }
                catch (ArgumentException) { return "outra janela"; }
            }
            return null;
        }

        /// <summary>Liga ou desliga uma faixa que se move sem parar, para a tela mudar a cada quadro.</summary>
        public void Animate(bool on)
        {
            if (on == _animating) return;
            _animating = on;
            if (!on) return;
            new Thread(() =>
            {
                while (_animating)
                {
                    try { _form.BeginInvoke(() => { _tick++; _form.Invalidate(Motion); }); }
                    catch (InvalidOperationException) { return; }
                    Thread.Sleep(4);
                }
            }) { IsBackground = true }.Start();
        }

        /// <summary>Troca a cor do quadrado e devolve o instante em que a janela terminou de se pintar.</summary>
        public Task<long> SetFlashAsync(Color color) => Task.Run(() => _form.Invoke(() =>
        {
            _flash = color;
            _form.Invalidate(Flash);
            _form.Update();
            return Stopwatch.GetTimestamp();
        }));

        private void Draw(Graphics g)
        {
            for (int i = 0; i < Blocks.Length; i++)
                using (var brush = new SolidBrush(Blocks[i])) g.FillRectangle(brush, 10 + i * 58, 10, 50, 50);
            for (int x = 0; x < Stripes.Width; x++)
                using (var brush = new SolidBrush(x % 2 == 0 ? Color.FromArgb(255, 0, 0) : Color.FromArgb(0, 0, 255)))
                    g.FillRectangle(brush, Stripes.X + x, Stripes.Y, 1, Stripes.Height);
            using (var flash = new SolidBrush(_flash)) g.FillRectangle(flash, Flash);
            g.FillRectangle(Brushes.DimGray, Motion);
            g.FillRectangle(Brushes.Gold, Motion.X + _tick * 7 % (Motion.Width - 40), Motion.Y, 40, Motion.Height);
        }

        public void Dispose()
        {
            _animating = false;
            try { _form.BeginInvoke(_form.Close); } catch (InvalidOperationException) { }
            _thread.Join(2000);
        }

        private sealed class PatternForm : Form
        {
            public PatternForm() => DoubleBuffered = true;
        }
    }
}
