using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace Ponte;

/// <summary>Uma sessão em que este computador controla outro.</summary>
internal sealed class ViewerSession : IAsyncDisposable
{
    private readonly Channel _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _receiveDir;
    private Task? _reader;
    private int _closedRaised;
    private VideoDecoder? _decoder;
    private int _videoId;
    private readonly DateTime _started = DateTime.Now;
    public bool HostHasVideo { get; }
    public string VideoStatus { get; private set; } = "JPEG · 20 FPS alvo";
    public event Action<string>? VideoStatusChanged;
    private int _videoStatusVersion;
    /// <summary>Quantas vezes o status de vídeo mudou; serve para esperar um status novo.</summary>
    public int VideoStatusVersion => Volatile.Read(ref _videoStatusVersion);

    public string Target { get; }
    /// <summary>Como o destino é identificado na configuração ("id:..." ou "ip:...").</summary>
    public string Key { get; }
    public string HostName { get; }
    public ScreenInfo[] Screens { get; }
    public string Fingerprint { get; }
    /// <summary>Este computador está na lista de autorizados do outro: as próximas conexões entram direto.</summary>
    public bool Paired { get; }
    /// <summary>Versão do Ponte no outro computador; vazia quando ele é anterior à 1.2.1 e não informa.</summary>
    public string HostVersion { get; }
    /// <summary>O outro computador transmite o som dele quando pedido (1.4.0 em diante, e se o dono não desligou).</summary>
    public bool HostHasSound { get; }

    /// <summary>Imagem atual da tela remota. Quem lê ou desenha precisa segurar <see cref="CanvasLock"/>.</summary>
    public Bitmap? Canvas { get; private set; }
    public readonly object CanvasLock = new();
    public long Frames;

    public event Action? FrameUpdated;
    public event Action<string>? Closed;
    public event Action<string>? FileReceived;
    public event Action<string>? ClipboardReceived;
    /// <summary>Aviso vindo do computador controlado, para mostrar a quem está operando.</summary>
    public event Action<string>? Notice;
    /// <summary>O ponteiro do mouse de lá mudou de forma. O conteúdo é o de <see cref="RemotePointer.FromPayload"/>.</summary>
    public event Action<byte[]>? PointerChanged;
    /// <summary>
    /// O ponteiro de lá mudou de lugar: x e y de 0 a 65535 na tela mostrada, e se ele está nela. A janela
    /// da sessão não usa (o ponteiro daqui já está no lugar); existe para o autoteste conferir o envio.
    /// </summary>
    public event Action<int, int, bool>? PointerMoved;
    /// <summary>Um trecho de som de lá: amostras de 16 bits intercaladas, amostras por segundo e canais.</summary>
    public event Action<short[], int, int>? SoundReceived;

    public long BytesReceived => Interlocked.Read(ref _channel.BytesReceived);

    private ViewerSession(Channel channel, string key, string target, AuthResultMsg result, string fingerprint, string receiveDir)
    {
        _channel = channel;
        _receiveDir = receiveDir;
        Key = key;
        Target = target;
        HostHasSound = (result.Caps & Caps.Audio) != 0;
        HostHasVideo = (result.Caps & Caps.Video) != 0;
        HostName = result.HostName;
        Screens = result.Screens;
        Fingerprint = fingerprint;
        Paired = result.Paired;
        HostVersion = result.Version is { Length: > 0 and <= 20 } v ? v : "";
    }

    /// <param name="askPassword">Chamado quando o outro lado pede senha. Devolve a senha, "" para pedir permissão ou null para desistir.</param>
    /// <param name="confirmNewIdentity">Chamado quando a identidade do destino mudou desde a última conexão.</param>
    public static async Task<ViewerSession> ConnectAsync(AppConfig config, string target, Func<Task<string?>> askPassword,
        Func<Task<bool>> confirmNewIdentity, string receiveDir, CancellationToken ct)
    {
        var (key, shown) = AppConfig.Target(target);

        var (ssl, remote) = await Transport.ConnectAsync(config, target, ct);
        var channel = new Channel(ssl);
        try
        {
            string fingerprint = Security.Fingerprint(remote);
            if (config.KnownHosts.TryGetValue(key, out string? known) && known != fingerprint && !await confirmNewIdentity())
                throw new PonteException("Conexão cancelada.");

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(20));
            string ownKey = Convert.ToBase64String(config.Certificate.PublicKey.ExportSubjectPublicKeyInfo());
            await channel.SendJsonAsync(Msg.Hello, new HelloMsg(Environment.MachineName, Environment.UserName, Updater.Current.ToString(), ownKey,
                Caps.Files | Caps.Cursor | Caps.Audio | Caps.Video), handshake.Token);
            var challenge = await channel.ExpectAsync<ChallengeMsg>(Msg.Challenge, handshake.Token);
            byte[] nonce = Convert.FromBase64String(challenge.Nonce), remoteHash = Security.CertificateHash(remote);
            if (nonce.Length != 32) throw new PonteException("O outro computador enviou um desafio inválido.");
            // A assinatura vai sempre: é ela que deixa o outro lado reconhecer este computador, agora ou numa próxima vez.
            string signature = Security.SignPairing(config.Certificate, nonce, remoteHash);

            string? proof = null;
            if (challenge.HasPassword && !challenge.Trusted)
            {
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                string? password = await askPassword() ?? throw new PonteException("Conexão cancelada.");
                if (password.Length > 0)
                {
                    if (challenge.Iterations is < 1000 or > Security.MaxIterations) throw new PonteException("O outro computador enviou parâmetros de senha inválidos.");
                    byte[] verifier = await Task.Run(() => Security.DeriveVerifier(password, Convert.FromBase64String(challenge.Salt), challenge.Iterations), ct);
                    proof = Convert.ToBase64String(Security.Proof(verifier, nonce, remoteHash));
                }
            }
            bool remember = config.FindSaved(key)?.AutoAccess == true;
            await channel.SendJsonAsync(Msg.AuthResponse, new AuthResponseMsg(proof, signature, remember), ct);

            // Sem senha nem autorização, a resposta depende de alguém clicar em Aceitar do outro lado.
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waiting.CancelAfter(TimeSpan.FromSeconds(75));
            var result = await channel.ExpectAsync<AuthResultMsg>(Msg.AuthResult, waiting.Token);
            if (!result.Ok) throw new PonteException(result.Reason.Length > 0 ? result.Reason : "Conexão recusada.");

            channel.Limit = Channel.MaxPayload;
            config.RecordConnection(key, shown, result.HostName, fingerprint, result.Paired);
            return new ViewerSession(channel, key, shown, result, fingerprint, receiveDir);
        }
        catch (Exception ex)
        {
            await channel.DisposeAsync();
            if (ex is OperationCanceledException && !ct.IsCancellationRequested) throw new PonteException("O outro computador não respondeu a tempo.");
            if (ex is IOException or InvalidDataException or FormatException or System.Text.Json.JsonException) throw new PonteException("A conexão caiu durante a identificação.");
            throw;
        }
    }

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    private async Task ReadLoopAsync()
    {
        string reason = "O outro computador encerrou a sessão.";
        // O motivo técnico, para o registro de sessões (o de cima é o que aparece na tela).
        string detail = "a conexão foi fechada sem aviso (pelo outro lado ou no caminho)";
        SessionLog.Write($"acessando: começou a sessão com {HostName} ({Target}, Ponte {HostVersion} lá)");
        try
        {
            using var files = new FileReceiver(_receiveDir);
            while (await _channel.ReadAsync(_stop.Token) is { } m)
            {
                switch (m.Type)
                {
                    case Msg.Frame:
                        // Quadro do modo compatível: o vídeo, se havia, acabou do outro lado.
                        if (_decoder is not null) await StopVideoAsync();
                        ApplyFrame(m.Payload);
                        break;
                    case Msg.VideoStart when HostHasVideo && m.Payload.Length <= 4096:
                        await StartVideoAsync(Channel.FromJson<VideoStartMsg>(m.Payload));
                        break;
                    case Msg.VideoData when HostHasVideo && m.Payload.Length is > 4 and <= AviFrameReader.MaxFrame + 4:
                        if (_decoder is { } decoder && BitConverter.ToInt32(m.Payload) == decoder.Id)
                            await decoder.FeedAsync(m.Payload[4..], _stop.Token);
                        break;
                    case Msg.VideoStatus when HostHasVideo && m.Payload.Length <= 2048:
                        SetVideoStatus(Encoding.UTF8.GetString(m.Payload));
                        break;
                    case Msg.Cursor when m.Payload.Length is >= 5 and <= 512 * 1024:
                        PointerChanged?.Invoke(m.Payload);
                        break;
                    case Msg.CursorPos when m.Payload.Length >= 5:
                        PointerMoved?.Invoke(BitConverter.ToUInt16(m.Payload, 1), BitConverter.ToUInt16(m.Payload, 3), (m.Payload[0] & 1) != 0);
                        break;
                    case Msg.Audio:
                        if (SoundReceived is { } listener && AudioCodec.Decode(m.Payload, out int rate, out int channels) is { } samples)
                            listener(samples, rate, channels);
                        break;
                    case Msg.Clipboard when m.Payload.Length <= 256 * 1024:
                        ClipboardReceived?.Invoke(Encoding.UTF8.GetString(m.Payload));
                        break;
                    case Msg.FileStart or Msg.FileChunk or Msg.FileEnd:
                        if (files.Handle(m.Type, m.Payload) is { } path) FileReceived?.Invoke(path);
                        break;
                    case Msg.Notice when m.Payload.Length <= 2048:
                        Notice?.Invoke(Encoding.UTF8.GetString(m.Payload));
                        break;
                    case Msg.Bye:
                        detail = "o outro computador encerrou";
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "";
            detail = "encerrada neste computador";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or ArgumentException)
        {
            reason = _stop.IsCancellationRequested ? "" : "A conexão com o outro computador caiu.";
            detail = _stop.IsCancellationRequested ? "encerrada neste computador" : "a conexão caiu: " + SessionLog.Describe(ex);
        }
        catch (Exception ex)
        {
            // Defeito do próprio Ponte: a sessão termina avisando, e o detalhe fica em erros.txt.
            ErrorLog.Write(ex);
            reason = $"A sessão terminou por um erro do Ponte ({ex.GetType().Name}). O detalhe ficou registrado em erros.txt.";
            detail = "erro do Ponte: " + ex.GetType().Name;
        }
        finally
        {
            SessionLog.Write($"acessando: fim da sessão com {HostName} ({Target}) depois de {SessionLog.Duration(_started)}, " +
                             $"recebidos {SessionLog.Megabytes(BytesReceived)}, {VideoStatus}: {detail}");
            try { await StopVideoAsync(); }
            catch (Exception ex) { ErrorLog.Write(ex); }
            if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(reason);
        }
    }

    private void SetVideoStatus(string status)
    {
        Interlocked.Increment(ref _videoStatusVersion);
        VideoStatus = status;
        VideoStatusChanged?.Invoke(status);
    }

    private async Task StopVideoAsync()
    {
        var previous = _decoder;
        _decoder = null;
        lock (CanvasLock)
        {
            _videoId = 0;
            // Os bitmaps do vídeo somem com o decodificador; o último quadro fica numa cópia, para a tela não piscar.
            if (previous is not null && previous.Owns(Canvas))
            {
                var copy = new Bitmap(Canvas!.Width, Canvas.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(copy)) g.DrawImageUnscaled(Canvas, 0, 0);
                Canvas = copy;
            }
        }
        if (previous is not null) await previous.DisposeAsync();
    }

    private async Task StartVideoAsync(VideoStartMsg start)
    {
        await StopVideoAsync();
        lock (CanvasLock) _videoId = start.Id;
        int received = 0;
        string actual = $"{VideoOptions.CodecName(start.Codec)} pela GPU ({VideoEncoder.Vendor(start.Encoder)}) · {start.Fps} FPS alvo · " +
            (start.Chroma444 ? "4:4:4" : "4:2:0") + (start.UltraLowLatency ? " · ultra baixa latência" : "") +
            (start.Capture.Length > 0 ? $" · captura {start.Capture}" : "");
        SetVideoStatus($"Iniciando o {VideoOptions.CodecName(start.Codec)}...");
        void Failed(string reason)
        {
            if (_stop.IsCancellationRequested || Volatile.Read(ref _videoId) != start.Id) return;
            SetVideoStatus("O vídeo não pôde continuar aqui; voltando ao modo compatível.");
            _ = Quiet(_channel.SendJsonAsync(Msg.VideoFeedback, new VideoFeedbackMsg(start.Id, true, reason), _stop.Token));
        }
        VideoDecoder? decoder = null;
        bool Present(Bitmap frame)
        {
            lock (CanvasLock)
            {
                if (_videoId != start.Id) return false;
                if (Canvas is { } old && !ReferenceEquals(old, frame) && decoder?.Owns(old) != true) old.Dispose();
                Canvas = frame;
            }
            Interlocked.Increment(ref Frames);
            if (Interlocked.Exchange(ref received, 1) == 0) SetVideoStatus(actual);
            FrameUpdated?.Invoke();
            return true;
        }
        try
        {
            _decoder = decoder = new VideoDecoder(start, Present, Failed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { Failed(ex.Message); }
    }

    private void ApplyFrame(byte[] p)
    {
        if (p.Length < 6) throw new InvalidDataException("Quadro incompleto.");
        int width = BitConverter.ToUInt16(p, 0), height = BitConverter.ToUInt16(p, 2), count = BitConverter.ToUInt16(p, 4);
        if (width == 0 || height == 0) throw new InvalidDataException("Quadro sem dimensões.");
        lock (CanvasLock)
        {
            if (Canvas is null || Canvas.Width != width || Canvas.Height != height)
            {
                Canvas?.Dispose();
                Canvas = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            }
            using var g = Graphics.FromImage(Canvas);
            int offset = 6;
            for (int i = 0; i < count; i++)
            {
                if (offset + 12 > p.Length) throw new InvalidDataException("Quadro incompleto.");
                int x = BitConverter.ToUInt16(p, offset), y = BitConverter.ToUInt16(p, offset + 2);
                int w = BitConverter.ToUInt16(p, offset + 4), h = BitConverter.ToUInt16(p, offset + 6);
                int length = BitConverter.ToInt32(p, offset + 8);
                offset += 12;
                if (length <= 0 || offset + length > p.Length || x + w > width || y + h > height) throw new InvalidDataException("Quadro incompleto.");
                using var stream = new MemoryStream(p, offset, length, false);
                using var image = Image.FromStream(stream);
                g.DrawImage(image, x, y, w, h);
                offset += length;
            }
        }
        Interlocked.Increment(ref Frames);
        FrameUpdated?.Invoke();
    }

    public Task SendMouseAsync(int nx, int ny, int action, int wheel)
    {
        var p = new byte[7];
        BitConverter.TryWriteBytes(p.AsSpan(0), (ushort)Math.Clamp(nx, 0, 65535));
        BitConverter.TryWriteBytes(p.AsSpan(2), (ushort)Math.Clamp(ny, 0, 65535));
        p[4] = (byte)action;
        BitConverter.TryWriteBytes(p.AsSpan(5), (short)Math.Clamp(wheel, short.MinValue, short.MaxValue));
        return Quiet(_channel.SendAsync(Msg.Mouse, p, _stop.Token));
    }

    public Task SendKeyAsync(int virtualKey, bool up, bool extended)
    {
        var p = new byte[3];
        BitConverter.TryWriteBytes(p.AsSpan(0), (ushort)virtualKey);
        p[2] = (byte)((up ? 1 : 0) | (extended ? 2 : 0));
        return Quiet(_channel.SendAsync(Msg.Key, p, _stop.Token));
    }

    /// <summary>Texto pronto para o outro lado digitar. Um controlado anterior à 1.3.0 ignora a mensagem.</summary>
    public Task SendTextAsync(string text) => Quiet(_channel.SendAsync(Msg.Text, Encoding.UTF8.GetBytes(text), _stop.Token));

    /// <param name="sound">Pede o som de lá. Um controlado anterior à 1.4.0 ignora o pedido.</param>
    public Task SendSettingsAsync(int screen, int quality, bool sound = false, VideoOptions? video = null) =>
        Quiet(_channel.SendJsonAsync(Msg.Settings, new SettingsMsg(screen, quality, sound, HostHasVideo ? video?.Normalize() : null), _stop.Token));

    /// <summary>Grava uma redução da tela remota, para reconhecer o computador na lista. Falso se ainda não há imagem.</summary>
    public bool SaveThumbnail(string path)
    {
        const int width = 320, height = 180;
        using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        lock (CanvasLock)
        {
            if (Canvas is null) return false;
            double scale = Math.Min(width / (double)Canvas.Width, height / (double)Canvas.Height);
            int w = Math.Max(1, (int)(Canvas.Width * scale)), h = Math.Max(1, (int)(Canvas.Height * scale));
            using var g = Graphics.FromImage(small);
            g.Clear(Color.FromArgb(30, 30, 30));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Canvas, (width - w) / 2, (height - h) / 2, w, h);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            small.Save(path + ".tmp", ImageFormat.Jpeg);
            File.Move(path + ".tmp", path, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    public Task SendClipboardAsync(string text) => Quiet(_channel.SendAsync(Msg.Clipboard, Encoding.UTF8.GetBytes(text), _stop.Token));

    public Task SendFileAsync(string path, IProgress<int>? progress) => FileSender.SendAsync(_channel, path, progress, _stop.Token);

    // Entrada de mouse e teclado é disparada sem esperar; se a conexão caiu, quem avisa é o laço de leitura.
    private static async Task Quiet(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            try
            {
                using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _channel.SendAsync(Msg.Bye, default, brief.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        _stop.Cancel();
        await _channel.DisposeAsync();
        if (_reader is not null)
        {
            try { await _reader; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        lock (CanvasLock)
        {
            Canvas?.Dispose();
            Canvas = null;
        }
    }
}
