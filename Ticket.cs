using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ponte;

/// <summary>
/// Bilhete da conta (1.17.0): o Ponte só funciona com uma conta liberada pelo dono. O servidor de retransmissão confere
/// a conta no Supabase do FilaBox e assina "a chave deste aparelho é da conta X, liberada até tal dia". Cada lado de uma
/// sessão manda o seu bilhete e confere o do outro: o computador acessado no Hello de quem acessa, quem acessa no
/// Challenge do computador acessado. Não depende do servidor estar no caminho: vale também na conexão direta por IP.
/// </summary>
internal static class Ticket
{
    /// <summary>Chave pública (SPKI, EC P-256) do servidor que assina os bilhetes. A privada fica só no servidor.</summary>
    public const string IssuerKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEnhEA2rsYc5UCSKpkORCQ/yTmqKPa33hievj1yunmgcJrtAmWmK++WYiFnXCndxKdaFvr/YMlLEX7wxOou/A/FA==";

    /// <summary>Quanto antes de vencer o bilhete é renovado (ele vale 14 dias).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(7);

    private static readonly ECDsa Issuer = LoadIssuer();

    /// <summary>Só o autoteste: uma chave de teste que também vale, para as sessões de teste não dependerem de uma conta real.</summary>
    internal static ECDsa? TestIssuer;

    private static ECDsa LoadIssuer()
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(IssuerKey), out _);
        return key;
    }

    internal sealed record Body(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("e")] string Email,
        [property: JsonPropertyName("k")] string Key,
        [property: JsonPropertyName("x")] long Expires);

    /// <summary>O SHA-256 (hex maiúsculo) da chave pública deste aparelho: é a ela que o bilhete fica preso.</summary>
    public static string KeyHashOf(AppConfig config) => Security.KeyHash(config.Certificate.PublicKey.ExportSubjectPublicKeyInfo());

    /// <summary>Lê o bilhete sem conferir a assinatura (para mostrar o e-mail e a validade); null se estiver estragado.</summary>
    public static Body? Read(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 2048) return null;
        int dot = ticket.IndexOf('.');
        if (dot <= 0) return null;
        try { return JsonSerializer.Deserialize<Body>(FromBase64Url(ticket[..dot])); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    public static DateTime ExpiresUtc(string? ticket) =>
        Read(ticket) is { } body ? DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(body.Expires, 0, 253402300799)).UtcDateTime : DateTime.MinValue;

    /// <summary>
    /// Confere o bilhete de um aparelho cuja chave tem o resumo <paramref name="keyHash"/>. Devolve o e-mail da conta,
    /// ou null com o motivo em <paramref name="reason"/>.
    /// </summary>
    public static string? Verify(string? ticket, string keyHash, out string reason)
    {
        reason = "sem bilhete da conta";
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 2048) return null;
        int dot = ticket.IndexOf('.');
        if (dot <= 0 || dot == ticket.Length - 1) { reason = "bilhete da conta estragado"; return null; }
        byte[] payload, signature;
        try
        {
            payload = FromBase64Url(ticket[..dot]);
            signature = FromBase64Url(ticket[(dot + 1)..]);
        }
        catch (FormatException) { reason = "bilhete da conta estragado"; return null; }
        bool signed = signature.Length == 64 && (Issuer.VerifyData(payload, signature, HashAlgorithmName.SHA256) ||
                                                 TestIssuer?.VerifyData(payload, signature, HashAlgorithmName.SHA256) == true);
        if (!signed) { reason = "bilhete da conta com assinatura inválida"; return null; }
        Body? body;
        try { body = JsonSerializer.Deserialize<Body>(payload); }
        catch (JsonException) { body = null; }
        if (body is not { Version: 1, Email.Length: > 0 }) { reason = "bilhete da conta estragado"; return null; }
        if (!string.Equals(body.Key, keyHash, StringComparison.OrdinalIgnoreCase)) { reason = "bilhete da conta de outro aparelho"; return null; }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= body.Expires) { reason = "bilhete da conta vencido"; return null; }
        reason = "";
        return body.Email;
    }

    /// <summary>O bilhete gravado ainda vale para este aparelho por mais de <paramref name="margin"/>.</summary>
    public static bool Valid(AppConfig config, TimeSpan margin = default) =>
        Verify(config.Ticket, KeyHashOf(config), out _) is not null && ExpiresUtc(config.Ticket) - DateTime.UtcNow > margin;

    /// <summary>A conta não está na lista de liberadas do dono do Ponte.</summary>
    internal sealed class NotReleasedException(string email) : Exception(
        $"A conta {email} ainda não foi liberada para usar o Ponte. Peça ao dono do Ponte para liberar este e-mail.")
    {
        public string Email { get; } = email;
    }

    /// <summary>Pede um bilhete novo ao servidor de retransmissão, com o token de acesso da conta.</summary>
    public static async Task<string> RequestAsync(AppConfig config, string accessToken, CancellationToken ct)
    {
        var (host, port) = Transport.ParseEndpoint(config.RelayServer, Transport.DefaultRelayPort);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        string reply;
        try
        {
            using var tcp = await Transport.ConnectTcpAsync(host, port, timeout.Token);
            var raw = tcp.GetStream();
            await Transport.WriteLineAsync(raw, Transport.RelayLine(config, "TICKET", KeyHashOf(config).ToLowerInvariant()), timeout.Token);
            await Transport.WriteLineAsync(raw, accessToken, timeout.Token);
            reply = await Transport.ReadLineAsync(raw, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new PonteException("Sem conexão com o servidor do Ponte para conferir a conta.");
        }
        if (reply.StartsWith("TICKET ", StringComparison.Ordinal) && Verify(reply[7..], KeyHashOf(config), out _) is not null) return reply[7..];
        if (reply.StartsWith("ERR naoliberado", StringComparison.Ordinal)) throw new NotReleasedException(reply.Length > 16 ? reply[16..] : "desta conta");
        throw new PonteException(reply switch
        {
            "ERR conta" => "A conta saiu deste computador. Entre de novo.",
            "ERR servidor" => "O servidor do Ponte não conseguiu conferir a conta agora. Tente de novo em instantes.",
            "ERR key" => Transport.RelayError(reply),
            _ when reply.StartsWith("TICKET ", StringComparison.Ordinal) => "O servidor do Ponte mandou um bilhete inválido.",
            _ => "O servidor do Ponte ainda não confere contas (falta atualizar o servidor).",
        });
    }

    /// <summary>Só o autoteste: liga a chave de teste e dá ao perfil um bilhete dela, válido por um dia.</summary>
    internal static void GiveTestTicket(AppConfig config, string email = "autoteste@ponte.local")
    {
        TestIssuer ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
        config.Ticket = SignForTest(email, KeyHashOf(config), DateTime.UtcNow.AddDays(1));
    }

    /// <summary>Só o autoteste: um bilhete assinado pela chave de teste.</summary>
    internal static string SignForTest(string email, string keyHash, DateTime expiresUtc)
    {
        if (TestIssuer is null) throw new InvalidOperationException("Sem chave de teste.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Body(1, email, keyHash.ToUpperInvariant(), new DateTimeOffset(expiresUtc).ToUnixTimeSeconds()));
        return ToBase64Url(payload) + "." + ToBase64Url(TestIssuer.SignData(payload, HashAlgorithmName.SHA256));
    }

    public static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
    {
        string s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}
