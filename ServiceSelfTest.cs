using System.Diagnostics;

namespace Ponte;
internal static class ServiceSelfTest
{
    public static async Task<int> RunAsync(string dir)
    {
        Directory.CreateDirectory(dir); SessionLog.Folder = dir;
        var lines = new List<string>(); AppConfig? config = null; int result = 1;
        void Check(string name, bool ok) { lines.Add($"[{(ok ? "OK" : "FALHA")}] {name}"); File.WriteAllLines(Path.Combine(dir,"servico.txt"),lines); if(!ok) throw new IOException(name); }
        async Task Until(Func<bool> test, int seconds=30) { var start=Stopwatch.GetTimestamp(); while(!test() && Stopwatch.GetElapsedTime(start).TotalSeconds<seconds) await Task.Delay(200); if(!test()) throw new TimeoutException("Esperando o serviço do Windows."); }
        try
        {
            config=AppConfig.Load(Path.Combine(dir,"host")); config.AutoUpdate=false; config.ListenPort=17419;
            Check("teste administrativo", Elevation.IsElevated);
            if (WindowsHost.Owns(config)) WindowsHost.Remove(config);
            Check("serviço de produção ausente", !WindowsHost.Installed);
            string password=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)); config.SetPassword(password);
            var vc=AppConfig.Load(Path.Combine(dir,"viewer")); vc.AutoUpdate=false; vc.AutoReconnect=false; vc.SaveComputer(config.Id,"Agente de teste",true);
            WindowsHost.Install(config);
            Check("serviço instalado e identidade preservada", WindowsHost.Owns(config) && AppConfig.Load(WindowsHost.Profile,true).Id==config.Id);
            // Let the explicit owner request expire. The service now hosts instead of a regular user app.
            WindowsHost.Heartbeat(false);
            await Task.Delay(5500);
            await Until(()=>File.Exists(Path.Combine(WindowsHost.Profile,"agent-status.json")),30);
            var proof=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(WindowsHost.Profile,"agent-status.json")));
            Check("agente real SYSTEM na sessão do console", proof.RootElement.GetProperty("system").GetBoolean() && proof.RootElement.GetProperty("session").GetInt32()>0);
            ViewerSession? v=null;
            for(int i=0;i<15 && v is null;i++)
            { try { using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6)); v=await ViewerSession.ConnectAsync(vc,config.Id,()=>Task.FromResult<string?>(password),()=>Task.FromResult(false),Path.Combine(dir,"recebidos"),deadline.Token); }
                catch(Exception ex) when(ex is PonteException or IOException or OperationCanceledException){await Task.Delay(1000);} }
            Check("cliente autenticado entra no agente pelo servidor",v is not null);
            await using(var viewer=v!)
            {
                viewer.Start(); await viewer.SendSettingsAsync(0,50,false,new VideoOptions(MaxHeight:480));
                await Until(()=>viewer.Frames>0);
                Check("captura real recebida do agente SYSTEM sem mover mouse nem teclado",viewer.Frames>0);
                await Until(()=>WindowsHost.ActiveSession);
                Check("sessão do serviço impede atualização automática durante uso",WindowsHost.ActiveSession);
                using var conflicting=WindowsHost.Lease(); Check("lease impede dois hosts com o mesmo ID",conflicting is null);
            }
            await Until(()=>!WindowsHost.ActiveSession);
            WindowsHost.RefreshBinary(config, force:true);
            Check("troca do executável do serviço mantém backup para rollback",Directory.EnumerateFiles(WindowsHost.Bin,"Ponte.exe.antigo-*").Any());
            using(var foreground=new HostService(config))
            {
                foreground.Start(); await Until(()=>foreground.RelayOnline,30);
                await using var viewer=await ViewerSession.ConnectAsync(vc,config.Id,()=>throw new IOException("A autorização salva foi perdida."),()=>Task.FromResult(false),Path.Combine(dir,"recebidos"),CancellationToken.None);
                Check("passagem para aplicativo preserva autorização criada no serviço",viewer.Paired);
            }
            await Task.Delay(5500);
            vc.AutoReconnect = true;
            await using (var viewer = await ViewerSession.ConnectAsync(vc,config.Id,()=>throw new IOException("A chave deveria estar autorizada no serviço."),()=>Task.FromResult(false),Path.Combine(dir,"recebidos"),CancellationToken.None))
            {
                string? ended = null; viewer.Closed += reason => ended = reason; viewer.Start();
                await Until(()=>WindowsHost.ActiveSession);
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".","PonteHostStop",System.IO.Pipes.PipeDirection.Out,System.IO.Pipes.PipeOptions.Asynchronous);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await pipe.ConnectAsync(deadline.Token); await pipe.WriteAsync("STOP"u8.ToArray(),deadline.Token);
                await Until(()=>ended is not null && !WindowsHost.ActiveSession);
                Check("interrupção local via pipe encerra acesso e impede reconexão",ended!.Contains("encerrada") && !viewer.Reconnecting);
            }
            await Task.Delay(1000); result = 0;
        }
        catch(Exception ex){lines.Add("[FALHA] "+ex); File.WriteAllLines(Path.Combine(dir,"servico.txt"),lines);}
        finally
        {
            if(config is not null && WindowsHost.Owns(config))
            {
                try{WindowsHost.Remove(config);lines.Add("[OK] serviço de teste removido");}
                catch(Exception ex){result = 1; lines.Add("[FALHA] remoção do serviço de teste: "+ex.Message);}
                File.WriteAllLines(Path.Combine(dir,"servico.txt"),lines);
            }
        }
        return result;
    }
}
