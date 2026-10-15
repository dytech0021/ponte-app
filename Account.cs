using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ponte;

/// <summary>
/// Conta opcional, a mesma do FilaBox (login pelo Supabase dele, com Google ou e-mail): guarda a lista de
/// computadores salvos de cada pessoa, para ela aparecer em qualquer aparelho em que a pessoa entrar. Cada conta
/// só enxerga a própria lista (regra do banco). Nada aqui dá acesso a computador nenhum: a permissão de entrar
/// continua em cada computador acessado.
/// </summary>
internal sealed class Account
{
    /// <summary>O Supabase do FilaBox. Só o autoteste troca, por um servidor falso nesta máquina.</summary>
    public static string Url { get; internal set; } = "https://kobtvgqnitdeviddvymm.supabase.co";
    /// <summary>Chave pública do Supabase do FilaBox: a mesma que o site entrega a qualquer navegador.</summary>
    public const string PublicKey = "sb_publishable_S0LPicuJdLg-XsrxGiN0Ug_tIDZ4sLo";
    public const string SiteUrl = "https://filabox3d.com.br";
    /// <summary>Porta deste computador para onde o login com Google volta (autorizada no Supabase).</summary>
    public const int GooglePort = 53682;
    private const string Table = "ponte_computadores";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _access = "", _refresh;
    private DateTime _expires = DateTime.MinValue;

    public string Email { get; }
    public string UserId { get; }
    /// <summary>O token de renovação mudou (o Supabase troca a cada renovação) e precisa ser gravado de novo.</summary>
    public event Action<Account>? TokenChanged;
    public string RefreshToken => _refresh;

    private Account(string email, string userId, string refresh)
    {
        Email = email;
        UserId = userId;
        _refresh = refresh;
    }

    // ---------- entrar ----------

    public static async Task<Account> SignInAsync(string email, string password, CancellationToken ct)
    {
        var session = await TokenAsync("password", new { email = email.Trim(), password }, ct);
        return FromSession(session);
    }

    /// <summary>
    /// Login com Google: abre o navegador na página do Google (pelo Supabase do FilaBox) e espera o retorno numa
    /// porta deste computador. Usa PKCE: o código que volta só vale com o segredo que ficou aqui.
    /// </summary>
    public static async Task<Account> SignInWithGoogleAsync(CancellationToken ct)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string redirect = $"http://127.0.0.1:{GooglePort}/ponte";
        var listener = new TcpListener(IPAddress.Loopback, GooglePort);
        try { listener.Start(); }
        catch (SocketException) { throw new PonteException($"A porta {GooglePort} deste computador está ocupada; feche o outro programa e tente de novo."); }
        try
        {
            OpenBrowser($"{Url}/auth/v1/authorize?provider=google&redirect_to={Uri.EscapeDataString(redirect)}" +
                        $"&code_challenge={challenge}&code_challenge_method=s256");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new PonteException("O login com Google não voltou para o Ponte a tempo. Tente de novo.");
                }
                using (client)
                {
                    var (code, error) = await ReadCallbackAsync(client, timeout.Token);
                    if (code is null && error is null) continue; // outro pedido qualquer (ícone do site, por exemplo)
                    if (error is not null) throw new PonteException("O Google não concluiu o login: " + error);
                    var session = await TokenAsync("pkce", new { auth_code = code, code_verifier = verifier }, ct);
                    return FromSession(session);
                }
            }
        }
        finally { listener.Stop(); }
    }

    /// <summary>Volta a conta gravada neste computador (sem pedir login de novo), ou null.</summary>
    public static Account? Restore(AppConfig config)
    {
        if (config.AccountToken.Length == 0 || config.AccountUserId.Length == 0) return null;
        try
        {
            string refresh = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(config.AccountToken), null, DataProtectionScope.CurrentUser));
            return new Account(config.AccountEmail, config.AccountUserId, refresh);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return null; }
    }

    /// <summary>Grava a conta no perfil, com o token de renovação protegido pelo Windows (só este usuário abre).</summary>
    public void Store(AppConfig config)
    {
        config.AccountEmail = Email;
        config.AccountUserId = UserId;
        config.AccountToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(_refresh), null, DataProtectionScope.CurrentUser));
    }

    public async Task SignOutAsync()
    {
        try
        {
            using var request = await AuthorizedAsync(HttpMethod.Post, "/auth/v1/logout?scope=local", CancellationToken.None);
            using var _ = await Http.SendAsync(request);
        }
        catch (Exception ex) when (ex is PonteException or HttpRequestException or TaskCanceledException) { }
    }

    // ---------- lista de computadores ----------

    internal sealed record Computer(
        [property: JsonPropertyName("chave")] string Key,
        [property: JsonPropertyName("endereco")] string Address,
        [property: JsonPropertyName("nome")] string Name,
        [property: JsonPropertyName("auto_acesso")] bool AutoAccess);

    public async Task<List<Computer>> ListAsync(CancellationToken ct)
    {
        using var request = await AuthorizedAsync(HttpMethod.Get, $"/rest/v1/{Table}?select=chave,endereco,nome,auto_acesso", ct);
        using var response = await SendAsync(request, ct);
        return JsonSerializer.Deserialize<List<Computer>>(await response.Content.ReadAsStringAsync(ct)) ?? new();
    }

    public async Task UpsertAsync(IEnumerable<Computer> computers, CancellationToken ct)
    {
        var rows = computers.Select(c => new
        {
            user_id = UserId, chave = c.Key, endereco = c.Address, nome = c.Name, auto_acesso = c.AutoAccess,
            atualizado_em = DateTime.UtcNow,
        }).ToList();
        if (rows.Count == 0) return;
        using var request = await AuthorizedAsync(HttpMethod.Post, $"/rest/v1/{Table}?on_conflict=user_id,chave", ct);
        request.Headers.Add("Prefer", "resolution=merge-duplicates,return=minimal");
        request.Content = new StringContent(JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json");
        using var _ = await SendAsync(request, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(HttpMethod.Delete, $"/rest/v1/{Table}?chave=eq.{Uri.EscapeDataString(key)}", ct);
        using var _ = await SendAsync(request, ct);
    }

    // ---------- HTTP ----------

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string path, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow >= _expires)
            {
                var session = await TokenAsync("refresh_token", new { refresh_token = _refresh }, ct);
                _access = session.AccessToken;
                _refresh = session.RefreshToken;
                _expires = DateTime.UtcNow.AddSeconds(Math.Max(60, session.ExpiresIn - 60));
                TokenChanged?.Invoke(this);
            }
        }
        finally { _refreshGate.Release(); }
        var request = new HttpRequestMessage(method, Url + path);
        request.Headers.Add("apikey", PublicKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access);
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new PonteException("Sem conexão com o servidor da conta.");
        }
        if (response.IsSuccessStatusCode) return response;
        string body = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        throw new PonteException(response.StatusCode == HttpStatusCode.NotFound
            ? "A conta ainda não está pronta no servidor (falta a tabela do Ponte)."
            : $"O servidor da conta recusou o pedido ({(int)response.StatusCode}). {Short(body)}");
    }

    private sealed record Session(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("user")] SessionUser? User);

    private sealed record SessionUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("email")] string? Email);

    private static async Task<Session> TokenAsync(string grant, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/auth/v1/token?grant_type={grant}");
        request.Headers.Add("apikey", PublicKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new PonteException("Sem conexão com o servidor da conta.");
        }
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new PonteException(AuthError(text));
            return JsonSerializer.Deserialize<Session>(text) is { AccessToken.Length: > 0, RefreshToken.Length: > 0 } session
                ? session : throw new PonteException("O servidor da conta respondeu de um jeito inesperado.");
        }
    }

    private static Account FromSession(Session session)
    {
        if (session.User is not { Id.Length: > 0 } user) throw new PonteException("O servidor da conta não informou quem entrou.");
        var account = new Account(user.Email ?? "", user.Id, session.RefreshToken)
        {
            _access = session.AccessToken,
            _expires = DateTime.UtcNow.AddSeconds(Math.Max(60, session.ExpiresIn - 60)),
        };
        return account;
    }

    // As mensagens do Supabase vêm em inglês; as comuns viram português.
    private static string AuthError(string body)
    {
        string code = "", message = "";
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            code = root.TryGetProperty("error_code", out var c) ? c.GetString() ?? "" : root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            message = root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : root.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
        }
        catch (JsonException) { }
        return code switch
        {
            "invalid_credentials" => "E-mail ou senha incorretos.",
            "invalid_grant" when message.Contains("credentials", StringComparison.OrdinalIgnoreCase) => "E-mail ou senha incorretos.",
            "email_not_confirmed" => "Confirme o e-mail da conta (o link chegou na sua caixa de entrada) e tente de novo.",
            "refresh_token_not_found" or "refresh_token_already_used" or "session_not_found" => "A conta saiu deste computador. Entre de novo.",
            "validation_failed" when message.Contains("refresh token", StringComparison.OrdinalIgnoreCase) => "A conta saiu deste computador. Entre de novo.",
            "over_request_rate_limit" => "Muitas tentativas seguidas. Espere um pouco e tente de novo.",
            _ => message.Length > 0 ? "A conta recusou: " + Short(message) : "A conta recusou o login.",
        };
    }

    private static string Short(string text) => text.Length > 200 ? text[..200] : text;

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Como administrador, o navegador abriria como administrador também; pelo Explorer ele abre como o usuário.
    private static void OpenBrowser(string url)
    {
        var start = Elevation.IsElevated
            ? new ProcessStartInfo("explorer.exe") { ArgumentList = { url } }
            : new ProcessStartInfo(url) { UseShellExecute = true };
        Process.Start(start)?.Dispose();
    }

    public static void OpenSite(string path) => OpenBrowser(SiteUrl + path);

    // Lê "GET /ponte?code=...&..." e responde uma página curta para a pessoa voltar ao Ponte.
    private static async Task<(string? Code, string? Error)> ReadCallbackAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        int read = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (n == 0) break;
                read += n;
                if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n")) break;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return (null, null); }
        string firstLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
        string[] parts = firstLine.Split(' ');
        if (parts.Length < 2 || !parts[1].StartsWith("/ponte")) { await RespondAsync(stream, 404, ""); return (null, null); }
        var query = parts[1].Contains('?') ? parts[1][(parts[1].IndexOf('?') + 1)..] : "";
        var values = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
        string? code = values.GetValueOrDefault("code");
        string? error = values.GetValueOrDefault("error_description") ?? values.GetValueOrDefault("error");
        await RespondAsync(stream, 200, code is not null
            ? "<h2>Pronto!</h2><p>Você entrou na conta. Pode fechar esta aba e voltar ao Ponte.</p>"
            : "<h2>O login não foi concluído.</h2><p>Volte ao Ponte e tente de novo.</p>");
        return (code, code is null ? error ?? "sem código de retorno" : null);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string html)
    {
        string page = $"<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>Ponte</title></head>" +
                      $"<body style=\"font-family:Segoe UI,sans-serif;margin:3em;color:#222\">{html}</body></html>";
        byte[] body = Encoding.UTF8.GetBytes(page);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }
        catch (IOException) { }
    }
}
