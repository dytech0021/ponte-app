namespace Ponte;

/// <summary>
/// Erros inesperados, um por bloco, na pasta de configuração (%APPDATA%\Ponte\erros.txt). O arquivo é
/// recomeçado quando passa de 512 KB.
/// </summary>
internal static class ErrorLog
{
    private static readonly object Gate = new();

    public static void Write(Exception? error)
    {
        if (error is null) return;
        try
        {
            lock (Gate)
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ponte");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "erros.txt");
                if (new FileInfo(file) is { Exists: true, Length: > 512 * 1024 }) File.Delete(file);
                File.AppendAllText(file, $"--- {DateTime.Now:dd/MM/yyyy HH:mm:ss} · Ponte {Updater.Current}" +
                    $"{(Elevation.IsElevated ? " (administrador)" : "")}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
