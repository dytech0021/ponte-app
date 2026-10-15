using System.Net.Sockets;

namespace Ponte;

/// <summary>
/// Quais computadores salvos estão com o Ponte aberto agora, para a bolinha verde ou vermelha da lista. Os salvos
/// por ID perguntam ao servidor de retransmissão (é onde eles ficam registrados enquanto o Ponte está aberto, com
/// um sinal a cada 25 s); os salvos por IP, testando a porta direta.
/// </summary>
internal static class Presence
{
    /// <summary>Por chave ("id:..." ou "ip:..."): true online, false offline. O que não deu para saber fica de fora.</summary>
    public static async Task<Dictionary<string, bool>> CheckAsync(AppConfig config, IReadOnlyList<SavedEntry> saved, CancellationToken ct)
    {
        var result = new Dictionary<string, bool>();
        var ids = saved.Where(s => s.Key.StartsWith("id:")).Select(s => s.Key[3..]).Distinct().ToList();
        var relay = ids.Count > 0 && config.RelayServer.Length > 0 ? AskRelayAsync(config, ids, ct) : Task.FromResult<Dictionary<string, bool>?>(null);
        var probes = saved.Where(s => s.Key.StartsWith("ip:")).Select(async s => (s.Key, Online: await ProbeAsync(s.Address, ct))).ToList();
        if (await relay is { } online)
            foreach (var (id, on) in online) result["id:" + id] = on;
        foreach (var (key, on) in await Task.WhenAll(probes)) result[key] = on;
        return result;
    }

    // Servidor fora do ar, ou ainda sem o STATUS: devolve null e a lista fica sem bolinha, em vez de tudo vermelho.
    private static async Task<Dictionary<string, bool>?> AskRelayAsync(AppConfig config, List<string> ids, CancellationToken ct)
    {
        var (host, port) = Transport.ParseEndpoint(config.RelayServer, Transport.DefaultRelayPort);
        var online = new Dictionary<string, bool>();
        try
        {
            foreach (string[] chunk in ids.Chunk(100))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var tcp = await Transport.ConnectTcpAsync(host, port, timeout.Token);
                var raw = tcp.GetStream();
                await Transport.WriteLineAsync(raw, Transport.RelayLine(config, "STATUS", string.Join(',', chunk)), timeout.Token);
                string reply = await Transport.ReadLineAsync(raw, timeout.Token);
                if (!reply.StartsWith("STATUS ") || reply.Length != 7 + chunk.Length) return null;
                for (int i = 0; i < chunk.Length; i++) online[chunk[i]] = reply[7 + i] == '1';
            }
            return online;
        }
        catch (Exception ex) when (ex is PonteException or IOException or SocketException or InvalidDataException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    // Por IP: o Ponte aberto lá aceita conexão na porta direta. Só abre e fecha, sem identificação nenhuma.
    private static async Task<bool> ProbeAsync(string address, CancellationToken ct)
    {
        var (host, port) = Transport.ParseEndpoint(address, Transport.DefaultDirectPort);
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }
}
