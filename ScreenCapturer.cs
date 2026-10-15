using System.Drawing.Imaging;

namespace Ponte;

/// <summary>
/// Captura a tela e devolve só o que mudou desde a captura anterior: a imagem é comparada em
/// blocos de 64 px, os blocos alterados viram retângulos e cada retângulo vai como JPEG.
/// Formato do quadro: largura, altura e quantidade (ushort), depois x, y, w, h (ushort), tamanho (int) e o JPEG.
/// </summary>
internal sealed class ScreenCapturer : IDisposable
{
    private const int Tile = 64;
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private Bitmap? _current, _previous;

    /// <summary>Quadro pronto para enviar, ou null se nada mudou.</summary>
    public unsafe byte[]? Capture(Rectangle bounds, int quality, bool forceFull, bool chroma444 = false)
    {
        int w = bounds.Width, h = bounds.Height;
        if (_current is null || _current.Width != w || _current.Height != h)
        {
            Dispose();
            _current = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            _previous = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            forceFull = true;
        }

        using (var g = Graphics.FromImage(_current))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

        var full = new Rectangle(0, 0, w, h);
        var rects = new List<Rectangle>();
        if (forceFull)
        {
            rects.Add(full);
        }
        else
        {
            int cols = (w + Tile - 1) / Tile, rows = (h + Tile - 1) / Tile, count = 0;
            var changed = new bool[rows, cols];
            var a = _current.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            var b = _previous!.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int ty = 0; ty < rows; ty++)
                {
                    int y0 = ty * Tile, y1 = Math.Min(h, y0 + Tile);
                    for (int tx = 0; tx < cols; tx++)
                    {
                        int x0 = tx * Tile, length = (Math.Min(w, x0 + Tile) - x0) * 4;
                        for (int y = y0; y < y1; y++)
                        {
                            var rowA = new ReadOnlySpan<byte>((byte*)a.Scan0 + (long)y * a.Stride + x0 * 4, length);
                            var rowB = new ReadOnlySpan<byte>((byte*)b.Scan0 + (long)y * b.Stride + x0 * 4, length);
                            if (rowA.SequenceEqual(rowB)) continue;
                            changed[ty, tx] = true;
                            count++;
                            break;
                        }
                    }
                }
            }
            finally
            {
                _current.UnlockBits(a);
                _previous.UnlockBits(b);
            }

            if (count == 0) return null;
            // Com a maior parte da tela alterada, um JPEG só sai menor e mais rápido que dezenas de pedaços.
            if (count > rows * cols * 6 / 10) rects.Add(full);
            else MergeTiles(changed, rows, cols, w, h, rects);
        }

        byte[] payload = Encode(_current, rects, full, quality, chroma444);
        (_previous, _current) = (_current, _previous);
        return payload;
    }

    // Junta blocos vizinhos: primeiro em faixas horizontais, depois estende a faixa para baixo enquanto ela se repete.
    private static void MergeTiles(bool[,] changed, int rows, int cols, int w, int h, List<Rectangle> rects)
    {
        var used = new bool[rows, cols];
        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                if (!changed[ty, tx] || used[ty, tx]) continue;
                int x1 = tx;
                while (x1 + 1 < cols && changed[ty, x1 + 1] && !used[ty, x1 + 1]) x1++;
                int y1 = ty;
                while (y1 + 1 < rows)
                {
                    bool whole = true;
                    for (int x = tx; x <= x1 && whole; x++) whole = changed[y1 + 1, x] && !used[y1 + 1, x];
                    if (!whole) break;
                    y1++;
                }
                for (int y = ty; y <= y1; y++)
                    for (int x = tx; x <= x1; x++)
                        used[y, x] = true;
                int left = tx * Tile, top = ty * Tile;
                rects.Add(new Rectangle(left, top, Math.Min(w, (x1 + 1) * Tile) - left, Math.Min(h, (y1 + 1) * Tile) - top));
            }
        }
    }

    internal static byte[] Encode(Bitmap image, List<Rectangle> rects, Rectangle full, int quality, bool chroma444 = false)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)full.Width);
        writer.Write((ushort)full.Height);
        writer.Write((ushort)rects.Count);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 10, 95));
        using var jpeg = new MemoryStream();
        foreach (var r in rects)
        {
            jpeg.SetLength(0);
            if (r == full)
            {
                if (chroma444) image.Save(jpeg, ImageFormat.Png);
                else image.Save(jpeg, JpegCodec, parameters);
            }
            else
            {
                using var part = image.Clone(r, image.PixelFormat);
                if (chroma444) part.Save(jpeg, ImageFormat.Png);
                else part.Save(jpeg, JpegCodec, parameters);
            }
            writer.Write((ushort)r.X);
            writer.Write((ushort)r.Y);
            writer.Write((ushort)r.Width);
            writer.Write((ushort)r.Height);
            writer.Write((int)jpeg.Length);
            writer.Write(jpeg.GetBuffer(), 0, (int)jpeg.Length);
        }
        writer.Flush();
        return output.ToArray();
    }

    public void Dispose()
    {
        _current?.Dispose();
        _previous?.Dispose();
        _current = _previous = null;
    }
}

/// <summary>Reproduz no computador controlado o mouse e o teclado que chegam do visualizador.</summary>
internal static class InputInjector
{
    private static readonly HashSet<ushort> KeysDown = new();
    private static readonly int InputSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>();

    /// <summary>
    /// Verdadeiro quando o Windows recusou a última entrada. Acontece quando a janela ativa pertence a um
    /// programa aberto como administrador e o Ponte não: o sistema não deixa um programa comum controlá-la.
    /// </summary>
    public static bool LastBlocked { get; private set; }

    private static void Send(Native.INPUT input) => LastBlocked = Native.SendInput(1, new[] { input }, InputSize) == 0;

    /// <param name="nx">Posição horizontal de 0 a 65535 dentro da tela escolhida.</param>
    /// <param name="action">0 mover; 1/2 esquerdo; 3/4 direito; 5/6 meio (aperta/solta); 7 roda; 8 roda horizontal.</param>
    public static void Mouse(Rectangle screen, int nx, int ny, int action, int wheel)
    {
        // Arredondando, o ponto volta a ser exatamente o pixel de onde o visualizador partiu.
        int px = screen.X + (int)Math.Round((double)nx * (screen.Width - 1) / 65535);
        int py = screen.Y + (int)Math.Round((double)ny * (screen.Height - 1) / 65535);
        int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);

        uint flags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
        uint data = 0;
        switch (action)
        {
            case 1: flags |= Native.MOUSEEVENTF_LEFTDOWN; break;
            case 2: flags |= Native.MOUSEEVENTF_LEFTUP; break;
            case 3: flags |= Native.MOUSEEVENTF_RIGHTDOWN; break;
            case 4: flags |= Native.MOUSEEVENTF_RIGHTUP; break;
            case 5: flags |= Native.MOUSEEVENTF_MIDDLEDOWN; break;
            case 6: flags |= Native.MOUSEEVENTF_MIDDLEUP; break;
            case 7: flags |= Native.MOUSEEVENTF_WHEEL; data = unchecked((uint)wheel); break;
            case 8: flags |= Native.MOUSEEVENTF_HWHEEL; data = unchecked((uint)wheel); break;
        }

        // O Windows quer a posição numa escala de 0 a 65535 e a converte de volta truncando. Mirar no meio
        // do pixel garante que a conta dele caia no pixel pedido, e não no vizinho.
        var input = new Native.INPUT { type = Native.INPUT_MOUSE };
        input.U.mi = new Native.MOUSEINPUT
        {
            dx = (int)(((long)(px - vx) * 65536 + 32768) / Math.Max(1, vw)),
            dy = (int)(((long)(py - vy) * 65536 + 32768) / Math.Max(1, vh)),
            mouseData = data, dwFlags = flags,
        };
        Send(input);
    }

    public static void Key(ushort virtualKey, bool up, bool extended)
    {
        if (virtualKey is 0 or > 0xFE) return;
        lock (KeysDown)
        {
            if (up) KeysDown.Remove(virtualKey);
            else KeysDown.Add(virtualKey);
        }
        var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        input.U.ki = new Native.KEYBDINPUT
        {
            wVk = virtualKey,
            wScan = (ushort)Native.MapVirtualKeyW(virtualKey, 0),
            dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (extended ? Native.KEYEVENTF_EXTENDEDKEY : 0),
        };
        Send(input);
    }

    /// <summary>
    /// Digita um texto qualquer, sem depender do layout de teclado daqui: cada caractere vai como entrada
    /// Unicode. É o que o aplicativo do celular usa, porque o teclado dele entrega texto, e não teclas.
    /// </summary>
    public static void Text(string text)
    {
        var inputs = new List<Native.INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\r') continue;
            // Enter e Tab vão como teclas de verdade: muitos programas ignoram os dois quando chegam como caractere.
            ushort vk = c switch { '\n' => 0x0D, '\t' => 0x09, _ => 0 };
            foreach (uint up in new[] { 0u, Native.KEYEVENTF_KEYUP })
            {
                var input = new Native.INPUT { type = Native.INPUT_KEYBOARD };
                input.U.ki = vk != 0
                    ? new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKeyW(vk, 0), dwFlags = up }
                    : new Native.KEYBDINPUT { wScan = c, dwFlags = Native.KEYEVENTF_UNICODE | up };
                inputs.Add(input);
            }
        }
        if (inputs.Count > 0) LastBlocked = Native.SendInput((uint)inputs.Count, inputs.ToArray(), InputSize) == 0;
    }

    /// <summary>Solta o que ficou pressionado, para a sessão não terminar com um Ctrl ou Shift "grudado".</summary>
    public static void ReleaseAll()
    {
        ushort[] pending;
        lock (KeysDown) pending = KeysDown.ToArray();
        foreach (ushort vk in pending) Key(vk, true, false);
    }
}
