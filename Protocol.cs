using System.Buffers.Binary;
using System.Text.Json;

namespace Ponte;

/// <summary>Erro com mensagem pronta para mostrar ao usuário.</summary>
internal sealed class PonteException(string message) : Exception(message);

internal enum Msg : byte
{
    Hello = 1, Challenge = 2, AuthResponse = 3, AuthResult = 4,
    Frame = 10, Cursor = 11, Audio = 12, CursorPos = 13,
    Mouse = 20, Key = 21, Text = 22,
    Clipboard = 30,
    FileStart = 40, FileChunk = 41, FileEnd = 42,
    Settings = 50, VideoStart = 51, VideoData = 52, VideoStatus = 53, VideoFeedback = 54,
    Notice = 60,
    Ping = 70, Pong = 71, Restart = 72, RestartResult = 73,
    /// <summary>1.17.0: Ctrl+Alt+Del de verdade (a tela segura do Windows), pelo serviço do Ponte do outro lado.</summary>
    SecureAttention = 74,
    /// <summary>1.17.1, do controlado: 1 byte, 1 quando o foco entrou num campo de texto e 0 quando saiu.</summary>
    TextFocus = 75,
    Bye = 99,
}

// Os campos com valor padrão vieram depois da 1.1: uma versão anterior simplesmente não os manda nem os lê.

/// <summary>O que cada lado sabe fazer além do básico. Vai no Hello (visualizador) e no AuthResult (controlado), a partir da 1.5.0.</summary>
internal static class Caps
{
    /// <summary>Visualizador: grava os arquivos que chegam. Controlado: não usado.</summary>
    public const int Files = 1;
    /// <summary>Visualizador: desenha o ponteiro que chega em <see cref="Msg.Cursor"/>. Controlado: envia o ponteiro.</summary>
    public const int Cursor = 2;
    /// <summary>Visualizador: toca o som de <see cref="Msg.Audio"/>. Controlado: transmite o som, se pedido em Settings.</summary>
    public const int Audio = 4;
    public const int Video = 8;
    /// <summary>Só no AuthResult (1.14.0): o controlado é um celular. O texto vai pronto e há Voltar, Início e Apps (<see cref="PhoneKeys"/>).</summary>
    public const int Phone = 16;
    public const int Management = 32;
    /// <summary>Só no AuthResult (1.16.0): Settings com Screen = <see cref="ScreenInfo.All"/> transmite todas as telas numa imagem só.</summary>
    public const int AllScreens = 64;
    /// <summary>Só no AuthResult (1.17.0): entende <see cref="Msg.SecureAttention"/> (Ctrl+Alt+Del). Se o serviço não estiver instalado, responde com um aviso.</summary>
    public const int SecureAttention = 128;
    /// <summary>1.17.1. Visualizador: quer saber quando o foco entra num campo de texto (abre o teclado do celular).
    /// Controlado: manda <see cref="Msg.TextFocus"/>.</summary>
    public const int TextFocus = 256;
}

/// <summary>Teclas que um celular controlado entende como os botões dele (1.14.0).</summary>
internal static class PhoneKeys
{
    public const int Back = 0xA6;    // VK_BROWSER_BACK
    public const int Home = 0xAC;    // VK_BROWSER_HOME
    public const int Recents = 0x5D; // VK_APPS
}

/// <summary>
/// Key é a chave pública do visualizador (SPKI em base64); é por ela que o outro lado o reconhece nas próximas vezes.
/// Caps nulo é um visualizador anterior à 1.5.0. Ticket (1.17.0) é o bilhete da conta liberada de quem acessa, preso a essa chave.
/// </summary>
internal sealed record HelloMsg(string Name, string User, string Version, string? Key = null, int? Caps = null, bool Resume = false, string? Ticket = null);
/// <summary>
/// Trusted avisa que a chave do visualizador já está autorizada: basta a assinatura, sem senha nem aceite.
/// Ticket (1.17.0) é o bilhete da conta liberada do computador acessado, preso à chave do certificado dele.
/// </summary>
internal sealed record ChallengeMsg(bool HasPassword, string Salt, int Iterations, string Nonce, bool Trusted = false, string? Ticket = null);
/// <summary>
/// Proof nulo significa "sem senha: peça permissão a quem está na frente do computador".
/// Signature prova a posse da chave enviada no Hello; Remember pede para ficar autorizado depois de entrar com a senha.
/// </summary>
internal sealed record AuthResponseMsg(string? Proof, string? Signature = null, bool Remember = false);
/// <summary>X e Y (1.16.0) são a posição da tela na área de trabalho de lá; antes vinham sempre zero.</summary>
internal sealed record ScreenInfo(int Index, int Width, int Height, bool Primary, int X = 0, int Y = 0)
{
    /// <summary>O Screen do Settings que pede todas as telas juntas, com o tamanho da área de trabalho inteira.</summary>
    public const int All = -1;
}
/// <summary>Paired diz ao visualizador que ele está na lista de autorizados do outro computador; Version é a versão do Ponte de lá.</summary>
internal sealed record AuthResultMsg(bool Ok, string Reason, string HostName, ScreenInfo[] Screens, bool Paired = false, string Version = "", int Caps = 0);
/// <summary>
/// Audio pede (ou dispensa) o som do computador controlado. IdleSeconds (1.13.0) é o tempo sem uso (sem mouse, teclado
/// ou toque) depois do qual quem acessa quer a sessão encerrada: 0 é nunca, e nulo deixa valer o do computador controlado.
/// </summary>
internal sealed record SettingsMsg(int Screen, int Quality, bool Audio = false, VideoOptions? Video = null, int? IdleSeconds = null);
internal sealed record FileStartMsg(string Name, long Size);

/// <summary>
/// Mensagens sobre o fluxo cifrado: 1 byte de tipo, 4 de tamanho e o conteúdo.
/// Antes da autenticação o tamanho aceito é pequeno, para um desconhecido não conseguir ocupar memória.
/// </summary>
internal sealed class Channel(Stream stream) : IAsyncDisposable
{
    public const int MaxPayload = 64 * 1024 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public readonly SemaphoreSlim FileLock = new(1, 1);
    public int Limit = 8 * 1024;
    public long BytesReceived, BytesSent;

    public async Task SendAsync(Msg type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new byte[5];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(header, ct);
            if (payload.Length > 0) await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
        Interlocked.Add(ref BytesSent, payload.Length + 5);
    }

    public Task SendJsonAsync<T>(Msg type, T value, CancellationToken ct) =>
        SendAsync(type, JsonSerializer.SerializeToUtf8Bytes(value), ct);

    /// <summary>Próxima mensagem, ou null quando o outro lado fechou a conexão.</summary>
    public async Task<(Msg Type, byte[] Payload)?> ReadAsync(CancellationToken ct)
    {
        var header = new byte[5];
        if (!await ReadExactAsync(header, ct)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > Limit) throw new InvalidDataException("Mensagem com tamanho inválido.");
        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(payload, ct)) return null;
        Interlocked.Add(ref BytesReceived, length + 5);
        return ((Msg)header[0], payload);
    }

    public async Task<T> ExpectAsync<T>(Msg type, CancellationToken ct)
    {
        var m = await ReadAsync(ct) ?? throw new PonteException("O outro computador encerrou a conexão.");
        if (m.Type != type) throw new InvalidDataException($"Esperava {type}, veio {m.Type}.");
        return FromJson<T>(m.Payload);
    }

    public static T FromJson<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Mensagem vazia.");

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        try { await stream.DisposeAsync(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
