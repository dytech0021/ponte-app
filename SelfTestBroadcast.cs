using System.Net;
using System.Text;

namespace Ponte;

/// <summary>
/// A transmissão de tela contra o servidor de verdade: o Ponte de teste pede o link, transmite 720p com som, e o
/// vídeo tem de aparecer no servidor de mídia (pela lista do HLS, a mesma que a página usa de reserva). Parada
/// a transmissão, o link deixa de valer. Não mexe no mouse nem no teclado.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Só esta parte: --autoteste pasta --relay servidor:porta --so-transmissao.</summary>
    private static async Task CheckBroadcastOnlyAsync(string dir, string? relay, Checker check)
    {
        if (relay is null)
        {
            check("transmissão de tela", true, "sem --relay: pulada", skipped: true);
            return;
        }
        var config = AppConfig.Load(Path.Combine(dir, "perfil-transmissao"));
        config.RelayServer = relay;
        config.ListenPort = FreePort();
        using var host = new HostService(config);
        host.Start(IPAddress.Loopback);
        for (int i = 0; i < 100 && !host.RelayOnline; i++) await Task.Delay(100);
        check("transmissão: registro no servidor", host.RelayOnline, host.RelayStatus);
        if (host.RelayOnline) await CheckBroadcastAsync(host, config, check);
    }

    private static async Task CheckBroadcastAsync(HostService host, AppConfig config, Checker check)
    {
        check("transmissão: tamanho da imagem (reduz até a altura pedida, nunca amplia, medidas pares)",
            Broadcast.OutputSize(2560, 1440, 1080) == (1920, 1080) && Broadcast.OutputSize(1920, 1080, 1080) == (1920, 1080) &&
            Broadcast.OutputSize(1366, 768, 720) == (1280, 720) && Broadcast.OutputSize(1280, 720, 1080) == (1280, 720) &&
            Broadcast.OutputSize(1601, 901, 1080) == (1600, 900));
        if (Broadcast.WebHost(config) is not { } server)
        {
            check("transmissão de tela", true, "servidor sem nome: pulada", skipped: true);
            return;
        }

        Broadcast broadcast;
        try
        {
            broadcast = await Broadcast.StartAsync(host, config, new BroadcastOptions(720, 30, true), _ => { }, CancellationToken.None);
        }
        catch (PonteException ex)
        {
            check("transmissão: começa e recebe o link", false, ex.Message);
            return;
        }
        string token = broadcast.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            for (int i = 0; i < 100 && !broadcast.Live; i++) await Task.Delay(200);
            check("transmissão: começa e recebe o link", broadcast.Live && broadcast.Link == $"https://{server}/ponte/assistir/#{token}",
                broadcast.Live ? $"{VideoEncoder.Vendor(broadcast.Encoder)}, link com código de {token.Length} letras" : broadcast.Status);

            // O MediaMTX monta o HLS na primeira vez que alguém pede: alguns segundos até a lista ficar pronta.
            string playlist = "", detail = "";
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    using var response = await http.GetAsync($"https://{server}/ponte/ao-vivo-hls/ao/{token}/index.m3u8");
                    playlist = await response.Content.ReadAsStringAsync();
                    detail = $"HTTP {(int)response.StatusCode}";
                    if (response.IsSuccessStatusCode && playlist.Contains("#EXTM3U")) break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { detail = ex.Message; }
                await Task.Delay(500);
            }
            string codecs = playlist.Split('\n').FirstOrDefault(l => l.Contains("CODECS=")) ?? "";
            check("transmissão: o vídeo chega ao servidor e sai pelo link", playlist.Contains("#EXTM3U") && codecs.Contains("avc1"), $"{detail} {codecs.Trim()}");
            check("transmissão: vai com o som (Opus)", codecs.Contains("opus", StringComparison.OrdinalIgnoreCase), codecs.Trim());
        }
        finally
        {
            await broadcast.DisposeAsync();
        }

        // Parada a transmissão, o código sai do servidor: quem abrir o link é recusado.
        await Task.Delay(500);
        int after = 0;
        try
        {
            using var response = await http.PostAsync($"https://{server}/ponte/ao-vivo/ao/{token}/whep",
                new StringContent("v=0", Encoding.UTF8, "application/sdp"));
            after = (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        check("transmissão: parada, o link deixa de valer", after == 401, $"HTTP {after}");
    }
}
