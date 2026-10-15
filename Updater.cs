using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ponte;

internal sealed record UpdateManifest(string Version, long Size, string Sha256, string Signature);

/// <summary>
/// Atualização automática. O programa baixa a versão nova do mesmo servidor de retransmissão, mas só
/// a aceita se a assinatura conferir com a chave pública embutida aqui. A chave privada que assina
/// fica apenas na máquina de quem publica: nem o servidor nem alguém no meio do caminho consegue
/// fabricar uma atualização válida. Isso importa em dobro porque o Ponte pode estar como administrador.
/// </summary>
internal static class Updater
{
    private const long MaxSize = 64L * 1024 * 1024;

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private static string ExePath => Environment.ProcessPath ?? throw new PonteException("Caminho do programa indisponível.");

    /// <summary>O executável de uso é um arquivo só; a versão de desenvolvimento tem uma DLL ao lado.</summary>
    public static bool IsSingleFile => !File.Exists(Path.ChangeExtension(ExePath, ".dll"));

    /// <summary>Só o executável de arquivo único consegue trocar a si mesmo.</summary>
    public static bool CanSelfUpdate => UpdateKey.PublicKey.Length > 0 && IsSingleFile;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static byte[] SignedMessage(string version, long size, string sha256) =>
        Encoding.UTF8.GetBytes($"ponte-atualizacao\n{version}\n{size}\n{sha256.ToLowerInvariant()}\n");

    private static bool WellFormed(UpdateManifest m) =>
        m is { Version: not null, Sha256: not null, Signature: not null } && Version.TryParse(m.Version, out _) &&
        m.Size is > 0 and <= MaxSize && m.Sha256.Length == 64 && m.Sha256.All(Uri.IsHexDigit);

    public static bool Verify(UpdateManifest m)
    {
        if (UpdateKey.PublicKey.Length == 0 || !WellFormed(m)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateKey.PublicKey), out _);
            return key.VerifyData(SignedMessage(m.Version, m.Size, m.Sha256), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Devolve a atualização disponível, ou null se esta já é a versão mais nova (nunca volta para versão anterior).</summary>
    public static async Task<UpdateManifest?> CheckAsync(AppConfig config, CancellationToken ct)
    {
        if (UpdateKey.PublicKey.Length == 0 || config.RelayServer.Length == 0) return null;
        byte[]? raw = await Transport.FetchAsync(config, "MANIFEST", 8192, ct);
        if (raw is null) return null;

        UpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(raw); }
        catch (JsonException) { manifest = null; }
        if (manifest is null || !Verify(manifest))
            throw new PonteException("A atualização oferecida pelo servidor não tem assinatura válida e foi ignorada.");
        return Normalize(Version.Parse(manifest.Version)) > Current ? manifest : null;
    }

    /// <summary>Baixa e confere o executável novo; devolve o caminho dele, ao lado do atual.</summary>
    public static async Task<string> DownloadAsync(AppConfig config, UpdateManifest manifest, CancellationToken ct)
    {
        byte[] data = await Transport.FetchAsync(config, "DOWNLOAD", (int)manifest.Size, ct)
                      ?? throw new PonteException("O servidor não entregou o arquivo de atualização.");
        if (!Matches(data, manifest)) throw new PonteException("O arquivo de atualização não confere com a assinatura e foi descartado.");
        string path = Path.Combine(Path.GetDirectoryName(ExePath)!, "Ponte-novo.exe");
        await File.WriteAllBytesAsync(path, data, ct);
        return path;
    }

    private static bool Matches(byte[] data, UpdateManifest m) =>
        data.Length == m.Size && Convert.ToHexString(SHA256.HashData(data)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Confere e troca o executável, e abre a versão nova (ver <see cref="Verify"/> e <see cref="Replace"/>).</summary>
    public static void Apply(string newExe, UpdateManifest manifest, IEnumerable<string> restartArgs)
    {
        Verify(newExe, manifest);
        Replace(newExe, restartArgs);
    }

    /// <summary>
    /// Confere o arquivo baixado e faz o teste de partida: uma versão que nem abre não substitui a que está
    /// funcionando. O prazo é folgado porque, logo depois de ligar o computador, tudo demora mais.
    /// </summary>
    public static void Verify(string newExe, UpdateManifest manifest)
    {
        if (!CanSelfUpdate) throw new PonteException("Esta cópia do Ponte não se atualiza sozinha.");
        // O arquivo ficou no disco desde o download; confere de novo antes de executar qualquer coisa dele.
        if (!Matches(File.ReadAllBytes(newExe), manifest)) throw new PonteException("O arquivo de atualização foi alterado depois de baixado e foi descartado.");

        using var probe = Process.Start(new ProcessStartInfo(newExe) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--verificar" } });
        if (probe is null || !probe.WaitForExit(90_000))
        {
            try { probe?.Kill(); } catch (InvalidOperationException) { }
            throw new PonteException("A versão nova não respondeu ao teste de partida e não foi instalada.");
        }
        if (probe.ExitCode != 0) throw new PonteException("A versão nova falhou no teste de partida e não foi instalada.");
    }

    /// <summary>
    /// Troca o executável e abre a versão nova. O Windows não deixa sobrescrever um programa em execução,
    /// mas deixa renomeá-lo: o atual vira ".antigo" e o novo assume o nome. Quem chama deve encerrar em seguida.
    /// </summary>
    public static void Replace(string newExe, IEnumerable<string> restartArgs)
    {
        string exe = ExePath, old = exe + ".antigo";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(newExe, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PonteException("Não foi possível trocar o arquivo do programa. Ele está numa pasta protegida ou em uso.");
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in restartArgs) start.ArgumentList.Add(a);
        Process.Start(start)?.Dispose();
    }

    /// <summary>Remove o que sobrou da última troca. Chamado na partida.</summary>
    public static void Cleanup()
    {
        try
        {
            string exe = ExePath;
            foreach (string leftover in new[] { exe + ".antigo", Path.Combine(Path.GetDirectoryName(exe)!, "Ponte-novo.exe") })
            {
                if (File.Exists(leftover) && !string.Equals(leftover, exe, StringComparison.OrdinalIgnoreCase)) File.Delete(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PonteException) { }
    }

    // ---------- ferramentas de quem publica (não usadas pelo programa em si) ----------

    /// <summary>Cria o par de chaves de assinatura. A privada fica protegida pela conta do Windows; a pública vai para UpdateKey.cs.</summary>
    public static int GenerateKey(string keyFile, string sourceFile)
    {
        if (File.Exists(keyFile)) return 3;   // nunca sobrescreve: perder a chave antiga deixaria as cópias instaladas sem atualização
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFile))!);
        File.WriteAllBytes(keyFile, ProtectedData.Protect(key.ExportPkcs8PrivateKey(), null, DataProtectionScope.CurrentUser));
        File.WriteAllText(sourceFile,
            "namespace Ponte;\n\n" +
            "/// <summary>Chave pública que confere a assinatura das atualizações. Vazia = atualização automática desligada.</summary>\n" +
            "internal static class UpdateKey\n{\n" +
            $"    public const string PublicKey = \"{Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}\";\n" +
            "}\n", new UTF8Encoding(false));
        return 0;
    }

    /// <summary>Assina um executável publicado e grava o manifesto que acompanha a atualização.</summary>
    public static int Sign(string exeFile, string keyFile, string manifestFile)
    {
        byte[] data = File.ReadAllBytes(exeFile);
        var info = FileVersionInfo.GetVersionInfo(exeFile);
        string version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        string sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(ProtectedData.Unprotect(File.ReadAllBytes(keyFile), null, DataProtectionScope.CurrentUser), out _);
        string signature = Convert.ToBase64String(key.SignData(SignedMessage(version, data.Length, sha256), HashAlgorithmName.SHA256));

        var manifest = new UpdateManifest(version, data.Length, sha256, signature);
        File.WriteAllText(manifestFile, JsonSerializer.Serialize(manifest), new UTF8Encoding(false));
        return Verify(manifest) ? 0 : 4;      // 4: a chave privada não corresponde à pública embutida neste executável
    }
}
