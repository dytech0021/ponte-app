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

    private const int TokenElevation = 20;
    private const uint ProcessQueryLimited = 0x1000, TokenQuery = 0x0008;
    private const int AccessDenied = 5;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static IntPtr _askedWindow;
    private static long _askedAt;
    private static bool _askedAnswer;

    /// <summary>
    /// A janela em primeiro plano é de um programa aberto como administrador e este Ponte não é: o Windows joga fora o
    /// mouse e o teclado que o Ponte injeta, e sem avisar (a função de injetar devolve sucesso). Só olhando de quem é a
    /// janela dá para saber. A resposta vale por 2 segundos para a mesma janela, porque isto roda a cada entrada.
    /// </summary>
    public static bool ForegroundOutOfReach()
    {
        if (IsElevated) return false;
        IntPtr window = TextFocus.GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        long now = Stopwatch.GetTimestamp();
        if (window == _askedWindow && Stopwatch.GetElapsedTime(_askedAt, now).TotalSeconds < 2) return _askedAnswer;

        bool elevated = false;
        Native.GetWindowThreadProcessId(window, out uint processId);
        IntPtr process = processId == 0 ? IntPtr.Zero : OpenProcess(ProcessQueryLimited, false, processId);
        if (process == IntPtr.Zero)
        {
            // Nem olhar o processo este Ponte pode: é de outro nível (um serviço, outro usuário).
            elevated = processId != 0 && Marshal.GetLastWin32Error() == AccessDenied;
        }
        else
        {
            try
            {
                if (OpenProcessToken(process, TokenQuery, out IntPtr token))
                {
                    try { elevated = GetTokenInformation(token, TokenElevation, out int isElevated, sizeof(int), out _) && isElevated != 0; }
                    finally { CloseHandle(token); }
                }
                else elevated = Marshal.GetLastWin32Error() == AccessDenied;
            }
            finally { CloseHandle(process); }
        }
        (_askedWindow, _askedAt, _askedAnswer) = (window, now, elevated);
        return elevated;
    }

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
