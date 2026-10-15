using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Ponte;

internal static class Security
{
    public const int DefaultIterations = 200_000;
    public const int MaxIterations = 2_000_000;

    /// <summary>Par de chaves do computador. A parte pública define o ID; a privada nunca sai da máquina.</summary>
    public static byte[] CreateCertificatePfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Ponte", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        return cert.Export(X509ContentType.Pkcs12);
    }

    // O Windows só usa em TLS uma chave que esteja num contêiner de chaves; UserKeySet cria um
    // temporário no perfil do usuário, apagado quando o certificado é descartado.
    public static X509Certificate2 LoadPfx(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);

    /// <summary>ID de 9 dígitos derivado da chave pública: quem não tem a chave privada não consegue se passar por ele.</summary>
    public static string IdFromSpki(byte[] spki)
    {
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(spki));
        return (100_000_000 + value % 900_000_000).ToString();
    }

    public static string IdFromCertificate(X509Certificate2 cert) => IdFromSpki(cert.PublicKey.ExportSubjectPublicKeyInfo());

    public static byte[] CertificateHash(X509Certificate2 cert) => SHA256.HashData(cert.RawData);

    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(CertificateHash(cert));

    public static string FormatId(string id) => id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

    public static byte[] DeriveVerifier(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>
    /// Prova de que o visualizador conhece a senha. Entra na conta o certificado que cada lado viu:
    /// um intermediário teria de apresentar outro certificado, e a prova deixaria de conferir.
    /// </summary>
    public static byte[] Proof(byte[] verifier, byte[] nonce, byte[] certificateHash)
    {
        var data = new byte[nonce.Length + certificateHash.Length];
        nonce.CopyTo(data, 0);
        certificateHash.CopyTo(data, nonce.Length);
        return HMACSHA256.HashData(verifier, data);
    }

    // ---------- computadores autorizados ----------

    private static byte[] PairingMessage(byte[] nonce, byte[] certificateHash) =>
        [.. "ponte-autorizacao\n"u8, .. nonce, .. certificateHash];

    /// <summary>
    /// Assinatura com que o visualizador prova ser o dono da própria chave. Como na prova de senha, entra o
    /// certificado que ele viu do outro lado, então a assinatura não serve para um intermediário repassar.
    /// </summary>
    public static string SignPairing(X509Certificate2 own, byte[] nonce, byte[] certificateHash)
    {
        using var key = own.GetECDsaPrivateKey() ?? throw new PonteException("Chave do computador indisponível.");
        return Convert.ToBase64String(key.SignData(PairingMessage(nonce, certificateHash), HashAlgorithmName.SHA256));
    }

    public static bool VerifyPairing(byte[] spki, byte[] nonce, byte[] certificateHash, string? signature)
    {
        if (signature is null || signature.Length > 256) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out int read);
            return read == spki.Length && key.KeySize == 256 &&
                   key.VerifyData(PairingMessage(nonce, certificateHash), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Uma chave autorizada é reconhecida pelo resumo inteiro dela; os 9 dígitos do ID são curtos demais para isso.</summary>
    public static string KeyHash(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki));
}

internal sealed class RecentEntry
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime When { get; set; }
}

/// <summary>Computador guardado na lista de quem acessa.</summary>
internal sealed class SavedEntry
{
    public string Key { get; set; } = "";
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Pedir ao outro computador para entrar sem aprovação nas próximas vezes.</summary>
    public bool AutoAccess { get; set; } = true;
    /// <summary>Na última conexão o outro computador confirmou que este está autorizado.</summary>
    public bool Paired { get; set; }
    public DateTime LastUsed { get; set; }
    /// <summary>Está guardado na conta (quando há conta). Paired e LastUsed são deste aparelho e não vão para a conta.</summary>
    public bool Synced { get; set; }
    /// <summary>Mudou aqui e ainda não foi para a conta.</summary>
    public bool Dirty { get; set; }
}

/// <summary>Computador que entra neste sem senha e sem aceite na tela.</summary>
internal sealed class TrustedViewer
{
    public string KeyHash { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public DateTime Added { get; set; }
    public DateTime LastUsed { get; set; }
    /// <summary>Autorizado por quem entrou com a senha, e não por alguém na frente deste computador.</summary>
    public bool ViaPassword { get; set; }
}

/// <summary>Configuração do usuário, em %APPDATA%\Ponte\config.json (ou na pasta dada por --perfil).</summary>
internal sealed class AppConfig
{
    public string CertPfx { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string PasswordVerifier { get; set; } = "";
    public int Iterations { get; set; } = Security.DefaultIterations;
    public string RelayServer { get; set; } = RelayDefaults.Server;
    public string RelayKey { get; set; } = RelayDefaults.Key;
    public int ListenPort { get; set; } = 7411;
    public bool DirectEnabled { get; set; } = true;
    /// <summary>Abrir como administrador quando a conta permitir, para conseguir controlar programas elevados.</summary>
    public bool RunElevated { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Abrir o Ponte quando o usuário entra no Windows, para o computador ficar acessível depois de reiniciar.</summary>
    public bool StartWithWindows { get; set; } = true;
    /// <summary>"auto" acompanha o tema do Windows; "claro" e "escuro" fixam.</summary>
    public string Theme { get; set; } = "auto";
    /// <summary>Quem acessa este computador pode ouvir o que ele está tocando.</summary>
    public bool ShareSound { get; set; } = true;
    /// <summary>Ao acessar outro computador, tocar aqui o som de lá.</summary>
    public bool PlaySound { get; set; } = true;
    /// <summary>Preferências de transmissão ao acessar outro computador; o padrão mantém o modo compatível atual.</summary>
    /// <summary>Perfil de transmissão em uso ao acessar outro computador; a próxima conexão começa nele.</summary>
    public string Profile { get; set; } = StreamProfile.Default;
    /// <summary>O que a pessoa mudou em cada perfil; um perfil que não está aqui vale o original.</summary>
    public Dictionary<string, StreamProfile> Profiles { get; set; } = new();
    /// <summary>Como foi a última transmissão de tela (qualidade e som), para a próxima começar igual.</summary>
    public BroadcastOptions Broadcast { get; set; } = new();

    public StreamProfile GetProfile(string key) =>
        Profiles.TryGetValue(key, out var profile) && profile is not null ? profile.Normalize() : StreamProfile.Original(key);
    public List<RecentEntry> Recent { get; set; } = new();
    public List<SavedEntry> Saved { get; set; } = new();
    /// <summary>Conta do FilaBox em que este Ponte entrou (opcional); vazio quando ninguém entrou.</summary>
    public string AccountEmail { get; set; } = "";
    public string AccountUserId { get; set; } = "";
    /// <summary>Token de renovação da conta, protegido pelo Windows (DPAPI): só este usuário, neste computador, abre.</summary>
    public string AccountToken { get; set; } = "";
    /// <summary>Computadores apagados aqui que ainda precisam sair da conta (o apagar pode ter sido sem internet).</summary>
    public List<string> AccountPendingDeletes { get; set; } = new();
    public List<TrustedViewer> Trusted { get; set; } = new();
    public Dictionary<string, string> KnownHosts { get; set; } = new();

    // Campos (não propriedades) ficam fora do JSON.
    public string Dir = "";
    public X509Certificate2 Certificate = null!;
    public string Id = "";

    public const int MaxTrusted = 50;

    // A janela e o atendimento de conexões mexem nas listas em threads diferentes; tudo que altera ou grava passa por aqui.
    private readonly Lock _gate = new();

    public bool HasPassword => PasswordVerifier.Length > 0;

    private string FilePath => Path.Combine(Dir, "config.json");

    private static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ponte");

    /// <summary>Só o tema gravado, sem carregar (nem criar) o resto da configuração: é lido antes de qualquer janela.</summary>
    public static string SavedTheme(string? dir)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir ?? DefaultDir, "config.json")));
            return json.RootElement.TryGetProperty(nameof(Theme), out var theme) ? theme.GetString() ?? "auto" : "auto";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return "auto";
        }
    }

    public static AppConfig Load(string? dir)
    {
        dir ??= DefaultDir;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "config.json");
        AppConfig? config = null;
        if (File.Exists(path))
        {
            try { config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)); }
            catch (JsonException) { File.Copy(path, path + ".invalido", true); }
        }
        config ??= new AppConfig();
        config.Profiles ??= new();
        config.Broadcast ??= new();
        config.AccountPendingDeletes ??= new();
        if (!StreamProfile.All.Any(p => p.Key == config.Profile)) config.Profile = StreamProfile.Default;
        config.Dir = dir;

        byte[]? pfx = null;
        if (config.CertPfx.Length > 0)
        {
            try { pfx = ProtectedData.Unprotect(Convert.FromBase64String(config.CertPfx), null, DataProtectionScope.CurrentUser); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { }
        }
        if (pfx is null)
        {
            // Sem chave legível (primeira execução ou perfil copiado de outra conta): nasce um ID novo.
            // Se havia uma chave que esta conta não consegue abrir, a configuração antiga fica guardada.
            if (config.CertPfx.Length > 0 && File.Exists(path)) File.Copy(path, path + ".outra-conta", true);
            pfx = Security.CreateCertificatePfx();
            config.CertPfx = Convert.ToBase64String(ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser));
            config.Certificate = Security.LoadPfx(pfx);
            config.Id = Security.IdFromCertificate(config.Certificate);
            config.Save();
        }
        else
        {
            config.Certificate = Security.LoadPfx(pfx);
            config.Id = Security.IdFromCertificate(config.Certificate);
        }
        return config;
    }

    public void Save()
    {
        lock (_gate)
        {
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
    }

    /// <summary>Guarda só um derivado da senha (PBKDF2 com sal); senha vazia ou nula desliga o acesso por senha.</summary>
    public void SetPassword(string? password)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(password))
            {
                PasswordSalt = PasswordVerifier = "";
            }
            else
            {
                byte[] salt = RandomNumberGenerator.GetBytes(16);
                Iterations = Security.DefaultIterations;
                PasswordSalt = Convert.ToBase64String(salt);
                PasswordVerifier = Convert.ToBase64String(Security.DeriveVerifier(password, salt, Iterations));
            }
            Save();
        }
    }

    // ---------- lado de quem acessa ----------

    /// <summary>Como um destino digitado é guardado na configuração e como aparece na tela.</summary>
    public static (string Key, string Shown) Target(string target)
    {
        target = target.Trim();
        return Transport.IsId(target, out string id) ? ("id:" + id, Security.FormatId(id)) : ("ip:" + target.ToLowerInvariant(), target);
    }

    public SavedEntry? FindSaved(string key)
    {
        lock (_gate) return Saved.Find(s => s.Key == key);
    }

    public void SaveComputer(string target, string name, bool autoAccess)
    {
        var (key, shown) = Target(target);
        lock (_gate)
        {
            var entry = Saved.Find(s => s.Key == key);
            if (entry is null) Saved.Add(entry = new SavedEntry { Key = key, Address = shown });
            entry.Name = name;
            entry.AutoAccess = autoAccess;
            entry.Dirty = true;
            AccountPendingDeletes.Remove(key);
            Save();
        }
    }

    public void RemoveSaved(string key)
    {
        lock (_gate)
        {
            if (Saved.Exists(s => s.Key == key && s.Synced) && !AccountPendingDeletes.Contains(key)) AccountPendingDeletes.Add(key);
            Saved.RemoveAll(s => s.Key == key);
            Save();
        }
        ForgetThumbnail(key);
    }

    /// <summary>
    /// Junta a lista daqui com a da conta: o que está na conta vem para cá, o que foi salvo ou mudado aqui vai para
    /// lá, e o que foi apagado em outro aparelho sai daqui. Devolve quantos computadores chegaram e saíram.
    /// </summary>
    public async Task<(int Added, int Removed)> SyncAccountAsync(Account account, CancellationToken ct)
    {
        string[] deletes;
        lock (_gate) deletes = AccountPendingDeletes.ToArray();
        foreach (string key in deletes)
        {
            await account.DeleteAsync(key, ct);
            lock (_gate) AccountPendingDeletes.Remove(key);
        }

        var remote = await account.ListAsync(ct);
        var gone = new List<string>();
        List<Account.Computer> push;
        int added = 0;
        lock (_gate)
        {
            var remoteKeys = remote.Select(r => r.Key).ToHashSet();
            foreach (var r in remote)
            {
                var local = Saved.Find(s => s.Key == r.Key);
                if (local is null)
                {
                    Saved.Add(new SavedEntry { Key = r.Key, Address = r.Address, Name = r.Name, AutoAccess = r.AutoAccess, Synced = true });
                    added++;
                }
                else if (!local.Dirty)
                {
                    local.Address = r.Address;
                    local.Name = r.Name;
                    local.AutoAccess = r.AutoAccess;
                    local.Synced = true;
                }
            }
            foreach (var entry in Saved.Where(s => s.Synced && !s.Dirty && !remoteKeys.Contains(s.Key)).ToList())
            {
                Saved.Remove(entry);
                gone.Add(entry.Key);
            }
            push = Saved.Where(s => s.Dirty || !s.Synced).Select(s => new Account.Computer(s.Key, s.Address, s.Name, s.AutoAccess)).ToList();
            Save();
        }
        foreach (string key in gone) ForgetThumbnail(key);

        await account.UpsertAsync(push, ct);
        lock (_gate)
        {
            var pushed = push.Select(p => p.Key).ToHashSet();
            foreach (var entry in Saved.Where(s => pushed.Contains(s.Key)))
            {
                entry.Synced = true;
                entry.Dirty = false;
            }
            Save();
        }
        return (added, gone.Count);
    }

    /// <summary>
    /// Sai da conta neste aparelho: os computadores que vieram dela (ou foram para ela) somem daqui, para a próxima
    /// pessoa que usar este Ponte não ver a lista de outra. O que foi salvo sem conta fica.
    /// </summary>
    public void SignOutAccount()
    {
        List<string> mine;
        lock (_gate)
        {
            mine = Saved.Where(s => s.Synced).Select(s => s.Key).ToList();
            Saved.RemoveAll(s => s.Synced);
            AccountEmail = AccountUserId = AccountToken = "";
            AccountPendingDeletes.Clear();
            Save();
        }
        foreach (string key in mine) ForgetThumbnail(key);
    }

    public void RemoveRecent(string address)
    {
        string key = Target(address).Key;
        lock (_gate)
        {
            Recent.RemoveAll(r => r.Address == address);
            Save();
        }
        if (FindSaved(key) is null) ForgetThumbnail(key);
    }

    /// <summary>Onde fica a miniatura de um computador acessado: a última imagem vista da tela dele, reduzida.</summary>
    public string ThumbnailPath(string key) =>
        Path.Combine(Dir, "miniaturas", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..20] + ".jpg");

    private void ForgetThumbnail(string key)
    {
        try { File.Delete(ThumbnailPath(key)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Depois de uma conexão feita: a identidade vista, a lista de recentes e o estado da autorização no salvo.</summary>
    public void RecordConnection(string key, string shown, string hostName, string fingerprint, bool paired)
    {
        lock (_gate)
        {
            KnownHosts[key] = fingerprint;
            if (Saved.Find(s => s.Key == key) is { } saved)
            {
                saved.Paired = paired;
                saved.LastUsed = DateTime.Now;
            }
            Recent.RemoveAll(r => r.Address == shown);
            Recent.Insert(0, new RecentEntry { Address = shown, Name = hostName, When = DateTime.Now });
            // Quem sai da lista de recentes sem estar salvo não deixa a miniatura para trás.
            foreach (var old in Recent.Skip(12).Where(r => Saved.TrueForAll(s => s.Key != Target(r.Address).Key)))
                ForgetThumbnail(Target(old.Address).Key);
            if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
            Save();
        }
    }

    // ---------- lado de quem é acessado ----------

    public TrustedViewer[] TrustedSnapshot()
    {
        lock (_gate) return Trusted.ToArray();
    }

    public bool IsTrusted(string keyHash)
    {
        lock (_gate) return Trusted.Exists(t => t.KeyHash == keyHash);
    }

    /// <summary>Autoriza um computador a entrar direto. Falso quando a lista já está cheia.</summary>
    public bool Trust(string keyHash, string id, string name, string user, bool viaPassword)
    {
        lock (_gate)
        {
            var entry = Trusted.Find(t => t.KeyHash == keyHash);
            if (entry is null)
            {
                if (Trusted.Count >= MaxTrusted) return false;
                Trusted.Add(entry = new TrustedViewer { KeyHash = keyHash, Id = id, Added = DateTime.Now, ViaPassword = viaPassword });
            }
            entry.Name = name;
            entry.User = user;
            entry.LastUsed = DateTime.Now;
            Save();
            return true;
        }
    }

    /// <summary>Marca o acesso de agora de um computador autorizado. Falso se ele saiu da lista nesse meio-tempo.</summary>
    public bool TouchTrusted(string keyHash, string name, string user)
    {
        lock (_gate)
        {
            if (Trusted.Find(t => t.KeyHash == keyHash) is not { } entry) return false;
            entry.Name = name;
            entry.User = user;
            entry.LastUsed = DateTime.Now;
            Save();
            return true;
        }
    }

    public void RemoveTrusted(IEnumerable<string> keyHashes)
    {
        var remove = keyHashes.ToHashSet();
        lock (_gate)
        {
            Trusted.RemoveAll(t => remove.Contains(t.KeyHash));
            Save();
        }
    }
}
