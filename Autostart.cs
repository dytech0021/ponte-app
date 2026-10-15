using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Ponte;

/// <summary>
/// Início com o Windows. O comum é a lista de programas de inicialização do próprio usuário: não precisa de
/// administrador e aparece no Gerenciador de Tarefas, em "Aplicativos de inicialização". Com "abrir como
/// administrador", quem abre o Ponte é uma tarefa agendada com privilégio máximo, que eleva sem o aviso do
/// UAC (que ninguém estaria ali para responder). Nos dois casos o Ponte abre minimizado na barra de tarefas.
/// </summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ponte";
    private const string TaskName = "Ponte";

    /// <summary>Argumento com que o Windows abre o Ponte na inicialização.</summary>
    public const string Argument = "--inicio";

    /// <summary>
    /// Só a cópia de uso (arquivo único, perfil padrão) entra na inicialização. A de desenvolvimento e
    /// as abertas com --perfil, usadas em testes, não mexem no registro.
    /// </summary>
    public static bool Managed(string[] args) => Updater.IsSingleFile && !args.Contains("--perfil") && !args.Contains("--captura-ui");

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    /// <summary>
    /// Deixa a inicialização igual à escolha do usuário, com o caminho atual do programa (ele pode ter sido
    /// movido de pasta). Só um Ponte já como administrador consegue criar ou apagar a tarefa; sem ela, vale a
    /// entrada comum. Devolve o aviso a mostrar quando algo passou a valer agora, ou null.
    /// </summary>
    public static string? Apply(AppConfig config)
    {
        bool elevatedStart = config.StartWithWindows && config.RunElevated && (Elevation.IsElevated || Elevation.CanElevateSameUser);
        if (Elevation.IsElevated)
        {
            if (elevatedStart)
            {
                bool existed = TaskMatches();
                if (existed || WriteTask())
                {
                    Write(ValueName, null);
                    return existed ? null : "O Ponte passa a abrir como administrador ao entrar no Windows, minimizado.";
                }
            }
            else
            {
                DeleteTask();
            }
        }
        else if (elevatedStart && TaskExists())
        {
            // A tarefa já abre o Ponte como administrador; a entrada comum abriria uma segunda cópia.
            Write(ValueName, null);
            return null;
        }
        return Write(ValueName, config.StartWithWindows ? Command : null)
            ? "O Ponte passa a iniciar com o Windows, minimizado. Para desligar, abra Configurações." : null;
    }

    private static (int Code, string Output) Schtasks(params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(15000) ? (process.ExitCode, output) : (-1, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
        }
    }

    internal static bool TaskExists() => Schtasks("/Query", "/TN", TaskName).Code == 0;

    // A tarefa existe e abre este mesmo executável.
    private static bool TaskMatches()
    {
        var (code, xml) = Schtasks("/Query", "/TN", TaskName, "/XML");
        return code == 0 && xml.Contains(SecurityElement.Escape(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase);
    }

    internal static void DeleteTask()
    {
        if (TaskExists()) Schtasks("/Delete", "/TN", TaskName, "/F");
    }

    // Ao entrar no Windows, só para este usuário, com privilégio máximo, sem prazo para terminar e com prioridade
    // normal (a padrão das tarefas é abaixo do normal e o Ponte herdaria isso).
    private static bool WriteTask()
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Environment.ProcessPath!), dir = SecurityElement.Escape(Path.GetDirectoryName(Environment.ProcessPath!)!);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Abre o Ponte como administrador ao entrar no Windows.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>{Argument}</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), $"ponte-tarefa-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F").Code == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>Grava o comando, ou remove a entrada quando ele é nulo. Verdadeiro se a entrada não existia e passou a existir.</summary>
    internal static bool Write(string name, string? command)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            string? current = key.GetValue(name) as string;
            if (command is null)
            {
                if (current is not null) key.DeleteValue(name, false);
                return false;
            }
            if (current != command) key.SetValue(name, command);
            return current is null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Sem acesso ao registro o Ponte só não inicia sozinho; o resto funciona.
            return false;
        }
    }
}
