using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Ponte;

/// <summary>
/// Acompanha o ponteiro do mouse do computador controlado. A captura de tela não traz o ponteiro: ele
/// vai à parte, só quando muda de forma, e o visualizador o usa no lugar do ponteiro local.
/// Formato: 1 byte (1 = visível), o ponto ativo x e y (ushort) e a imagem em PNG, com transparência.
/// </summary>
internal sealed class PointerWatcher
{
    public const int MaxSize = 256;
    private static readonly byte[] Hidden = new byte[5];
    private IntPtr _last = -1;
    private (bool Inside, int X, int Y) _lastPosition = (false, -1, -1);

    /// <summary>
    /// Onde o ponteiro está, para o aplicativo do celular desenhá-lo no lugar certo mesmo sem ter tocado
    /// na tela: 1 byte (1 = dentro da tela mostrada), x e y (ushort) de 0 a 65535, na mesma escala da
    /// mensagem de mouse. Null se nada mudou desde a última chamada.
    /// </summary>
    public byte[]? PollPosition(Rectangle screen)
    {
        if (!Native.GetCursorPos(out var p)) return null;
        bool inside = screen.Contains(p.X, p.Y);
        int x = inside ? (int)Math.Round((double)(p.X - screen.X) * 65535 / Math.Max(1, screen.Width - 1)) : 0;
        int y = inside ? (int)Math.Round((double)(p.Y - screen.Y) * 65535 / Math.Max(1, screen.Height - 1)) : 0;
        if ((inside, x, y) == _lastPosition) return null;
        _lastPosition = (inside, x, y);
        return [(byte)(inside ? 1 : 0), (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8)];
    }

    /// <summary>A forma atual, pronta para enviar, ou null se é a mesma da última chamada.</summary>
    public byte[]? Poll(bool force = false)
    {
        var info = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref info)) return null;
        IntPtr handle = (info.flags & Native.CURSOR_SHOWING) != 0 ? info.hCursor : IntPtr.Zero;
        if (handle == _last && !force) return null;
        _last = handle;
        return handle == IntPtr.Zero ? Hidden : Encode(handle) ?? Hidden;
    }

    // Desenha o ponteiro sobre um fundo todo preto (0) ou todo branco (255) e devolve os pontos, em BGRA.
    // O fundo precisa ser uma imagem do próprio Windows: é sobre ela que ele faz a transparência e a inversão.
    private static unsafe uint[]? Draw(IntPtr cursor, int w, int h, byte background)
    {
        var header = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
        IntPtr surface = Native.CreateDIBSection(dc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (surface == IntPtr.Zero)
        {
            Native.DeleteDC(dc);
            return null;
        }
        IntPtr previous = Native.SelectObject(dc, surface);
        try
        {
            var pixels = new Span<uint>((void*)bits, w * h);
            MemoryMarshal.AsBytes(pixels).Fill(background);
            if (!Native.DrawIconEx(dc, 0, 0, cursor, w, h, 0, IntPtr.Zero, Native.DI_NORMAL)) return null;
            Native.GdiFlush();
            return pixels.ToArray();
        }
        finally
        {
            Native.SelectObject(dc, previous);
            Native.DeleteObject(surface);
            Native.DeleteDC(dc);
        }
    }

    /// <summary>
    /// O ponteiro no formato de envio, ou null se não deu para lê-lo ou se ele é todo transparente.
    /// O Windows não entrega a imagem com transparência pronta: ela é desenhada duas vezes, sobre preto e
    /// sobre branco, e a diferença entre os dois resultados diz quanto cada ponto é transparente.
    /// </summary>
    public static unsafe byte[]? Encode(IntPtr cursor)
    {
        if (!Native.GetIconInfo(cursor, out var icon)) return null;
        try
        {
            // Ponteiro monocromático não tem imagem de cor: a máscara traz as duas metades, uma sobre a outra.
            bool color = icon.hbmColor != IntPtr.Zero;
            if (Native.GetObjectW(color ? icon.hbmColor : icon.hbmMask, Marshal.SizeOf<Native.BITMAP>(), out var size) == 0) return null;
            int w = size.bmWidth, h = color ? size.bmHeight : size.bmHeight / 2;
            if (w is < 1 or > MaxSize || h is < 1 or > MaxSize) return null;

            if (Draw(cursor, w, h, 0) is not { } onBlack || Draw(cursor, w, h, 255) is not { } onWhite) return null;
            var result = new uint[w * h];
            var inverts = new bool[w * h];
            bool visible = false;
            for (int i = 0; i < result.Length; i++)
            {
                uint dark = onBlack[i], light = onWhite[i];
                int difference = 0;
                for (int shift = 0; shift < 24; shift += 8) difference += (int)(light >> shift & 255) - (int)(dark >> shift & 255);
                difference /= 3;
                if (difference < -32)
                {
                    // Mais claro sobre o preto que sobre o branco: o ponto inverte a cor que está embaixo,
                    // como na barra de texto. Sem ter o que inverter, ele vira preto.
                    inverts[i] = visible = true;
                    result[i] = 0xFF000000;
                    continue;
                }
                // Sobre o preto sai cor x opacidade; sobre o branco, o mesmo mais o branco que sobra.
                uint alpha = (uint)Math.Clamp(255 - difference, 0, 255);
                if (alpha == 0) continue;
                visible = true;
                uint pixel = alpha << 24;
                for (int shift = 0; shift < 24; shift += 8) pixel |= Math.Min(255, (dark >> shift & 255) * 255 / alpha) << shift;
                result[i] = pixel;
            }
            // Um ponteiro todo transparente é o jeito de um programa escondê-lo.
            if (!visible) return null;
            // Contorno branco em volta do que invertia, para continuar visível sobre fundo escuro.
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] >> 24 == 0 && NearInverted(inverts, w, h, i % w, i / w)) result[i] = 0xFFFFFFFF;
            }

            using var image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var locked = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) result.AsSpan(y * w, w).CopyTo(new Span<uint>((byte*)locked.Scan0 + y * locked.Stride, w));
            image.UnlockBits(locked);

            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write((byte)1);
            writer.Write((ushort)Math.Clamp(icon.xHotspot, 0, w - 1));
            writer.Write((ushort)Math.Clamp(icon.yHotspot, 0, h - 1));
            writer.Flush();
            image.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
        finally
        {
            if (icon.hbmColor != IntPtr.Zero) Native.DeleteObject(icon.hbmColor);
            if (icon.hbmMask != IntPtr.Zero) Native.DeleteObject(icon.hbmMask);
        }
    }

    private static bool NearInverted(bool[] inverts, int w, int h, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && inverts[ny * w + nx]) return true;
            }
        }
        return false;
    }
}

/// <summary>O ponteiro do outro computador, pronto para ser o ponteiro da janela da sessão.</summary>
internal sealed class RemotePointer : IDisposable
{
    private readonly IntPtr _handle;

    public Cursor Cursor { get; }
    public Size Size { get; }
    public Point HotSpot { get; }

    private RemotePointer(IntPtr handle, Size size, Point hotSpot)
    {
        _handle = handle;
        Cursor = new Cursor(handle);
        Size = size;
        HotSpot = hotSpot;
    }

    /// <summary>Ponteiro transparente quando oculto; null quando o conteúdo não serve.</summary>
    public static RemotePointer? FromPayload(byte[] p)
    {
        if (p.Length < 5 || p.Length > 512 * 1024) return null;
        bool hidden = (p[0] & 1) == 0;
        if (!hidden && p.Length == 5) return null;
        try
        {
            using var stream = new MemoryStream(p, 5, p.Length - 5, false);
            using var image = hidden ? new Bitmap(1, 1, PixelFormat.Format32bppArgb) : new Bitmap(stream);
            if (image.Width > PointerWatcher.MaxSize || image.Height > PointerWatcher.MaxSize) return null;
            var hotSpot = new Point(Math.Min(BitConverter.ToUInt16(p, 1), image.Width - 1), Math.Min(BitConverter.ToUInt16(p, 3), image.Height - 1));

            // Um ícone vira ponteiro quando ganha um ponto ativo.
            IntPtr icon = image.GetHicon();
            try
            {
                if (!Native.GetIconInfo(icon, out var info)) return null;
                info.fIcon = false;
                info.xHotspot = hotSpot.X;
                info.yHotspot = hotSpot.Y;
                IntPtr handle = Native.CreateIconIndirect(ref info);
                if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
                if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
                return handle == IntPtr.Zero ? null : new RemotePointer(handle, image.Size, hotSpot);
            }
            finally
            {
                Native.DestroyIcon(icon);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Cursor.Dispose();
        Native.DestroyIcon(_handle);
    }
}
