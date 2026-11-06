using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Ponte;
internal static class LifecycleSelfTest
{
    public static async Task<int> RunAsync(string dir)
    {
        Directory.CreateDirectory(dir); SessionLog.Folder = dir;
        var lines = new List<string>();
        void Check(string text, bool ok) { lines.Add($"[{(ok ? "OK" : "FALHA")}] {text}"); File.WriteAllLines(Path.Combine(dir, "ciclo.txt"), lines); if (!ok) throw new IOException(text); }
        async Task Until(Func<bool> test, int seconds = 25)
        { long start = Stopwatch.GetTimestamp(); while (!test() && Stopwatch.GetElapsedTime(start).TotalSeconds < seconds) await Task.Delay(100); Check("espera: " + (test() ? "concluída" : "tempo excedido"), test()); }
        HostService? host = null; ViewerSession? viewer = null;
        try
        {
            var controller = new AutomaticQuality();
            var original = new VideoOptions(120, "h264", true, true, 32);
            Check("automático começa sem alterar as preferências manuais", controller.Apply(90, original) == (90, original));
            controller.Observe(30, 3);
            for (int i = 0; i < 8; i++) controller.Observe(600, 5);
            var degraded = controller.Apply(90, original);
            Check("congestionamento reduz qualidade, FPS, bitrate e resolução", controller.Level == 4 && degraded.Quality < 90 && degraded.Video.Fps < 120 && degraded.Video.BitrateMbps < 32 && degraded.Video.MaxHeight == 480);
            Check("automático preserva codec, chroma e preferência de latência", degraded.Video.Codec == original.Codec && degraded.Video.Chroma444 && degraded.Video.UltraLowLatency);
            for (int i = 0; i < 40; i++) controller.Observe(35, 2);
            Check("rede recuperada restaura os limites originais", controller.Apply(90, original) == (90, original));
            Check("redução preserva proporção e dimensões pares", new VideoOptions(MaxHeight:720).Size(1920,1080) == (1280,720));

            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var hc = AppConfig.Load(Path.Combine(dir, "host")); hc.RelayServer = ""; hc.ListenPort = port; hc.AutoUpdate = false; hc.SetPassword("Ponte-teste-ciclo-privado");
            var vc = AppConfig.Load(Path.Combine(dir, "viewer")); vc.RelayServer = ""; vc.AutoUpdate = false;
            string target = $"127.0.0.1:{port}"; vc.SaveComputer(target, "Host isolado", true);
            host = new HostService(hc); host.Start(IPAddress.Loopback);
            viewer = await ViewerSession.ConnectAsync(vc, target, () => Task.FromResult<string?>("Ponte-teste-ciclo-privado"), () => Task.FromResult(false), Path.Combine(dir,"recebidos"), CancellationToken.None);
            Check("autenticação e reconexão autorizadas com chave salva", viewer.Paired && viewer.CanReconnect && viewer.HostHasManagement);
            viewer.Start(); await viewer.SendSettingsAsync(0, 60, false, new VideoOptions(MaxHeight:480));
            await Until(() => { lock (viewer.CanvasLock) return viewer.Canvas is { Height: <=480 }; });
            lock (viewer.CanvasLock) Check("JPEG transmite resolução reduzida", viewer.Canvas is { Height: <=480 });
            await viewer.PingAsync(); await Until(() => viewer.RoundTripMilliseconds > 0);
            if (VideoRuntime.Present)
            {
                await viewer.SendSettingsAsync(0,60,false,new VideoOptions(30,"h264",BitrateMbps:8,MaxHeight:480));
                await Until(()=>viewer.VideoStatus.Contains("H.264 pela GPU"),35);
                long initial=viewer.Frames; await Until(()=>viewer.Frames>initial+3);
                lock(viewer.CanvasLock) Check("H.264 pela GPU mantém dimensões corretas na resolução automática",viewer.Canvas is { Height: <=480 });
                await viewer.SendSettingsAsync(0,60,false,new VideoOptions(MaxHeight:480));
            }
            int restarts = 0; RemotePower.TestRestart = () => Interlocked.Increment(ref restarts);
            bool refused = false; viewer.Notice += text => { if (text.Contains("desativou")) refused = true; };
            hc.AllowRemoteRestart = false; await viewer.RequestRestartAsync(); await Until(() => refused);
            Check("reinício remoto desativado é recusado no host", restarts == 0);
            hc.AllowRemoteRestart = true; await viewer.RequestRestartAsync(); await Until(() => restarts == 1);
            Check("reinício autenticado chega à ação simulada, sem reiniciar este PC", restarts == 1);
            int connections = 0; viewer.Reconnected += () => Interlocked.Increment(ref connections);
            host.Dispose(); await Task.Delay(500); host = new HostService(hc); host.Start(IPAddress.Loopback);
            await Until(() => connections > 0, 35); long frames = viewer.Frames; await Until(() => viewer.Frames > frames);
            Check("queda retoma na mesma sessão com vídeo e preferências", viewer.CanReconnect && !viewer.Reconnecting);
            bool closed = false; viewer.Closed += _ => closed = true;
            host.Current!.Stop(); await Until(() => closed);
            int before = connections; await Task.Delay(2500);
            Check("encerrar no host não provoca reconexão automática", connections == before);
            Check("encerramento explícito bloqueia retomadas pendentes",hc.ResumeBlocked(Security.KeyHash(vc.Certificate.PublicKey.ExportSubjectPublicKeyInfo())));
            await viewer.DisposeAsync(); viewer = null;
            await Until(() => host.Current is null);
            viewer = await ViewerSession.ConnectAsync(vc, target, () => Task.FromResult<string?>("Ponte-teste-ciclo-privado"), () => Task.FromResult(false), Path.Combine(dir,"recebidos"), CancellationToken.None);
            Check("nova conexão manual libera a retomada dessa nova sessão",!hc.ResumeBlocked(Security.KeyHash(vc.Certificate.PublicKey.ExportSubjectPublicKeyInfo())));
            viewer.Start(); closed = false; viewer.Closed += _ => closed = true;
            // This session connected by a trusted key, with no cached password. Revoking the key must stop retries.
            hc.Trusted.Clear(); hc.SetPassword(null); host.Dispose(); await Task.Delay(500);
            host = new HostService(hc); host.AskPermission = _ => throw new IOException("Reconexão não pode pedir aceite de novo."); host.Start(IPAddress.Loopback);
            await Until(() => closed, 25);
            Check("revogação da autorização interrompe retomada sem pedir aceite", closed && host.Current is null);
            return 0;
        }
        catch (Exception ex) { lines.Add("[FALHA] " + ex); File.WriteAllLines(Path.Combine(dir, "ciclo.txt"), lines); return 1; }
        finally { if (viewer is not null) await viewer.DisposeAsync(); host?.Dispose(); RemotePower.TestRestart = null; }
    }
}
