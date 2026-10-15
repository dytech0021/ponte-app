using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ponte;

/// <summary>
/// A conta opcional contra um Supabase falso nesta máquina: login, junção das listas nos dois sentidos, apagar
/// sem internet, renovação do token e sair. Nada vai para o servidor de verdade.
/// </summary>
internal static partial class SelfTest
{
    private static async Task CheckAccountAsync(string dir, Checker check)
    {
        var fake = new FakeAccountServer();
        string real = Account.Url;
        Account.Url = fake.Url;
        try
        {
            string profile = Path.Combine(dir, "perfil-conta");
            if (Directory.Exists(profile)) Directory.Delete(profile, true);
            var config = AppConfig.Load(profile);
            var ct = CancellationToken.None;
            string casa = AppConfig.Target("192.168.0.10").Key, escritorio = AppConfig.Target("111 222 333").Key;

            string wrong = "";
            try { await Account.SignInAsync("pessoa@exemplo.com", "errada", ct); }
            catch (PonteException ex) { wrong = ex.Message; }
            check("conta: senha errada vira mensagem em português", wrong == "E-mail ou senha incorretos.", wrong);

            config.SaveComputer("192.168.0.10", "Casa", true);
            fake.Rows[escritorio] = FakeAccountServer.Row(escritorio, "111 222 333", "Escritório", false);
            var account = await Account.SignInAsync(" pessoa@exemplo.com ", "certa", ct);
            account.Store(config);
            var restored = Account.Restore(config);
            check("conta: entra e guarda o token protegido pelo Windows",
                account.Email == "pessoa@exemplo.com" && restored?.UserId == "u1" && !config.AccountToken.Contains("renova"));

            var first = await config.SyncAccountAsync(account, ct);
            check("conta: o da conta chega aqui e o daqui vai para a conta",
                first == (1, 0) && config.Saved.Count == 2 && config.Saved.All(s => s.Synced && !s.Dirty) &&
                fake.Rows.TryGetValue(casa, out var sent) && sent["nome"]?.GetValue<string>() == "Casa" && sent["auto_acesso"]?.GetValue<bool>() == true,
                $"{first}, {config.Saved.Count} aqui, {fake.Rows.Count} na conta");

            config.SaveComputer("192.168.0.10", "Casa nova", false);
            fake.Rows[escritorio] = FakeAccountServer.Row(escritorio, "111 222 333", "Escritório 2", true);
            var second = await config.SyncAccountAsync(account, ct);
            check("conta: mudança dos dois lados ao mesmo tempo, cada lado leva a sua",
                second == (0, 0) && fake.Rows[casa]["nome"]?.GetValue<string>() == "Casa nova" &&
                config.FindSaved(escritorio) is { Name: "Escritório 2", AutoAccess: true });

            config.RemoveSaved(escritorio);
            Account.Url = "http://127.0.0.1:1";
            string offline = "";
            try { await config.SyncAccountAsync(account, ct); }
            catch (PonteException ex) { offline = ex.Message; }
            Account.Url = fake.Url;
            bool waited = fake.Rows.ContainsKey(escritorio);
            fake.Rows.TryRemove(casa, out _);
            var third = await config.SyncAccountAsync(account, ct);
            check("conta: apagado sem internet sai da conta na próxima vez; apagado em outro aparelho sai daqui",
                offline == "Sem conexão com o servidor da conta." && waited && third == (0, 1) && fake.Rows.IsEmpty && config.Saved.Count == 0,
                $"{offline}; {third}");

            // Outra execução do Ponte: o token gravado é renovado na primeira chamada e o novo é gravado.
            string before = config.AccountToken;
            var again = Account.Restore(config)!;
            again.TokenChanged += a => a.Store(config);
            config.SaveComputer("192.168.0.20", "Só daqui", false);
            await config.SyncAccountAsync(again, ct);
            config.SaveComputer("192.168.0.30", "Salvo depois", false);
            check("conta: renova o token e grava o novo", config.AccountToken != before && fake.Session == 2);

            config.SignOutAccount();
            check("conta: sair leva embora só o que era da conta",
                config.AccountToken.Length == 0 && Account.Restore(config) is null && config.Saved.Select(s => s.Name).SequenceEqual(new[] { "Salvo depois" }));

            string gone = "";
            try { await Account.Restore(BuildWith(profile, "pessoa@exemplo.com", "u1", "renova1"))!.ListAsync(ct); }
            catch (PonteException ex) { gone = ex.Message; }
            check("conta: sessão encerrada em outro lugar avisa que a conta saiu", gone.StartsWith("A conta saiu"), gone);
        }
        finally
        {
            Account.Url = real;
            fake.Dispose();
        }
    }

    // Um perfil com um token velho gravado, como se fosse de antes de a senha ser trocada.
    private static AppConfig BuildWith(string profile, string email, string userId, string refresh)
    {
        var config = AppConfig.Load(profile + "-velho");
        config.AccountEmail = email;
        config.AccountUserId = userId;
        config.AccountToken = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            Encoding.UTF8.GetBytes(refresh), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        return config;
    }

    /// <summary>O pedaço do Supabase que o Ponte usa: login, renovação, e a tabela de uma conta só ("u1").</summary>
    private sealed class FakeAccountServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject> Rows = new();
        public int Session = 1;
        public string Url { get; }

        public FakeAccountServer()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(LoopAsync);
        }

        public static JsonObject Row(string key, string address, string name, bool auto) =>
            new() { ["chave"] = key, ["endereco"] = address, ["nome"] = name, ["auto_acesso"] = auto };

        public void Dispose() => _listener.Stop();

        private async Task LoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
                using (client)
                {
                    try { await ServeAsync(client.GetStream()); }
                    catch (Exception ex) when (ex is IOException or SocketException or JsonException) { }
                }
            }
        }

        private async Task ServeAsync(NetworkStream stream)
        {
            // Cabeçalho até a linha em branco, byte a byte; o corpo pelo Content-Length (em bytes, não em letras).
            var head = new List<byte>();
            var one = new byte[1];
            while (head.Count < 65536 && !(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await stream.ReadAsync(one) == 0) return;
                head.Add(one[0]);
            }
            string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return;
            var headers = lines.Skip(1).Where(l => l.Contains(':'))
                .ToDictionary(l => l[..l.IndexOf(':')], l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
            var body = new byte[int.Parse(headers.GetValueOrDefault("Content-Length", "0"))];
            await stream.ReadExactlyAsync(body);
            string method = first[0], path = first[1].Split('?')[0], query = first[1].Contains('?') ? first[1][(first[1].IndexOf('?') + 1)..] : "";
            var (status, answer) = Handle(method, path, query, headers, Encoding.UTF8.GetString(body));
            byte[] bytes = Encoding.UTF8.GetBytes(answer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(bytes);
        }

        private (int, string) Handle(string method, string path, string query, Dictionary<string, string> headers, string body)
        {
            lock (Rows)
            {
                if (headers.GetValueOrDefault("apikey") != Account.PublicKey) return (401, """{"message":"sem apikey"}""");
                if (path == "/auth/v1/token") return Token(query, JsonNode.Parse(body)!.AsObject());
                if (headers.GetValueOrDefault("Authorization") != $"Bearer acesso{Session}") return (401, """{"message":"JWT expired"}""");
                if (path == "/auth/v1/logout") return (204, "");
                if (path != "/rest/v1/ponte_computadores") return (404, "{}");
                switch (method)
                {
                    case "GET":
                        return (200, new JsonArray(Rows.Values.Select(r => r.DeepClone()).ToArray()).ToJsonString());
                    case "POST" when query.Contains("on_conflict=user_id,chave"):
                        foreach (var row in JsonNode.Parse(body)!.AsArray().Select(r => r!.AsObject()))
                        {
                            if (row["user_id"]?.GetValue<string>() != "u1") continue;
                            Rows[row["chave"]!.GetValue<string>()] = Row(row["chave"]!.GetValue<string>(), row["endereco"]!.GetValue<string>(),
                                row["nome"]!.GetValue<string>(), row["auto_acesso"]!.GetValue<bool>());
                        }
                        return (201, "");
                    case "DELETE":
                        Rows.TryRemove(Uri.UnescapeDataString(query[(query.IndexOf("chave=eq.") + 9)..]), out _);
                        return (204, "");
                    default:
                        return (405, "{}");
                }
            }
        }

        private (int, string) Token(string query, JsonObject body)
        {
            bool ok = query switch
            {
                "grant_type=password" => body["email"]?.GetValue<string>() == "pessoa@exemplo.com" && body["password"]?.GetValue<string>() == "certa",
                "grant_type=refresh_token" when body["refresh_token"]?.GetValue<string>() == $"renova{Session}" => ++Session > 0,
                _ => false,
            };
            if (!ok)
                return (400, query == "grant_type=password"
                    ? """{"error_code":"invalid_credentials","msg":"Invalid login credentials"}"""
                    : """{"error_code":"refresh_token_not_found","msg":"Invalid Refresh Token: Refresh Token Not Found"}""");
            return (200, JsonSerializer.Serialize(new
            {
                access_token = $"acesso{Session}", refresh_token = $"renova{Session}", expires_in = 3600,
                user = new { id = "u1", email = "pessoa@exemplo.com" },
            }));
        }
    }
}
