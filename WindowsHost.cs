using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;

namespace Ponte;

// SCM supervisor in session 0; a headless SYSTEM worker runs in the console session. The regular app
// owns the host while its desktop is available. An exclusive file lease prevents duplicate IDs/listeners.
internal static class WindowsHost
{
    public const string Name = "PonteHost";
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PonteHost");
    public static readonly string Bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PonteHost");
    public static string Profile => Path.Combine(Root, "host");
    private static string Coordination => Path.Combine(Root, "coord");
    private static string RequestFile => Path.Combine(Coordination, "owner.json");
    public static bool Agent { get; private set; }
    public static bool Installed => File.Exists(Path.Combine(Root, "owner.json"));
    public static bool ActiveSession
    {
        get { try { return File.Exists(Path.Combine(Profile, "active")) && DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(Profile, "active")) < TimeSpan.FromSeconds(5); }
            catch (UnauthorizedAccessException) { return Installed; } }
    }

    public static void RefreshBinary(AppConfig c, bool force = false)
    {
        if (!Owns(c) || !Elevation.IsElevated || !Updater.IsSingleFile) return;
        string destination = Path.Combine(Bin, "Ponte.exe");
        if (!force && Version.TryParse(FileVersionInfo.GetVersionInfo(destination).FileVersion, out var installed) && installed >= Updater.Current) return;
        string stage = destination + ".novo", old = destination + ".antigo-" + Guid.NewGuid().ToString("N");
        File.Copy(Environment.ProcessPath!, stage, true);
        Sc("stop", Name, allowExists: true);
        WaitStopped();
        File.Replace(stage, destination, old);
        try { Sc("start", Name); }
        catch
        {
            File.Move(old, destination, true); Sc("start", Name, allowExists: true); throw;
        }
    }
    public static bool Owns(AppConfig c)
    {
        try { return Installed && TrustedDirectory(Root) && File.ReadAllText(Path.Combine(Root, "owner.json")) == WindowsIdentity.GetCurrent().User?.Value &&
                File.ReadAllText(Path.Combine(Root, "id.txt")) == c.Id; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Install(AppConfig config)
    {
        if (!Elevation.IsElevated) throw new PonteException("Abra o Ponte como administrador para instalar o serviço.");
        if (!Updater.IsSingleFile) throw new PonteException("Instale o serviço usando o Ponte.exe publicado.");
        if (!config.HasPassword && config.Trusted.Count == 0)
            throw new PonteException("Defina uma senha de acesso ou autorize um computador antes de instalar o serviço.");
        if (Installed && !Owns(config)) throw new PonteException("O serviço já pertence a outro perfil. Remova-o usando o perfil original.");
        foreach (string path in new[] { Root, Profile, Bin, Coordination })
            if (Directory.Exists(path) && !TrustedDirectory(path)) throw new PonteException("A pasta do serviço não é uma pasta administrativa segura: " + path);
        Directory.CreateDirectory(Root); Protect(Root);
        Directory.CreateDirectory(Profile); Protect(Profile);
        Directory.CreateDirectory(Bin); Protect(Bin);
        Directory.CreateDirectory(Coordination); Protect(Coordination, WindowsIdentity.GetCurrent().User);
        string exe = Path.Combine(Bin, "Ponte.exe");
        if (!Installed)
        {
            File.Copy(Environment.ProcessPath!, exe + ".novo", true);
            if (File.Exists(exe)) File.Move(exe, exe + ".antes-" + Guid.NewGuid().ToString("N"));
            File.Move(exe + ".novo", exe);
        }
        File.WriteAllText(Path.Combine(Root, "owner.json"), WindowsIdentity.GetCurrent().User!.Value);
        File.WriteAllText(Path.Combine(Root, "id.txt"), config.Id);
        // Read-only identity markers let the owning non-elevated UI recognize this installation.
        foreach (var file in new[] { "owner.json", "id.txt" })
        {
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            new FileInfo(Path.Combine(Root, file)).SetAccessControl(acl);
        }
        // Traversal/read only at the root; secrets remain in the protected host subdirectory.
        var rootAcl = new DirectoryInfo(Root).GetAccessControl();
        rootAcl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(rootAcl);
        Export(config);
        config.RunElevated = true;
        config.Save();
        Sc("create", Name, "binPath=", $"\"{exe}\" --servico", "start=", "auto", "DisplayName=", "Ponte - acesso antes do login", allowExists: true);
        Sc("description", Name, "Acesso remoto autorizado antes do login e na tela bloqueada. Configurável e removível pelo Ponte.");
        Sc("failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000");
        Sc("start", Name, allowExists: true);
        Heartbeat(true); // The existing app keeps hosting until it is reopened under lease coordination.
        SessionLog.Write("serviço: instalado para o mesmo ID e autorizações deste perfil");
    }

    public static void Remove(AppConfig c)
    {
        if (!Elevation.IsElevated || !Owns(c)) throw new PonteException("Remova o serviço como administrador usando o perfil que o instalou.");
        Sc("stop", Name, allowExists: true);
        WaitStopped();
        Sc(["delete", Name], true);
        File.Delete(Path.Combine(Root, "owner.json"));
        // Keep the protected profile as a recovery backup; removal never recursively deletes user files.
        SessionLog.Write("serviço: removido; perfil protegido mantido como backup");
    }

    public static void Export(AppConfig config)
    {
        if (Agent || !Elevation.IsElevated || !Owns(config)) return;
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
        byte[] pfx = ProtectedData.Unprotect(Convert.FromBase64String(config.CertPfx), null, DataProtectionScope.CurrentUser);
        try { copy.CertPfx = Convert.ToBase64String(ProtectedData.Protect(pfx, null, DataProtectionScope.LocalMachine)); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        copy.AccountToken = copy.AccountEmail = copy.AccountUserId = "";
        copy.Saved.Clear(); copy.Recent.Clear(); copy.KnownHosts.Clear(); copy.AccountPendingDeletes.Clear(); copy.AutoUpdate = false;
        string destination = Path.Combine(Profile, "config.json");
        // DPAPI produces random ciphertext: compare the effective host settings rather than PFX ciphertext.
        if (File.Exists(destination))
        {
            var previous = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(destination));
            if (previous is not null)
            {
                using var oldCertificate = Security.LoadPfx(ProtectedData.Unprotect(Convert.FromBase64String(previous.CertPfx), null, DataProtectionScope.LocalMachine));
                if (Security.IdFromCertificate(oldCertificate) == config.Id) copy.CertPfx = previous.CertPfx;
                else File.Copy(destination, destination + ".antes-instalar", true);
                if (JsonSerializer.Serialize(previous) == JsonSerializer.Serialize(copy)) return;
            }
        }
        File.WriteAllText(destination + ".tmp", JsonSerializer.Serialize(copy)); File.Move(destination + ".tmp", destination, true);
    }

    private static void Protect(string path, SecurityIdentifier? owner = null)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new PonteException("Não é permitido instalar o serviço em um link de diretório.");
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (owner is not null || path == Bin) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    private static bool TrustedDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        var acl = new DirectoryInfo(path).GetAccessControl();
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner) return false;
        if (!owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid)) return false;
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) return false;
        }
        return true;
    }

    private static void Sc(params string[] args) => Sc(args, false);
    private static void WaitStopped()
    {
        IntPtr manager = OpenSCManager(null, null, 1), service = manager == IntPtr.Zero ? IntPtr.Zero : OpenService(manager, Name, 4);
        try
        {
            if (service == IntPtr.Zero) { if (Marshal.GetLastWin32Error() == 1060) return; throw new Win32Exception(); }
            for (int i = 0; i < 100; i++) { if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(); if (status.state == 1) return; Thread.Sleep(100); }
            throw new PonteException("O serviço ainda está encerrando. Tente novamente em alguns segundos.");
        }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); if (manager != IntPtr.Zero) CloseServiceHandle(manager); }
    }
    private static void Sc(string a, string b, bool allowExists) => Sc([a, b], allowExists);
    private static void Sc(string a, string b, string c, string d, string e, string f, string g, string h, bool allowExists) => Sc([a,b,c,d,e,f,g,h], allowExists);
    private static void Sc(string[] args, bool allowExists)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var p = Process.Start(info)!; string output = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0 && !(allowExists && (p.ExitCode is 1073 or 1056 or 1062 || args[0] == "stop" && p.ExitCode == 1061 || args[0] is "stop" or "delete" && p.ExitCode == 1060))) throw new PonteException("Não foi possível alterar o serviço: " + output.Trim());
    }

    internal static void Heartbeat(bool paused)
    {
        try { File.WriteAllText(RequestFile + ".tmp", JsonSerializer.Serialize(new OwnerRequest(Environment.TickCount64, paused)));
            File.Move(RequestFile + ".tmp", RequestFile, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private sealed record OwnerRequest(long Tick, bool Paused)
    {
        public long Boot { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64;
    }
    private static bool OwnerWantsHost()
    {
        try { var r = JsonSerializer.Deserialize<OwnerRequest>(File.ReadAllText(RequestFile));
            return r is not null && Math.Abs(r.Boot - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64)) < 60000 &&
                (r.Paused || Environment.TickCount64 >= r.Tick && Environment.TickCount64 - r.Tick < 4000 && !DesktopAccess.Locked); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
    internal static IDisposable? Lease()
    {
        try { return new FileStream(Path.Combine(Coordination, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task CoordinateAsync(HostService parent, AppConfig config, CancellationToken ct)
    {
        if (!Agent && !Elevation.IsElevated) throw new PonteException("Abra como administrador para gerenciar o host instalado como serviço.");
        HostService? host = null; IDisposable? lease = null;
        string stopMarker = Path.Combine(Coordination,"stop");
        DateTime lastStop = File.GetLastWriteTimeUtc(stopMarker);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool own = Agent ? !OwnerWantsHost() : !DesktopAccess.Locked;
                DateTime newStop = File.GetLastWriteTimeUtc(stopMarker);
                if (newStop != lastStop) { lastStop = newStop; host?.Current?.Stop(); }
                if (!Agent) Heartbeat(false);
                else if (host?.Current is not null)
                { File.WriteAllText(Path.Combine(Profile, "active"), "1"); File.WriteAllText(Path.Combine(Coordination,"active"), "1"); NotifyConsole(); }
                else
                { if (File.Exists(Path.Combine(Profile, "active"))) File.Delete(Path.Combine(Profile, "active")); if (File.Exists(Path.Combine(Coordination,"active"))) File.Delete(Path.Combine(Coordination,"active")); }
                if (!own && host is not null) { host.Dispose(); parent.SetManaged(null); host = null; lease?.Dispose(); lease = null; }
                if (own && host is null && (lease = Lease()) is not null)
                {
                    if (Agent) config = AppConfig.Load(Profile, machine: true);
                    else if (Elevation.IsElevated)
                    {
                        var machine = AppConfig.Load(Profile, machine: true);
                        config.Trusted = machine.Trusted;
                        config.BlockedResumes = machine.BlockedResumes;
                        config.Save();
                    }
                    host = new HostService(config, true) { ReceiveDir = parent.ReceiveDir, AskPermission = request => parent.AskPermission?.Invoke(request) ?? Task.FromResult(Permission.Refuse) };
                    parent.SetManaged(host); host.Start();
                }
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { host?.Dispose(); parent.SetManaged(null); lease?.Dispose(); if (Agent) { if (File.Exists(Path.Combine(Profile, "active"))) File.Delete(Path.Combine(Profile, "active")); if (File.Exists(Path.Combine(Coordination,"active"))) File.Delete(Path.Combine(Coordination,"active")); } }
    }

    public static int RunAgent()
    {
        if (!WindowsIdentity.GetCurrent().IsSystem) return 5;
        Agent = true;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var config = AppConfig.Load(Profile, machine: true);
        SessionLog.Folder = Profile;
        File.WriteAllText(Path.Combine(Profile, "agent-status.json"), JsonSerializer.Serialize(new { system = WindowsIdentity.GetCurrent().IsSystem, session = Process.GetCurrentProcess().SessionId }));
        using var host = new HostService(config);
        host.ReceiveDir = Path.Combine(Profile, "recebidos");
        host.Start();
        // No privileged windows on the interactive desktop. Unknown callers are refused, never auto-approved.
        try { host.CoordinationTask!.GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { ErrorLog.Write(ex); return 2; }
    }

    private static Process? _notice;
    private static void NotifyConsole()
    {
        if (DesktopAccess.Locked || _notice is { HasExited: false }) return;
        _notice?.Dispose(); _notice = null;
        uint session = WTSGetActiveConsoleSessionId();
        if (!WTSQueryUserToken(session, out var user)) return;
        try
        {
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = @"winsta0\default" };
            string exe = Path.Combine(Bin, "Ponte.exe");
            if (!CreateProcessAsUser(user, exe, new StringBuilder($"\"{exe}\" --aviso-servico {Environment.ProcessId}"), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, Bin, ref si, out var pi)) return;
            CloseHandle(pi.thread); CloseHandle(pi.process); _notice = Process.GetProcessById((int)pi.pid);
        }
        finally { CloseHandle(user); }
    }

    public static int RunNotice(int parentPid)
    {
        ApplicationConfiguration.Initialize();
        using var context = new ApplicationContext();
        using var tray = new NotifyIcon { Icon = Ui.MakeIcon(), Text = "Ponte: acesso remoto ativo pelo serviço", Visible = true };
        using var menu = new ContextMenuStrip();
        menu.Items.Add("Encerrar acesso remoto", null, async (_, _) =>
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "PonteHostStop", PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await pipe.ConnectAsync(deadline.Token); await pipe.WriteAsync("STOP"u8.ToArray(), deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { MessageBox.Show("Não foi possível interromper pelo serviço. Abra o Ponte como administrador.", "Ponte"); }
        });
        tray.ContextMenuStrip = menu;
        tray.ShowBalloonTip(10000, "Ponte: acesso remoto ativo", "Uma sessão autorizada está acessando este computador pelo serviço. Para interromper, use o menu deste ícone.", ToolTipIcon.Info);
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            try { using var parent = Process.GetProcessById(parentPid); }
            catch (ArgumentException) { context.ExitThread(); return; }
            if (!File.Exists(Path.Combine(Coordination,"active")) || DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(Coordination,"active")) > TimeSpan.FromSeconds(5)) context.ExitThread();
        };
        timer.Start(); Application.Run(context); tray.Visible = false; return 0;
    }

    private static readonly ManualResetEvent Stop = new(false);
    private static ServiceMain? _main; private static ServiceHandler? _handler; private static IntPtr _status, _job;
    public static int RunService()
    {
        if (!WindowsIdentity.GetCurrent().IsSystem) return 5;
        _job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobLimits { basic = new BasicLimits { flags = 0x2000 } };
        if (_job == IntPtr.Zero || !SetInformationJobObject(_job,9,ref limits,(uint)Marshal.SizeOf<JobLimits>())) return Marshal.GetLastWin32Error();
        _main = (_, _) =>
        {
            _handler = control => { if (control is 1 or 5) { Report(3); Stop.Set(); } };
            _status = RegisterServiceCtrlHandler(Name, _handler); Report(2);
            Report(4);
            using var pipeStop = new CancellationTokenSource();
            var pipeServer = Task.Run(async () =>
            {
                while (!pipeStop.IsCancellationRequested)
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null), PipeAccessRights.FullControl, AccessControlType.Deny));
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null), PipeAccessRights.FullControl, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    try
                    {
                        using var pipe = NamedPipeServerStreamAcl.Create("PonteHostStop", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64,64,security);
                        await pipe.WaitForConnectionAsync(pipeStop.Token);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(pipeStop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                        byte[] command = new byte[4]; await pipe.ReadExactlyAsync(command, deadline.Token);
                        if (command.AsSpan().SequenceEqual("STOP"u8)) { File.WriteAllText(Path.Combine(Coordination,"stop"),"1"); Heartbeat(true); SessionLog.Write("serviço: acesso interrompido pelo usuário local"); }
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { if (!pipeStop.IsCancellationRequested) await Task.Delay(500, pipeStop.Token); }
                }
            });
            Process? child = null; uint session = uint.MaxValue;
            try
            {
                while (!Stop.WaitOne(1000))
                {
                    uint current = WTSGetActiveConsoleSessionId();
                    if (child is not null && (child.HasExited || current != session))
                    { if (!child.HasExited) child.Kill(); child.WaitForExit(3000); child.Dispose(); child = null; }
                    if (child is null && current != uint.MaxValue)
                    {
                        try { child = Launch(current); session = current; }
                        catch (Win32Exception ex) { SessionLog.Write("serviço: falha ao abrir agente (" + ex.NativeErrorCode + ")"); }
                    }
                }
            }
            finally
            {
                pipeStop.Cancel(); try { pipeServer.Wait(4000); } catch (AggregateException) { }
                if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
                if (child is not null) { child.WaitForExit(3000); child.Dispose(); } Report(1);
            }
        };
        try { return StartServiceCtrlDispatcher([new ServiceEntry { name = Name, main = _main }, new ServiceEntry()]) ? 0 : Marshal.GetLastWin32Error(); }
        finally { if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; } }
    }
    private static void Report(uint state) { var s = new ServiceStatus { type = 0x10, state = state, accepted = state == 4 ? 5u : 0, wait = state is 2 or 3 ? 10000u : 0 }; SetServiceStatus(_status, ref s); }
    private static Process Launch(uint session)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0xF01FF, out var token)) throw new Win32Exception();
        IntPtr primary = IntPtr.Zero;
        try
        {
            var privileges = new TokenPrivileges { count = 1, attributes = 2 };
            if (!LookupPrivilegeValue(null, "SeTcbPrivilege", out privileges.luid) || !AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            if (!DuplicateTokenEx(token, 0xF01FF, IntPtr.Zero, 2, 1, out primary) || !SetTokenInformation(primary, 12, ref session, sizeof(uint))) throw new Win32Exception();
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), desktop = @"winsta0\default" };
            string exe = Path.Combine(Bin, "Ponte.exe");
            if (!CreateProcessAsUser(primary, exe, new StringBuilder($"\"{exe}\" --agente-servico"), IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, Bin, ref si, out var pi)) throw new Win32Exception();
            if (!AssignProcessToJobObject(_job,pi.process)) { int error=Marshal.GetLastWin32Error(); TerminateProcess(pi.process,1); CloseHandle(pi.thread); CloseHandle(pi.process); throw new Win32Exception(error); }
            CloseHandle(pi.thread); CloseHandle(pi.process); return Process.GetProcessById((int)pi.pid);
        }
        finally { if (primary != IntPtr.Zero) CloseHandle(primary); CloseHandle(token); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMain(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceHandler(uint control);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceEntry { [MarshalAs(UnmanagedType.LPWStr)] public string? name; public ServiceMain? main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint type,state,accepted,exit,specific,checkpoint,wait; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? reserved, desktop, title; public uint x,y,w,h,cx,cy,fill,flags; public ushort show, reservedSize; public IntPtr reservedPtr, input,output,error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process,thread; public uint pid,tid; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct TokenPrivileges { public uint count; public long luid; public uint attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long processTime,jobTime; public uint flags; public UIntPtr min,max; public uint active; public UIntPtr affinity; public uint priority,scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong read,write,other,readBytes,writeBytes,otherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits basic; public IoCounters io; public UIntPtr processMemory,jobMemory,peakProcess,peakJob; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job,int type,ref JobLimits limits,uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandler(string name, ServiceHandler handler);
    [DllImport("advapi32.dll")] private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr primary);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int type, ref uint data, int size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string directory, ref StartupInfo startup, out ProcessInfo process);
}

internal sealed class DesktopAccess : IDisposable
{
    private readonly IntPtr _previous, _desktop;
    private DesktopAccess(IntPtr previous, IntPtr desktop) { _previous = previous; _desktop = desktop; }
    public string Name { get { var name = new StringBuilder(128); GetUserObjectInformation(_desktop, 2, name, 256, out _); return name.ToString(); } }
    public static bool Locked
    {
        get { var d = OpenInputDesktop(0, false, 0x0001); if (d == IntPtr.Zero) return true;
            try { var name = new StringBuilder(128); return !GetUserObjectInformation(d, 2, name, 256, out _) || !name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase); }
            finally { CloseDesktop(d); } }
    }
    public static DesktopAccess? Enter()
    {
        if (!WindowsHost.Agent) return null;
        var old = GetThreadDesktop(GetCurrentThreadId()); var d = OpenInputDesktop(0, false, 0x01FF);
        if (d == IntPtr.Zero) throw new Win32Exception();
        if (!SetThreadDesktop(d)) { int error = Marshal.GetLastWin32Error(); CloseDesktop(d); throw new Win32Exception(error); }
        return new DesktopAccess(old, d);
    }
    public void Dispose() { SetThreadDesktop(_previous); CloseDesktop(_desktop); }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint size, out uint needed);
}
