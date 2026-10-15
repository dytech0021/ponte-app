using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Ponte;

/// <summary>
/// O Windows não deixa um programa comum mandar mouse e teclado para a janela de um programa aberto
/// como administrador. Para controlar esses programas, o Ponte do computador controlado precisa
/// estar ele mesmo como administrador; a elevação é a normal do Windows (UAC), nada é contornado.
/// </summary>
internal static class Elevation
{
    private const int TokenElevationType = 18;
    private const int Limited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int needed);

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Conta administradora que ainda não elevou: o Windows eleva sem pedir a senha de outra conta.</summary>
    public static bool CanElevateSameUser
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return GetTokenInformation(identity.Token, TokenElevationType, out int type, sizeof(int), out _) && type == Limited;
        }
    }

    /// <summary>
    /// O UAC está em "elevar sem avisar" para administradores: pedir administrador não mostra aviso nenhum, então
    /// dá para elevar mesmo sem ninguém na frente do computador, como na inicialização do Windows.
    /// </summary>
    public static bool ElevatesSilently
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                return key?.GetValue("ConsentPromptBehaviorAdmin") is int consent && consent == 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Abre outra cópia deste programa como administrador. Falso se o usuário recusou ou não foi possível.</summary>
    public static bool TryRelaunchElevated(IEnumerable<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (string a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
