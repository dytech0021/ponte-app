using System.Net.Sockets;

namespace Ponte;

/// <summary>
/// Uma linha por início e por fim de sessão, dos dois lados, em %APPDATA%\Ponte\sessoes.txt: quanto durou, quanto
/// passou pela conexão e por que terminou. Quando uma conexão cai, é aqui que se vê se foi a rede (com o código do
/// Windows), o outro lado ou o próprio Ponte. O arquivo é recomeçado quando passa de 256 KB.
/// </summary>
internal static class SessionLog
{
    private static readonly object Gate = new();

    /// <summary>Pasta do registro; o autoteste troca pela dele, para não misturar as sessões de teste com as de verdade.</summary>
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ponte");

    public static string FilePath => Path.Combine(Folder, "sessoes.txt");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                string dir = Folder;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "sessoes.txt");
                if (new FileInfo(file) is { Exists: true, Length: > 256 * 1024 }) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:dd/MM/yyyy HH:mm:ss} · {Updater.Current} · {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Duration(DateTime started)
    {
        var d = DateTime.Now - started;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:00}min"
            : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}min{d.Seconds:00}s" : $"{d.Seconds}s";
    }

    public static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0.0} MB";

    /// <summary>A falha com as causas por dentro, e o código do Windows quando é da rede (ConnectionReset, TimedOut...).</summary>
    public static string Describe(Exception error)
    {
        var parts = new List<string>();
        for (var e = error; e is not null; e = e.InnerException)
        {
            string part = e is SocketException s ? $"{s.SocketErrorCode} ({s.ErrorCode}): {s.Message}" : $"{e.GetType().Name}: {e.Message}";
            if (!parts.Contains(part)) parts.Add(part);
        }
        return string.Join(" <- ", parts);
    }
}
