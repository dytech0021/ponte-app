using System.Drawing.Imaging;

namespace Ponte;

// 1.17.5: o quadro em faixas. Codifica uma imagem conhecida com o código de quem é acessado e a decodifica com o de quem
// acessa, sem rede no meio: a imagem tem de voltar no lugar, e sem perdas tem de voltar idêntica.
internal static partial class SelfTest
{
    private static void CheckFrameBands(Checker check)
    {
        // Lados que não são múltiplos de 16, de propósito: a última faixa e a última coluna ficam "quebradas".
        const int w = 1931, h = 1087;
        var full = new Rectangle(0, 0, w, h);
        using var image = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        Paint(image);

        // Sem perdas (o modo 4:4:4), tela inteira: cortada em faixas, tem de voltar ponto a ponto igual.
        byte[] frame = ScreenCapturer.Encode(image, [full], full, 60, chroma444: true);
        var (fw, fh, parts) = FrameDecoder.Parse(frame);
        using var exact = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var changed = FrameDecoder.Draw(exact, frame, parts);
        var (max, _) = Difference(image, exact, full);
        bool split = Environment.ProcessorCount < 2 || parts.Length > 1;
        check("quadro inteiro vai em faixas e volta idêntico (sem perdas)",
            fw == w && fh == h && split && changed == full && max == 0 && parts.All(p => p.Y % 16 == 0),
            $"{parts.Length} faixas, área {changed}, maior diferença {max}");

        // JPEG, dois retângulos soltos: cada um volta no lugar dele (com a perda normal do JPEG) e o resto fica intocado.
        var rects = new List<Rectangle> { new(64, 128, 640, 512), new(1280, 704, 651, 383) };
        frame = ScreenCapturer.Encode(image, rects, full, 85);
        (_, _, parts) = FrameDecoder.Parse(frame);
        using var partial = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        changed = FrameDecoder.Draw(partial, frame, parts);
        double worst = rects.Max(r => Difference(image, partial, r).Mean);
        using var black = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        // Fora dos dois retângulos, a imagem de destino continua como nasceu (preta).
        long outside = 0;
        foreach (var strip in new[] { new Rectangle(0, 0, w, 128), new Rectangle(0, 640, 1280, h - 640), new Rectangle(704, 128, w - 704, 576) })
            outside = Math.Max(outside, Difference(black, partial, strip).Max);
        check("retângulos soltos voltam no lugar e não tocam no resto",
            changed == Rectangle.Union(rects[0], rects[1]) && worst < 4 && outside == 0,
            $"{parts.Length} partes, diferença média {worst:N2}, fora dos retângulos {outside}");

        // Quadro estragado não derruba o Ponte: é recusado como dado inválido.
        byte[] broken = (byte[])frame.Clone();
        Array.Fill(broken, (byte)0x5A, 6 + 12, 256);
        bool refused = false;
        try
        {
            using var target = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            FrameDecoder.Draw(target, broken, FrameDecoder.Parse(broken).Parts);
        }
        catch (InvalidDataException) { refused = true; }
        check("quadro com imagem estragada é recusado como dado inválido", refused);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    /// <summary>
    /// O que a área está mostrando agora, copiado da superfície dela no Windows (e não repintado na hora, que é o que
    /// o PrintWindow faz); null se não deu.
    /// </summary>
    private static Bitmap? Shown(Control view, Size size)
    {
        const uint SourceCopy = 0x00CC0020;
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(image);
        IntPtr target = g.GetHdc(), source = GetDC(view.Handle);
        bool ok;
        try { ok = source != IntPtr.Zero && BitBlt(target, 0, 0, size.Width, size.Height, source, 0, 0, SourceCopy); }
        finally
        {
            if (source != IntPtr.Zero) ReleaseDC(view.Handle, source);
            g.ReleaseHdc(target);
        }
        if (ok) return image;
        image.Dispose();
        return null;
    }

    /// <summary>
    /// A área da sessão repinta só o pedaço que mudou (1.17.5). Abre uma área de verdade nesta sessão, faz a tela mudar
    /// aos poucos (um quadradinho andando) e compara o que ficou NA TELA com a imagem inteira desenhada de uma vez: se
    /// algum pedaço deixasse de ser repintado, as duas não bateriam. Roda numa linha própria, com as janelas dela.
    /// </summary>
    private static void CheckPartialRepaint(ViewerSession session, Checker check)
    {
        bool? same = null;
        string detail = "a janela de teste não respondeu";
        var ui = new Thread(() =>
        {
            try
            {
                var area = Screen.PrimaryScreen!.WorkingArea;
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
                    Bounds = new Rectangle(area.Right - 660, area.Top + 20, 640, 360),
                };
                using var view = new RemoteView(session) { Dock = DockStyle.Fill };
                form.Controls.Add(view);
                using var marker = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, TopMost = true,
                    BackColor = Color.OrangeRed, Bounds = new Rectangle(area.Left + 40, area.Top + 300, 48, 48),
                };
                int pieces = 0, whole = 0;
                void Count(Rectangle region, Size _) { if (region.IsEmpty) Interlocked.Increment(ref whole); else Interlocked.Increment(ref pieces); }
                session.RegionUpdated += view.RequestRepaint;
                session.RegionUpdated += Count;
                try
                {
                    form.Show();
                    marker.Show();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (int step = 0; clock.ElapsedMilliseconds < 1800; step++)
                    {
                        marker.Location = new Point(area.Left + 40 + step * 37 % 500, area.Top + 300 + step * 23 % 300);
                        Application.DoEvents();
                        Thread.Sleep(15);
                    }
                    marker.Hide();
                    // Com a imagem presa (nenhum quadro novo entra), o que estava para ser repintado é repintado; aí a
                    // tela e a imagem têm de dizer a mesma coisa.
                    lock (session.CanvasLock)
                    {
                        for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(20); }
                        view.Update();
                        var size = view.ClientSize;
                        if (session.Canvas is null) { detail = "a sessão ainda não tinha imagem"; return; }
                        // O que a janela está mostrando de verdade vem do Windows (a superfície dela, mesmo com outra
                        // janela por cima). Para ter certeza de que é isso, e não uma repintura feita na hora, um
                        // quadradinho desenhado por fora da pintura da área tem de aparecer na leitura.
                        using (var direct = view.CreateGraphics()) direct.FillRectangle(Brushes.Magenta, 0, 0, 12, 12);
                        using (var canary = Shown(view, size))
                        {
                            var spot = canary?.GetPixel(5, 5) ?? Color.Black;
                            if (spot.R < 240 || spot.G > 15 || spot.B < 240) { detail = "o Windows não deixou ler o que a janela de teste mostra"; return; }
                        }
                        view.Invalidate(new Rectangle(0, 0, 12, 12));
                        view.Update();
                        using var seen = Shown(view, size);
                        if (seen is null) { detail = "o Windows não deixou ler o que a janela de teste mostra"; return; }
                        using var expected = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                        view.DrawToBitmap(expected, new Rectangle(Point.Empty, size));
                        var (max, mean) = Difference(expected, seen, new Rectangle(Point.Empty, size));
                        detail = $"{pieces} repinturas de um pedaço e {whole} inteiras; diferença entre o que a janela mostra e a imagem: máxima {max}, média {mean:N3}";
                        // Sem nenhuma repintura de pedaço não houve o que conferir (a tela não mudou aos poucos).
                        if (pieces > 0) same = max <= 2;
                    }
                }
                finally
                {
                    session.RegionUpdated -= view.RequestRepaint;
                    session.RegionUpdated -= Count;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                detail = ex.Message;
            }
        }) { IsBackground = true, Name = "ponte-teste-repintura" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join(20000);
        check("janela repinta só o pedaço que mudou e fica igual à imagem inteira", same == true, detail, skipped: same is null);
    }

    // Degradês nos dois sentidos e um xadrez de 64 pontos: comprime bem e qualquer troca de lugar aparece.
    private static unsafe void Paint(Bitmap image)
    {
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < image.Height; y++)
            {
                uint* row = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                for (int x = 0; x < image.Width; x++)
                {
                    uint red = (uint)(x * 255 / (image.Width - 1)), green = (uint)(y * 255 / (image.Height - 1));
                    uint blue = (x / 64 + y / 64) % 2 == 0 ? 40u : 200u;
                    row[x] = 0xFF000000u | red << 16 | green << 8 | blue;
                }
            }
        }
        finally { image.UnlockBits(data); }
    }

    /// <summary>A maior diferença e a diferença média, por cor, entre as duas imagens dentro de <paramref name="area"/>.</summary>
    private static unsafe (long Max, double Mean) Difference(Bitmap a, Bitmap b, Rectangle area)
    {
        var whole = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var db = b.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            long max = 0, sum = 0;
            for (int y = area.Top; y < area.Bottom; y++)
            {
                byte* ra = (byte*)da.Scan0 + (long)y * da.Stride + area.Left * 4, rb = (byte*)db.Scan0 + (long)y * db.Stride + area.Left * 4;
                for (int i = 0; i < area.Width * 4; i++)
                {
                    if (i % 4 == 3) continue;
                    int d = Math.Abs(ra[i] - rb[i]);
                    sum += d;
                    if (d > max) max = d;
                }
            }
            return (max, sum / (double)Math.Max(1, (long)area.Width * area.Height * 3));
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }
}
