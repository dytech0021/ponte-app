using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ponte;

/// <summary>
/// O aplicativo do Ponte para Android, oferecido por este programa: baixa o APK do servidor de
/// retransmissão, confere que é o publicado e que foi assinado pela chave do Ponte, e o entrega ao
/// celular por um link na rede local ou salva em arquivo.
/// </summary>
internal static class AndroidApp
{
    /// <summary>
    /// SHA-256 do certificado que assina o APK (chaves/android.jks). Se a chave do aplicativo mudar, este
    /// valor muda junto; sem isso, o APK baixado é recusado.
    /// </summary>
    public const string SignerSha256 = "07cc51a24374eb5a472044848c7e22015170683338191fecd3d8959164d08a15";

    private const long MaxSize = 64L * 1024 * 1024;

    public sealed record Package(byte[] Apk, string Version);

    private sealed record Manifest(int VersionCode, string VersionName, long Size, string Sha256);

    public static async Task<Package> DownloadAsync(AppConfig config, CancellationToken ct)
    {
        if (config.RelayServer.Length == 0) throw new PonteException("Sem servidor de retransmissão configurado, não há de onde baixar o aplicativo.");
        byte[] raw = await Transport.FetchAsync(config, "ANDROID_MANIFEST", 8192, ct)
                     ?? throw new PonteException("O servidor ainda não tem o aplicativo para Android.");
        Manifest? manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { manifest = null; }
        if (manifest is not { Size: > 0 and <= MaxSize, Sha256.Length: 64, VersionName: not null })
            throw new PonteException("O servidor descreveu o aplicativo de um jeito inesperado.");

        byte[] apk = await Transport.FetchAsync(config, "ANDROID_DOWNLOAD", (int)manifest.Size, ct)
                     ?? throw new PonteException("O servidor não entregou o aplicativo.");
        if (apk.Length != manifest.Size || !Convert.ToHexString(SHA256.HashData(apk)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PonteException("O aplicativo baixado não confere com o que o servidor anunciou e foi descartado.");
        // O instalador do Android confere se a assinatura bate com o conteúdo; aqui se confere de quem ela é.
        if (SignerDigest(apk) != SignerSha256)
            throw new PonteException("O aplicativo baixado não foi assinado pela chave do Ponte e foi descartado.");
        return new Package(apk, manifest.VersionName);
    }

    /// <summary>
    /// SHA-256 do certificado de quem assinou o APK, lido do bloco de assinatura (esquemas v2 e v3) que
    /// fica logo antes do diretório central do ZIP. Null se o arquivo não tiver esse bloco.
    /// </summary>
    public static string? SignerDigest(byte[] apk)
    {
        try
        {
            // Fim do diretório central (assinatura 0x06054b50), procurado de trás para a frente.
            int eocd = -1;
            for (int i = apk.Length - 22; i >= Math.Max(0, apk.Length - 22 - 65535); i--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) return null;
            long directory = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(eocd + 16));
            if (directory < 32 || directory > eocd || Encoding.ASCII.GetString(apk, (int)directory - 16, 16) != "APK Sig Block 42") return null;
            long blockSize = BinaryPrimitives.ReadInt64LittleEndian(apk.AsSpan((int)directory - 24));
            long blockStart = directory - blockSize - 8;
            if (blockSize < 24 || blockStart < 0) return null;

            // Pares (tamanho de 8 bytes, identificador de 4 bytes, valor) entre os dois campos de tamanho.
            int pos = (int)blockStart + 8, end = (int)directory - 24;
            (int Start, int Length) v2 = default, v3 = default;
            while (pos + 12 <= end)
            {
                long length = BinaryPrimitives.ReadInt64LittleEndian(apk.AsSpan(pos));
                if (length < 4 || pos + 8 + length > end) return null;
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(pos + 8));
                if (id == 0x7109871a) v2 = (pos + 12, (int)length - 4);
                else if (id == 0xf05368c0) v3 = (pos + 12, (int)length - 4);
                pos += 8 + (int)length;
            }
            var scheme = v2.Length > 0 ? v2 : v3;
            if (scheme.Length == 0) return null;

            // Signatários → primeiro signatário → dados assinados → resumos, certificados → primeiro certificado.
            var signers = Prefixed(apk, scheme);
            var signer = Prefixed(apk, signers);
            var signedData = Prefixed(apk, signer);
            var digests = Prefixed(apk, signedData);
            var certificates = Prefixed(apk, (digests.Start + digests.Length, signedData.Start + signedData.Length - digests.Start - digests.Length));
            var certificate = Prefixed(apk, certificates);
            return Convert.ToHexString(SHA256.HashData(apk.AsSpan(certificate.Start, certificate.Length))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    // O primeiro item de uma sequência em que cada item tem um tamanho de 4 bytes na frente.
    private static (int Start, int Length) Prefixed(byte[] data, (int Start, int Length) area)
    {
        if (area.Length < 4) throw new InvalidDataException("bloco de assinatura truncado");
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(area.Start)));
        if (length < 0 || 4 + length > area.Length) throw new InvalidDataException("bloco de assinatura truncado");
        return (area.Start + 4, length);
    }

    /// <summary>
    /// Endereços IPv4 deste computador na rede local, dos adaptadores que têm saída (gateway) primeiro.
    /// Adaptadores de máquinas virtuais ficam de fora: o celular não os alcança.
    /// </summary>
    public static List<IPAddress> LocalAddresses()
    {
        string[] virtualNames = { "virtual", "vethernet", "vmware", "hyper-v", "loopback", "bluetooth", "tap-", "wsl" };
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        !virtualNames.Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .Select(n => (Props: n.GetIPProperties(), Wireless: n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            .OrderByDescending(n => n.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .ThenByDescending(n => n.Wireless)
            .SelectMany(n => n.Props.UnicastAddresses.Select(a => a.Address))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// Servidor HTTP mínimo na rede local, que existe só enquanto a janela do aplicativo para celular está
/// aberta. Responde a dois endereços: a página com o botão de baixar e o próprio APK.
/// </summary>
internal sealed class ApkServer : IDisposable
{
    public const int PreferredPort = 7413;

    private readonly TcpListener _listener;
    private readonly byte[] _apk;
    private readonly byte[] _page;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public ApkServer(AndroidApp.Package package)
    {
        _apk = package.Apk;
        _page = Encoding.UTF8.GetBytes(Page(package.Version, package.Apk.Length));
        _listener = Listen(PreferredPort) ?? Listen(0) ?? throw new PonteException("Não foi possível abrir uma porta para o celular baixar o aplicativo.");
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private static TcpListener? Listen(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var stream = client.GetStream();

                // Só o cabeçalho do pedido interessa: lê até a linha em branco, no máximo 8 KB.
                var header = new List<byte>(512);
                var one = new byte[1];
                while (header.Count < 8192 && !(header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n'))
                {
                    if (await stream.ReadAsync(one, timeout.Token) == 0) return;
                    header.Add(one[0]);
                }
                string[] request = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0].Split(' ');
                string method = request.Length > 1 ? request[0] : "", path = request.Length > 1 ? request[1].Split('?')[0] : "";

                if (method is not ("GET" or "HEAD")) await RespondAsync(stream, "405 Method Not Allowed", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Só leitura."), false, null, timeout.Token);
                else if (path == "/") await RespondAsync(stream, "200 OK", "text/html; charset=utf-8", _page, method == "HEAD", null, timeout.Token);
                else if (path.Equals("/Ponte.apk", StringComparison.OrdinalIgnoreCase))
                    await RespondAsync(stream, "200 OK", "application/vnd.android.package-archive", _apk, method == "HEAD", "attachment; filename=\"Ponte.apk\"", timeout.Token);
                else await RespondAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Não encontrado."), method == "HEAD", null, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string type, byte[] body, bool headOnly, string? disposition, CancellationToken ct)
    {
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status}\r\n")
            .Append($"Content-Type: {type}\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        if (disposition is not null) head.Append($"Content-Disposition: {disposition}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (!headOnly) await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Page(string version, int size) => $$"""
        <!doctype html>
        <html lang="pt-BR">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Ponte para Android</title>
        <style>
          body { margin: 0; font-family: system-ui, sans-serif; background: #f3f5f6; color: #1b1f22; }
          main { max-width: 480px; margin: 0 auto; padding: 32px 20px; }
          h1 { color: #006978; font-size: 30px; margin: 0 0 4px; }
          .versao { color: #5f6b72; margin: 0 0 28px; }
          a.botao { display: block; text-align: center; background: #006978; color: #fff; text-decoration: none;
                    font-size: 18px; padding: 16px; border-radius: 12px; }
          ol { line-height: 1.5; padding-left: 20px; margin-top: 28px; }
        </style>
        </head>
        <body>
        <main>
          <h1>Ponte</h1>
          <p class="versao">Aplicativo para Android, versão {{version}} ({{size / 1024}} KB)</p>
          <a class="botao" href="/Ponte.apk">Baixar o aplicativo</a>
          <ol>
            <li>Toque em <b>Baixar o aplicativo</b> e, ao terminar, abra o arquivo.</li>
            <li>Se o Android perguntar, permita instalar aplicativos desta origem.</li>
            <li>Se aparecer um aviso do Play Protect, escolha instalar assim mesmo.</li>
          </ol>
          <p>Depois de instalado, o aplicativo se atualiza sozinho.</p>
        </main>
        </body>
        </html>
        """;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
