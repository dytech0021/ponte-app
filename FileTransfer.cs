namespace Ponte;

/// <summary>Grava os arquivos que chegam numa pasta fixa. Nada do que chega é aberto nem executado.</summary>
internal sealed class FileReceiver(string directory) : IDisposable
{
    public const long MaxSize = 8L << 30;

    private FileStream? _file;
    private string _path = "";
    private long _remaining;

    /// <summary>Trata as mensagens de arquivo; devolve o caminho quando um arquivo termina de chegar.</summary>
    public string? Handle(Msg type, byte[] payload)
    {
        switch (type)
        {
            case Msg.FileStart:
                Abort();
                var start = Channel.FromJson<FileStartMsg>(payload);
                if (start.Size < 0 || start.Size > MaxSize) throw new InvalidDataException("Tamanho de arquivo inválido.");
                Directory.CreateDirectory(directory);
                _path = UniquePath(directory, SafeName(start.Name));
                _file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write);
                _remaining = start.Size;
                return null;

            case Msg.FileChunk when _file is not null:
                if (payload.Length > _remaining)
                {
                    Abort();
                    throw new InvalidDataException("Arquivo maior que o anunciado.");
                }
                _file.Write(payload);
                _remaining -= payload.Length;
                return null;

            case Msg.FileEnd when _file is not null:
                if (_remaining != 0)
                {
                    Abort();
                    return null;
                }
                _file.Dispose();
                _file = null;
                return _path;
        }
        return null;
    }

    // O nome vem do outro computador: fica só o último componente, sem caracteres ou nomes reservados.
    private static string SafeName(string name)
    {
        string n = name.Replace('\\', '/').Split('/').Last();
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().TrimEnd('.');
        if (n.Length > 150) n = n[..150];
        string stem = Path.GetFileNameWithoutExtension(n).ToUpperInvariant();
        string[] reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
        return n.Length == 0 || reserved.Contains(stem) ? "arquivo_" + n : n;
    }

    private static string UniquePath(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        for (int i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    private void Abort()
    {
        if (_file is null) return;
        _file.Dispose();
        _file = null;
        try { File.Delete(_path); }
        catch (IOException) { }
    }

    public void Dispose() => Abort();
}

internal static class FileSender
{
    public static async Task SendAsync(Channel channel, string path, IProgress<int>? progress, CancellationToken ct)
    {
        await channel.FileLock.WaitAsync(ct);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await channel.SendJsonAsync(Msg.FileStart, new FileStartMsg(Path.GetFileName(path), file.Length), ct);
            var buffer = new byte[64 * 1024];
            long sent = 0;
            int n;
            while ((n = await file.ReadAsync(buffer, ct)) > 0)
            {
                await channel.SendAsync(Msg.FileChunk, buffer.AsMemory(0, n), ct);
                sent += n;
                progress?.Report(file.Length == 0 ? 100 : (int)(sent * 100 / file.Length));
            }
            await channel.SendAsync(Msg.FileEnd, default, ct);
        }
        finally
        {
            channel.FileLock.Release();
        }
    }
}
