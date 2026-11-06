using System.Diagnostics;
namespace Ponte;
internal static class RemotePower
{
    // A test can replace the action; no test ever reboots the user's computer.
    internal static Action? TestRestart { get; set; }
    public static void ScheduleRestart()
    {
        if (TestRestart is { } test) { test(); return; }
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // No /f: do not force-close unsaved work. The explicit remote confirmation precedes this message.
        foreach (string arg in new[] { "/r", "/t", "0", "/d", "p:0:0", "/c", "Reinício solicitado em sessão autenticada do Ponte." }) info.ArgumentList.Add(arg);
        using var p = Process.Start(info) ?? throw new PonteException("O Windows não iniciou o pedido de reinício.");
        string output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new PonteException("O Windows recusou o reinício: " + output.Trim());
    }
}
