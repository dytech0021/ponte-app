using System.Text;

namespace Ponte;

/// <summary>
/// Gera QR Code (modo byte, correção de erros nível M, versões 1 a 10, até 213 bytes), para o celular
/// abrir um link apontando a câmera. Segue a norma ISO/IEC 18004; está aqui para o Ponte continuar sem
/// pacotes externos. O resultado é a matriz de módulos, sem a margem: verdadeiro é escuro.
/// </summary>
internal static class QrCode
{
    // Nível M, por versão (índice 1 a 10): códigos de correção por bloco e quantidade de blocos.
    private static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    private static readonly int[] BlockCount = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };

    // Centros dos padrões de alinhamento, por versão.
    private static readonly int[][] Alignment =
    {
        Array.Empty<int>(), Array.Empty<int>(), new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 },
        new[] { 6, 34 }, new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };

    private const int FormatLevelM = 0;   // bits do nível na informação de formato: L=1, M=0, Q=3, H=2

    /// <param name="forcedMask">De 0 a 7 para usar uma máscara fixa (testes); -1 escolhe a de menor penalidade, como manda a norma.</param>
    public static bool[,] Encode(string text, int forcedMask = -1)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        while (version <= 10 && 4 + CountBits(version) + data.Length * 8 > DataCodewords(version) * 8) version++;
        if (version > 10) throw new ArgumentException("Texto longo demais para o QR Code.", nameof(text));

        var qr = new Matrix(version);
        qr.DrawFunctionPatterns();
        qr.DrawCodewords(AddEccAndInterleave(version, DataBytes(version, data)));

        int mask = forcedMask;
        if (mask < 0)
        {
            int best = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                qr.ApplyMask(m);
                qr.DrawFormat(m);
                int penalty = qr.Penalty();
                if (penalty < best)
                {
                    best = penalty;
                    mask = m;
                }
                qr.ApplyMask(m);   // a máscara é um "ou exclusivo": aplicar de novo desfaz
            }
        }
        qr.ApplyMask(mask);
        qr.DrawFormat(mask);
        return qr.Modules;
    }

    private static int CountBits(int version) => version < 10 ? 8 : 16;

    private static int RawDataModules(int version)
    {
        int result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            int align = version / 7 + 2;
            result -= (25 * align - 10) * align - 55;
            if (version >= 7) result -= 36;
        }
        return result;
    }

    private static int DataCodewords(int version) => RawDataModules(version) / 8 - EccPerBlock[version] * BlockCount[version];

    // Modo byte (0100), quantidade de bytes, os bytes, terminador, completa o byte e enche com 0xEC/0x11.
    private static byte[] DataBytes(int version, byte[] data)
    {
        var bits = new List<bool>();
        void Append(int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }
        Append(0b0100, 4);
        Append(data.Length, CountBits(version));
        foreach (byte b in data) Append(b, 8);
        int capacity = DataCodewords(version) * 8;
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var result = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) result[i >> 3] |= (byte)(0x80 >> (i & 7));
        return result;
    }

    // Divide em blocos, calcula a correção de cada um e intercala: primeiro os dados, depois a correção.
    private static byte[] AddEccAndInterleave(int version, byte[] data)
    {
        int blocks = BlockCount[version], eccLength = EccPerBlock[version], raw = RawDataModules(version) / 8;
        int shortBlocks = blocks - raw % blocks, shortLength = raw / blocks;
        byte[] divisor = ReedSolomonDivisor(eccLength);

        var all = new byte[blocks][];
        for (int i = 0, k = 0; i < blocks; i++)
        {
            int dataLength = shortLength - eccLength + (i < shortBlocks ? 0 : 1);
            byte[] dat = data.AsSpan(k, dataLength).ToArray();
            k += dataLength;
            byte[] ecc = ReedSolomonRemainder(dat, divisor);
            // Todos os blocos ficam com o tamanho do longo; nos curtos sobra uma posição vazia antes da correção.
            var block = new byte[shortLength + 1];
            dat.CopyTo(block, 0);
            ecc.CopyTo(block, shortLength + 1 - eccLength);
            all[i] = block;
        }

        var result = new List<byte>(raw);
        for (int i = 0; i <= shortLength; i++)
            for (int j = 0; j < blocks; j++)
                if (i != shortLength - eccLength || j >= shortBlocks) result.Add(all[j][i]);
        return result.ToArray();
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }
        return result;
    }

    // Multiplicação no corpo de Galois GF(2^8), com o polinômio 0x11D.
    private static byte Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private sealed class Matrix(int version)
    {
        public readonly int Size = version * 4 + 17;
        public readonly bool[,] Modules = new bool[version * 4 + 17, version * 4 + 17];   // [linha, coluna]
        private readonly bool[,] _function = new bool[version * 4 + 17, version * 4 + 17];

        private void Set(int x, int y, bool dark)
        {
            Modules[y, x] = dark;
            _function[y, x] = true;
        }

        public void DrawFunctionPatterns()
        {
            for (int i = 0; i < Size; i++)
            {
                Set(6, i, i % 2 == 0);
                Set(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);

            int[] centers = Alignment[version];
            int last = centers.Length - 1;
            for (int i = 0; i < centers.Length; i++)
                for (int j = 0; j < centers.Length; j++)
                    if (!(i == 0 && j == 0 || i == 0 && j == last || i == last && j == 0))
                        DrawAlignment(centers[i], centers[j]);

            DrawFormat(0);   // reserva o lugar; o valor certo entra depois da máscara
            DrawVersion();
        }

        private void DrawFinder(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int distance = Math.Max(Math.Abs(dx), Math.Abs(dy)), xx = x + dx, yy = y + dy;
                    if (xx >= 0 && xx < Size && yy >= 0 && yy < Size) Set(xx, yy, distance != 2 && distance != 4);
                }
        }

        private void DrawAlignment(int x, int y)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Set(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        public void DrawFormat(int mask)
        {
            int data = FormatLevelM << 3 | mask, rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            bool Bit(int i) => ((bits >> i) & 1) != 0;

            for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
            Set(8, 7, Bit(6));
            Set(8, 8, Bit(7));
            Set(7, 8, Bit(8));
            for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));

            for (int i = 0; i < 8; i++) Set(Size - 1 - i, 8, Bit(i));
            for (int i = 8; i < 15; i++) Set(8, Size - 15 + i, Bit(i));
            Set(8, Size - 8, true);   // módulo sempre escuro
        }

        private void DrawVersion()
        {
            if (version < 7) return;
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = Size - 11 + i % 3, b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }

        // Preenche em zigue-zague, de baixo para cima e de cima para baixo, em pares de colunas da direita
        // para a esquerda, pulando a coluna do padrão de temporização.
        public void DrawCodewords(byte[] data)
        {
            int i = 0;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < Size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? Size - 1 - vert : vert;
                        if (!_function[y, x] && i < data.Length * 8)
                        {
                            Modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
            }
        }

        public void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    if (_function[y, x]) continue;
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => (x / 3 + y / 2) % 2 == 0,
                        5 => x * y % 2 + x * y % 3 == 0,
                        6 => (x * y % 2 + x * y % 3) % 2 == 0,
                        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                    };
                    if (invert) Modules[y, x] = !Modules[y, x];
                }
        }

        // Penalidade da norma: sequências longas da mesma cor, blocos 2x2, trechos parecidos com os
        // padrões de localização e desequilíbrio entre escuro e claro. A máscara de menor penalidade é a
        // mais fácil de ler.
        public int Penalty()
        {
            int penalty = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < Size; a++)
                {
                    int run = 1;
                    for (int b = 1; b <= Size; b++)
                    {
                        if (b < Size && At(pass, a, b) == At(pass, a, b - 1))
                        {
                            run++;
                            continue;
                        }
                        if (run >= 5) penalty += 3 + (run - 5);
                        run = 1;
                    }
                    for (int b = 0; b + 6 < Size; b++)
                    {
                        if (At(pass, a, b) && !At(pass, a, b + 1) && At(pass, a, b + 2) && At(pass, a, b + 3) && At(pass, a, b + 4) &&
                            !At(pass, a, b + 5) && At(pass, a, b + 6) && (Light(pass, a, b - 4, b) || Light(pass, a, b + 7, b + 11)))
                            penalty += 40;
                    }
                }

            for (int y = 0; y + 1 < Size; y++)
                for (int x = 0; x + 1 < Size; x++)
                {
                    bool c = Modules[y, x];
                    if (c == Modules[y, x + 1] && c == Modules[y + 1, x] && c == Modules[y + 1, x + 1]) penalty += 3;
                }

            int dark = 0;
            foreach (bool m in Modules)
                if (m) dark++;
            int total = Size * Size;
            penalty += ((Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1) * 10;
            return penalty;
        }

        // pass 0 percorre linhas (a = linha, b = coluna); pass 1, colunas.
        private bool At(int pass, int a, int b) => pass == 0 ? Modules[a, b] : Modules[b, a];

        // Fora da matriz conta como claro: é a margem.
        private bool Light(int pass, int a, int from, int to)
        {
            for (int b = Math.Max(0, from); b < Math.Min(Size, to); b++)
                if (At(pass, a, b)) return false;
            return true;
        }
    }
}
