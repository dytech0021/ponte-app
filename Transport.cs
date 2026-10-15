using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ponte;

/// <summary>
/// Como dois computadores se encontram. Direto: o visualizador abre TCP no IP do outro.
/// Por ID: os dois abrem conexões de saída para um servidor de retransmissão, que só repassa bytes.
/// Nos dois casos o TLS é de ponta a ponta, então o servidor não enxerga o conteúdo.
/// </summary>
internal static class Transport
{
    public const int DefaultDirectPort = 7411;
    public const int DefaultRelayPort = 7412;
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    public static bool IsId(string text, out string id)
    {
        id = new string(text.Where(char.IsDigit).ToArray());
        return id.Length == 9 && text.All(c => char.IsDigit(c) || c == ' ' || c == '-');
    }

    public static (string host, int port) ParseEndpoint(string text, int defaultPort)
    {
        text = text.Trim();
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int port) && port is > 0 and < 65536)
            return (text[..colon], port);
        return (text, defaultPort);
    }

    public static async Task<TcpClient> ConnectTcpAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, timeout.Token);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            return tcp;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            tcp.Dispose();
            if (ct.IsCancellationRequested) throw;
            throw new PonteException($"Não foi possível alcançar {host}:{port}.");
        }
    }

    // Lê byte a byte de propósito: nada além da linha pode ser consumido, porque o que vem depois já é TLS.
    public static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (bytes.Count < 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new PonteException("O servidor de retransmissão fechou a conexão.");
            if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("Linha longa demais.");
    }

    public static Task WriteLineAsync(Stream stream, string line, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), ct).AsTask();

    /// <summary>Primeira linha enviada ao servidor de retransmissão, com a chave de acesso quando houver.</summary>
    public static string RelayLine(AppConfig config, string command, string argument)
    {
        string key = new(config.RelayKey.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return key.Length > 0 ? $"PONTE {command} {argument} {key}" : $"PONTE {command} {argument}";
    }

    public static string RelayError(string reply) => reply switch
    {
        "ERR key" => "O servidor de retransmissão recusou a chave de acesso. Confira a chave nas configurações.",
        "ERR offline" => "Esse computador não está online.",
        "ERR timeout" => "O outro computador não respondeu a tempo.",
        "ERR busy" => "O servidor de retransmissão está ocupado. Tente de novo.",
        _ => $"O servidor de retransmissão recusou a conexão ({reply}).",
    };

    /// <summary>
    /// Pede um arquivo ao servidor de retransmissão (manifesto ou executável de atualização).
    /// Devolve null quando o servidor não tem o arquivo. O conteúdo não é confiável por si: quem chama confere a assinatura.
    /// </summary>
    public static async Task<byte[]?> FetchAsync(AppConfig config, string command, int maxBytes, CancellationToken ct)
    {
        var (host, port) = ParseEndpoint(config.RelayServer, DefaultRelayPort);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var tcp = await ConnectTcpAsync(host, port, timeout.Token);
            var raw = tcp.GetStream();
            await WriteLineAsync(raw, RelayLine(config, command, "0"), timeout.Token);
            string reply = await ReadLineAsync(raw, timeout.Token);
            if (reply == "ERR none") return null;
            if (!reply.StartsWith("OK ") || !int.TryParse(reply.AsSpan(3), out int length) || length <= 0 || length > maxBytes)
                throw new PonteException(reply.StartsWith("ERR ") ? RelayError(reply) : "O servidor respondeu de forma inesperada ao pedido de atualização.");

            var data = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await raw.ReadAsync(data.AsMemory(read), timeout.Token);
                if (n == 0) throw new PonteException("A conexão caiu durante o download da atualização.");
                read += n;
            }
            return data;
        }
        catch (Exception ex) when (ex is IOException or SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new PonteException("Não foi possível falar com o servidor de atualização.");
        }
    }

    /// <summary>
    /// Baixa do servidor de retransmissão um arquivo grande direto para <paramref name="output"/>. O prazo é de
    /// silêncio, e não do download inteiro: numa conexão lenta ele pode levar minutos. Quem chama confere o conteúdo.
    /// </summary>
    public static async Task DownloadAsync(AppConfig config, string command, string argument, long maxBytes, Stream output,
        Action<long, long>? progress, CancellationToken ct)
    {
        if (config.RelayServer.Length == 0) throw new PonteException("Configure o servidor de retransmissão para baixar o arquivo.");
        var (host, port) = ParseEndpoint(config.RelayServer, DefaultRelayPort);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var tcp = await ConnectTcpAsync(host, port, idle.Token);
            var raw = tcp.GetStream();
            await WriteLineAsync(raw, RelayLine(config, command, argument), idle.Token);
            string reply = await ReadLineAsync(raw, idle.Token);
            if (reply == "ERR none") throw new PonteException("O servidor ainda não tem esse arquivo.");
            if (!reply.StartsWith("OK ") || !long.TryParse(reply.AsSpan(3), out long length) || length <= 0 || length > maxBytes)
                throw new PonteException(reply.StartsWith("ERR ") ? RelayError(reply) : "O servidor respondeu de forma inesperada ao pedido do arquivo.");

            var buffer = new byte[128 * 1024];
            for (long read = 0; read < length;)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                int n = await raw.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - read)), idle.Token);
                if (n == 0) throw new PonteException("A conexão caiu durante o download.");
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                progress?.Invoke(read, length);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new PonteException("Não foi possível baixar o arquivo do servidor.");
        }
    }

    /// <summary>Lado do visualizador: conecta, faz o TLS e devolve o certificado que o outro lado apresentou.</summary>
    public static async Task<(SslStream stream, X509Certificate2 remote)> ConnectAsync(AppConfig config, string target, CancellationToken ct)
    {
        bool byId = IsId(target, out string id);
        TcpClient tcp;
        if (byId)
        {
            if (config.RelayServer.Length == 0)
                throw new PonteException("Para conectar por ID é preciso configurar o servidor de retransmissão. Na mesma rede, use o endereço IP.");
            var (host, port) = ParseEndpoint(config.RelayServer, DefaultRelayPort);
            tcp = await ConnectTcpAsync(host, port, ct);
            try
            {
                var raw = tcp.GetStream();
                await WriteLineAsync(raw, RelayLine(config, "CONNECT", id), ct);
                string reply = await ReadLineAsync(raw, ct);
                if (reply != "OK") throw new PonteException(RelayError(reply));
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
        else
        {
            var (host, port) = ParseEndpoint(target, DefaultDirectPort);
            tcp = await ConnectTcpAsync(host, port, ct);
        }

        X509Certificate2? remote = null;
        // A cadeia não é validada por autoridade: a identidade é conferida logo abaixo (ID e impressão digital).
        var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert is not null) remote = new X509Certificate2(cert);
            return cert is not null;
        });
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "ponte", EnabledSslProtocols = Protocols }, ct);
            if (remote is null) throw new PonteException("O outro computador não apresentou identidade.");
            if (byId && Security.IdFromCertificate(remote) != id)
                throw new PonteException("A identidade do computador remoto não confere com o ID digitado. A conexão foi recusada.");
            return (ssl, remote);
        }
        catch (Exception ex)
        {
            await ssl.DisposeAsync();
            if (ex is AuthenticationException or IOException) throw new PonteException("Falha ao estabelecer a conexão segura.");
            throw;
        }
    }

    /// <summary>Lado do computador controlado: TLS como servidor, com a chave própria.</summary>
    /// <remarks>
    /// O Ponte do Windows e o do Android começam pelo TLS. O Ponte no navegador, que não faz TLS com o
    /// certificado próprio do computador, começa por "WPONTE1" e segue pelo canal de <see cref="WebSecureStream"/>.
    /// O primeiro byte decide: o TLS sempre começa por 0x16.
    /// </remarks>
    public static async Task<Stream> AcceptAsync(TcpClient tcp, X509Certificate2 certificate, CancellationToken ct)
    {
        tcp.NoDelay = true;
        var network = tcp.GetStream();
        var first = new byte[1];
        if (await network.ReadAsync(first, ct) == 0) throw new IOException("O outro lado fechou a conexão.");
        if (first[0] == WebSecureStream.Magic[0]) return await WebSecureStream.AcceptAsync(new PrefixedStream(network, first), certificate, ct);

        var ssl = new SslStream(new PrefixedStream(network, first), false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, ClientCertificateRequired = false, EnabledSslProtocols = Protocols,
            }, ct);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync();
            throw;
        }
    }
}
