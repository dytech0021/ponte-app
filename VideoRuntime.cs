using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Ponte;

/// <summary>
/// O FFmpeg (compilação LGPL) que codifica e decodifica H.264 e H.265. Não vem dentro do Ponte.exe: são
/// 130 MB que toda atualização carregaria para todos os computadores. Ele é baixado do servidor de
/// retransmissão na primeira vez que alguém escolhe H.264 ou H.265, comprimido, e só vale se o resumo bater
/// com o que está fixado aqui, dentro do executável assinado. Fica em %LOCALAPPDATA%\Ponte\video.
/// </summary>
internal static class VideoRuntime
{
    internal const string Sha256 = "ddf94f763d015cf13e76b515d92040364b4fcbc9f137f5b9238f116b26b8d245";
    internal const long Size = 134_092_288;
    private const long PackageMax = 96L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ponte", "video", Sha256);
    public static string ExePath => Path.Combine(Folder, "ffmpeg.exe");
    /// <summary>Nome do pacote comprimido no servidor (pasta de atualização).</summary>
    public static string PackageName => $"video-{Sha256}.br";

    /// <summary>Conferência rápida, só pelo tamanho; o resumo é conferido a cada vez que o programa é aberto.</summary>
    public static bool Present
    {
        get
        {
            try { return new FileInfo(ExePath) is { Exists: true, Length: Size }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>
    /// Garante o FFmpeg conferido, baixando-o se preciso. <paramref name="progress"/> recebe a porcentagem.
    /// Duas sessões pedindo ao mesmo tempo esperam o mesmo download.
    /// </summary>
    public static async Task<string> EnsureAvailableAsync(AppConfig config, Action<int>? progress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (Matches(ExePath)) return ExePath;
            Directory.CreateDirectory(Folder);
            string id = Guid.NewGuid().ToString("N");
            string package = Path.Combine(Folder, id + ".br.tmp"), exe = Path.Combine(Folder, id + ".exe.tmp");
            try
            {
                int last = -1;
                await using (var file = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await Transport.DownloadAsync(config, "VIDEO", Sha256, PackageMax, file, (read, total) =>
                    {
                        int percent = (int)(read * 100 / total);
                        if (percent != last) progress?.Invoke(last = percent);
                    }, ct);
                await using (var input = File.OpenRead(package))
                await using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
                await using (var output = new FileStream(exe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1024 * 1024];
                    long written = 0;
                    int n;
                    while ((n = await brotli.ReadAsync(buffer, ct)) > 0)
                    {
                        if ((written += n) > Size) throw new PonteException("O componente de vídeo baixado não confere.");
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                }
                if (!Matches(exe)) throw new PonteException("O componente de vídeo baixado não confere; nada foi usado.");
                File.Move(exe, ExePath, true);
                return ExePath;
            }
            catch (InvalidDataException) { throw new PonteException("O componente de vídeo baixado veio corrompido."); }
            finally
            {
                foreach (string temp in new[] { package, exe })
                    try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Para quem publica: comprime o ffmpeg.exe conferido no pacote que vai para o servidor.
    /// Brotli no nível máximo deixa o download em cerca de um terço do original.
    /// </summary>
    public static int Pack(string ffmpeg, string output)
    {
        if (!Matches(ffmpeg))
        {
            Console.Error.WriteLine($"{ffmpeg} não é o FFmpeg fixado em VideoRuntime.Sha256.");
            return 2;
        }
        using var input = File.OpenRead(ffmpeg);
        using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
        using var encoder = new BrotliEncoder(11, 24);
        byte[] source = new byte[1024 * 1024], target = new byte[2 * 1024 * 1024];
        int n;
        while ((n = input.Read(source)) > 0)
        {
            var pending = source.AsSpan(0, n);
            while (pending.Length > 0)
            {
                encoder.Compress(pending, target, out int consumed, out int written, false);
                file.Write(target, 0, written);
                pending = pending[consumed..];
            }
        }
        OperationStatus status;
        do
        {
            status = encoder.Flush(target, out int written);
            file.Write(target, 0, written);
        } while (status == OperationStatus.DestinationTooSmall);
        do
        {
            status = encoder.Compress(ReadOnlySpan<byte>.Empty, target, out _, out int written, true);
            file.Write(target, 0, written);
        } while (status == OperationStatus.DestinationTooSmall);
        return 0;
    }

    private static readonly object HeldGate = new();
    private static FileStream? _held;

    /// <summary>
    /// Caminho do FFmpeg conferido. Na primeira vez confere o resumo e mantém o arquivo aberto sem
    /// permitir escrita, troca de nome ou exclusão: ninguém o substitui enquanto o Ponte estiver aberto.
    /// </summary>
    public static string VerifiedPath()
    {
        lock (HeldGate)
        {
            if (_held is null)
            {
                FileStream file;
                try { file = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new IOException("O componente de vídeo ainda não foi baixado neste computador."); }
                if (file.Length != Size || !Convert.ToHexString(SHA256.HashData(file)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    file.Dispose();
                    throw new IOException("O componente de vídeo deste computador não confere.");
                }
                _held = file;
            }
            // O Windows carrega bibliotecas da pasta do programa antes das do sistema. Como administrador, o FFmpeg
            // não pode achar ao lado dele nada além dele mesmo: o que aparecer ali é apagado antes de ele abrir.
            foreach (string entry in Directory.EnumerateFileSystemEntries(Folder))
            {
                // Os .tmp são de um download em andamento; uma biblioteca só é carregada pelo nome exato (.dll).
                string name = Path.GetFileName(entry);
                if (name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Há um arquivo estranho na pasta do componente de vídeo: " + entry);
                }
            }
            return ExePath;
        }
    }

    internal static bool Matches(string path)
    {
        try
        {
            if (new FileInfo(path) is not { Exists: true, Length: Size }) return false;
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
