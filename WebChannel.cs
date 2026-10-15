using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ponte;

/// <summary>
/// Canal cifrado de ponta a ponta para o Ponte no navegador. O navegador não faz TLS com o certificado
/// próprio de cada computador, então este canal faz o mesmo papel com o que a criptografia do navegador
/// oferece (ECDH, ECDSA, HKDF e AES-GCM, todos P-256/SHA-256):
///
///   navegador → computador: "WPONTE1" | chave efêmera ECDH (65 bytes, ponto não comprimido) | 32 bytes aleatórios
///   computador → navegador: chave efêmera (65) | tamanho do certificado (2 bytes) | certificado | assinatura (64, r||s)
///
/// A assinatura é da chave do computador (a mesma do TLS, da qual sai o ID) sobre
/// "ponte-web-1\n" | SHA-256(mensagem do navegador) | chave efêmera do computador | SHA-256(certificado).
/// O navegador confere o ID e a assinatura antes de mandar qualquer coisa, como o TLS faria.
///
/// Chaves: HKDF-SHA256 do segredo ECDH, com sal = SHA-256 das duas mensagens e informação
/// "ponte-web-1 c2s" (navegador para computador) ou "ponte-web-1 s2c" (o contrário), 32 bytes cada.
/// Registros: tamanho do cifrado (4 bytes, big-endian) | AES-256-GCM com nonce = 4 zeros | contador de 8 bytes.
/// Cada sentido tem chave e contador próprios; um registro alterado, fora de ordem ou repetido derruba a conexão.
/// </summary>
internal sealed class WebSecureStream : Stream
{
    public static ReadOnlySpan<byte> Magic => "WPONTE1"u8;
    public const int ClientHelloLength = 7 + 65 + 32;
    private const int MaxPlain = 1 << 20;
    private const int TagSize = 16;

    private readonly Stream _inner;
    private readonly AesGcm _receive, _send;
    private ulong _receiveCounter, _sendCounter;
    private readonly MemoryStream _pending = new();
    private byte[] _plain = Array.Empty<byte>();
    private int _plainOffset;

    private WebSecureStream(Stream inner, byte[] receiveKey, byte[] sendKey)
    {
        _inner = inner;
        _receive = new AesGcm(receiveKey, TagSize);
        _send = new AesGcm(sendKey, TagSize);
    }

    /// <summary>Faz o aperto de mão do lado do computador. O fluxo recebido começa na marca "WPONTE1".</summary>
    public static async Task<WebSecureStream> AcceptAsync(Stream inner, X509Certificate2 certificate, CancellationToken ct)
    {
        var clientHello = new byte[ClientHelloLength];
        await inner.ReadExactlyAsync(clientHello, ct);
        if (!clientHello.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("Início de conexão desconhecido.");

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] secret;
        using (var client = ECDiffieHellman.Create(PointToParameters(clientHello.AsSpan(7, 65))))
            secret = ephemeral.DeriveRawSecretAgreement(client.PublicKey);

        byte[] ownPoint = ParametersToPoint(ephemeral.ExportParameters(false));
        byte[] cert = certificate.RawData;
        byte[] signed = [.. "ponte-web-1\n"u8, .. SHA256.HashData(clientHello), .. ownPoint, .. SHA256.HashData(cert)];
        byte[] signature;
        using (var identity = certificate.GetECDsaPrivateKey() ?? throw new PonteException("Chave do computador indisponível."))
            signature = identity.SignData(signed, HashAlgorithmName.SHA256);

        var serverHello = new byte[65 + 2 + cert.Length + signature.Length];
        ownPoint.CopyTo(serverHello, 0);
        BinaryPrimitives.WriteUInt16BigEndian(serverHello.AsSpan(65), (ushort)cert.Length);
        cert.CopyTo(serverHello, 67);
        signature.CopyTo(serverHello, 67 + cert.Length);
        await inner.WriteAsync(serverHello, ct);
        await inner.FlushAsync(ct);

        byte[] salt = SHA256.HashData([.. clientHello, .. serverHello]);
        byte[] c2s = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "ponte-web-1 c2s"u8.ToArray());
        byte[] s2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "ponte-web-1 s2c"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        return new WebSecureStream(inner, c2s, s2c);
    }

    private static ECParameters PointToParameters(ReadOnlySpan<byte> point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new InvalidDataException("Chave efêmera inválida.");
        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() } };
    }

    private static byte[] ParametersToPoint(ECParameters p) => [0x04, .. p.Q.X!, .. p.Q.Y!];

    private static void Nonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_plainOffset >= _plain.Length)
        {
            var header = new byte[4];
            int got = await _inner.ReadAtLeastAsync(header, 4, false, ct);
            if (got == 0) return 0;
            if (got < 4) throw new IOException("Registro incompleto.");
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length < TagSize || length > MaxPlain + TagSize) throw new InvalidDataException("Registro com tamanho inválido.");
            var cipher = new byte[length];
            await _inner.ReadExactlyAsync(cipher, ct);
            var plain = new byte[length - TagSize];
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _receiveCounter++);
            _receive.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain);
            _plain = plain;
            _plainOffset = 0;
            if (plain.Length == 0) return await ReadAsync(buffer, ct);
        }
        int count = Math.Min(buffer.Length, _plain.Length - _plainOffset);
        _plain.AsMemory(_plainOffset, count).CopyTo(buffer);
        _plainOffset += count;
        return count;
    }

    // O canal de mensagens escreve o cabeçalho e o conteúdo em separado e chama Flush no fim de cada
    // mensagem: juntar até o Flush faz de cada mensagem um registro só.
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        _pending.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override async Task FlushAsync(CancellationToken ct)
    {
        byte[] data = _pending.ToArray();
        _pending.SetLength(0);
        for (int offset = 0; offset < data.Length; offset += MaxPlain)
        {
            int length = Math.Min(MaxPlain, data.Length - offset);
            var record = new byte[4 + length + TagSize];
            BinaryPrimitives.WriteUInt32BigEndian(record, (uint)(length + TagSize));
            var nonce = new byte[12];
            Nonce(nonce, _sendCounter++);
            _send.Encrypt(nonce, data.AsSpan(offset, length), record.AsSpan(4, length), record.AsSpan(4 + length, TagSize));
            await _inner.WriteAsync(record, ct);
        }
        await _inner.FlushAsync(ct);
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _receive.Dispose();
            _send.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Fluxo que devolve primeiro os bytes já lidos para saber que tipo de conexão chegou.</summary>
internal sealed class PrefixedStream(Stream inner, byte[] prefix) : Stream
{
    private int _used;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_used < prefix.Length)
        {
            int count = Math.Min(buffer.Length, prefix.Length - _used);
            prefix.AsMemory(_used, count).CopyTo(buffer);
            _used += count;
            return count;
        }
        return await inner.ReadAsync(buffer, ct);
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
