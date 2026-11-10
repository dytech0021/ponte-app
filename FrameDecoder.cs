using System.Collections.Concurrent;
using System.Drawing.Imaging;

namespace Ponte;

/// <summary>
/// Lê um quadro do modo compatível (o formato está em <see cref="ScreenCapturer"/>) e o desenha na imagem da tela remota.
/// 1.17.5: cada retângulo é decodificado num núcleo e gravado direto na memória da imagem. Antes era um de cada vez,
/// passando pelo desenho do GDI+: uma área de trabalho de 3440 x 2880 levava 61 ms por quadro inteiro, com a janela
/// parada esperando; assim leva perto de 8.
/// </summary>
internal static class FrameDecoder
{
    internal readonly record struct Part(int X, int Y, int Width, int Height, int Offset, int Length)
    {
        public Rectangle Bounds => new(X, Y, Width, Height);
    }

    /// <summary>Confere o quadro inteiro antes de qualquer desenho: nada fora da imagem, nada além do que chegou.</summary>
    public static (int Width, int Height, Part[] Parts) Parse(byte[] p)
    {
        if (p.Length < 6) throw new InvalidDataException("Quadro incompleto.");
        int width = BitConverter.ToUInt16(p, 0), height = BitConverter.ToUInt16(p, 2), count = BitConverter.ToUInt16(p, 4);
        if (width == 0 || height == 0) throw new InvalidDataException("Quadro sem dimensões.");
        var parts = new Part[count];
        int offset = 6;
        for (int i = 0; i < count; i++)
        {
            if (offset + 12 > p.Length) throw new InvalidDataException("Quadro incompleto.");
            int x = BitConverter.ToUInt16(p, offset), y = BitConverter.ToUInt16(p, offset + 2);
            int w = BitConverter.ToUInt16(p, offset + 4), h = BitConverter.ToUInt16(p, offset + 6);
            int length = BitConverter.ToInt32(p, offset + 8);
            offset += 12;
            if (length <= 0 || length > p.Length - offset || x + w > width || y + h > height)
                throw new InvalidDataException("Quadro incompleto.");
            parts[i] = new Part(x, y, w, h, offset, length);
            offset += length;
        }
        return (width, height, parts);
    }

    /// <summary>
    /// Desenha as partes do quadro em <paramref name="canvas"/>, que tem o tamanho que o quadro diz. Devolve o
    /// retângulo que as contém (vazio se o quadro veio sem partes).
    /// </summary>
    public static Rectangle Draw(Bitmap canvas, byte[] payload, Part[] parts)
    {
        if (parts.Length == 0) return Rectangle.Empty;
        var bounds = parts[0].Bounds;
        long bytes = 0;
        foreach (var part in parts)
        {
            bounds = Rectangle.Union(bounds, part.Bounds);
            bytes += part.Length;
        }

        // As que não chegam do tamanho do retângulo (nenhum Ponte manda assim) ficam para o desenho com ajuste, no fim.
        var odd = new ConcurrentQueue<Part>();
        var data = canvas.LockBits(new Rectangle(0, 0, canvas.Width, canvas.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppRgb);
        try
        {
            if (parts.Length > 1 && bytes >= 24 * 1024)
            {
                try { Parallel.For(0, parts.Length, i => { if (!Decode(payload, parts[i], data)) odd.Enqueue(parts[i]); }); }
                catch (AggregateException ex) when (ex.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
            }
            else
            {
                foreach (var part in parts)
                    if (!Decode(payload, part, data)) odd.Enqueue(part);
            }
        }
        finally { canvas.UnlockBits(data); }

        if (!odd.IsEmpty)
        {
            using var g = Graphics.FromImage(canvas);
            foreach (var part in odd)
            {
                using var stream = new MemoryStream(payload, part.Offset, part.Length, false);
                using var image = Open(stream);
                g.DrawImage(image, part.X, part.Y, part.Width, part.Height);
            }
        }
        return bounds;
    }

    // Uma imagem estragada vira "quadro inválido" (a sessão termina como conexão com defeito), e não um erro do Ponte.
    private static Bitmap Open(Stream stream)
    {
        try { return new Bitmap(stream); }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            throw new InvalidDataException("Quadro com imagem inválida.");
        }
    }

    /// <summary>Decodifica uma parte direto no lugar dela. Falso se a imagem não tem o tamanho do retângulo.</summary>
    private static unsafe bool Decode(byte[] payload, Part part, BitmapData canvas)
    {
        using var stream = new MemoryStream(payload, part.Offset, part.Length, false);
        using var image = Open(stream);
        if (image.Width != part.Width || image.Height != part.Height) return false;
        BitmapData source;
        // Em 24 bits, que é como o JPEG sai: pedir 32 aqui faria o GDI+ converter, e essa conversão dele é lenta.
        try { source = image.LockBits(new Rectangle(0, 0, part.Width, part.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb); }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            throw new InvalidDataException("Quadro com imagem inválida.");
        }
        try
        {
            for (int y = 0; y < part.Height; y++)
            {
                byte* from = (byte*)source.Scan0 + (long)y * source.Stride;
                uint* to = (uint*)((byte*)canvas.Scan0 + (long)(part.Y + y) * canvas.Stride + part.X * 4);
                // Quatro bytes de cada vez (o quarto é do ponto seguinte e é trocado pelo opaco); o último ponto da
                // linha vai byte a byte, para não ler além dela.
                int last = part.Width - 1;
                for (int x = 0; x < last; x++, from += 3) to[x] = *(uint*)from | 0xFF000000u;
                to[last] = (uint)(from[0] | from[1] << 8 | from[2] << 16) | 0xFF000000u;
            }
        }
        finally { image.UnlockBits(source); }
        return true;
    }
}
