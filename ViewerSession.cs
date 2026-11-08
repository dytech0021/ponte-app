using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace Ponte;

/// <summary>Uma sessão em que este computador controla outro.</summary>
internal sealed class ViewerSession : IAsyncDisposable
{
    private Channel _channel;
    private Func<CancellationToken, Task<ViewerSession>>? _reconnect;
    private bool _unexpected, _restartPending;
    private volatile bool _reconnecting;
    private SettingsMsg? _lastSettings;
    private string _lastReason = "";
    private long _lastReceived = System.Diagnostics.Stopwatch.GetTimestamp();
    public bool CanReconnect => _reconnect is not null;
    public bool Reconnecting => _reconnecting;
    public bool HostHasManagement { get; }
    /// <summary>O outro computador manda todas as telas numa imagem só, para abrir uma janela por tela (1.16.0 em diante).</summary>
    public bool HostHasAllScreens { get; }
    /// <summary>1.17.0: o outro lado faz Ctrl+Alt+Del de verdade (pelo serviço dele, se instalado).</summary>
    public bool HostHasSecureAttention { get; }
    /// <summary>A conta liberada em que o outro aparelho está (do bilhete dele).</summary>
    public string HostAccount { get; private init; } = "";
    public double RoundTripMilliseconds { get; private set; }
    public event Action<string>? ConnectionStatusChanged;
    public event Action? Reconnected;
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
    public ScreenInfo[] Screens { get; private set; }
    public string Fingerprint { get; }
    /// <summary>Este computador está na lista de autorizados do outro: as próximas conexões entram direto.</summary>
    public bool Paired { get; }
    /// <summary>Versão do Ponte no outro computador; vazia quando ele é anterior à 1.2.1 e não informa.</summary>
    public string HostVersion { get; }
    /// <summary>O outro computador transmite o som dele quando pedido (1.4.0 em diante, e se o dono não desligou).</summary>
    public bool HostHasSound { get; }
    /// <summary>Do outro lado está um celular (Ponte para Android 1.14.0 em diante).</summary>
    public bool HostIsPhone { get; }

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
        HostHasManagement = (result.Caps & Caps.Management) != 0;
        HostHasVideo = (result.Caps & Caps.Video) != 0;
        HostIsPhone = (result.Caps & Caps.Phone) != 0;
        HostHasAllScreens = (result.Caps & Caps.AllScreens) != 0;
        HostHasSecureAttention = (result.Caps & Caps.SecureAttention) != 0;
        HostName = result.HostName;
        Screens = result.Screens;
        Fingerprint = fingerprint;
        Paired = result.Paired;
        HostVersion = result.Version is { Length: > 0 and <= 20 } v ? v : "";
    }

    /// <param name="askPassword">Chamado quando o outro lado pede senha. Devolve a senha, "" para pedir permissão ou null para desistir.</param>
    /// <param name="confirmNewIdentity">Chamado quando a identidade do destino mudou desde a última conexão.</param>
    public static async Task<ViewerSession> ConnectAsync(AppConfig config, string target, Func<Task<string?>> askPassword,
        Func<Task<bool>> confirmNewIdentity, string receiveDir, CancellationToken ct, bool retrying = false)
    {
        var (key, shown) = AppConfig.Target(target);
        // 1.17.0: só quem está numa conta liberada acessa (o bilhete prova isso para o outro lado).
        if (!Ticket.Valid(config)) throw new PonteException("Para acessar outro aparelho, entre numa conta liberada do Ponte (botão Conta).");

        var (ssl, remote) = await Transport.ConnectAsync(config, target, ct);
        var channel = new Channel(ssl);
        try
        {
            string fingerprint = Security.Fingerprint(remote);
            if (config.KnownHosts.TryGetValue(key, out string? known) && known != fingerprint && (retrying || !await confirmNewIdentity()))
            {
                if (retrying) throw new UnauthorizedAccessException("A identidade do computador mudou. Conecte novamente para conferir.");
                throw new PonteException("Conexão cancelada.");
            }

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(20));
            string ownKey = Convert.ToBase64String(config.Certificate.PublicKey.ExportSubjectPublicKeyInfo());
            await channel.SendJsonAsync(Msg.Hello, new HelloMsg(Environment.MachineName, Environment.UserName, Updater.Current.ToString(), ownKey,
                Caps.Files | Caps.Cursor | Caps.Audio | Caps.Video, Resume: retrying, Ticket: config.Ticket), handshake.Token);
            var challenge = await channel.ExpectAsync<ChallengeMsg>(Msg.Challenge, handshake.Token);
            // O outro lado também precisa estar numa conta liberada. Sem bilhete, ele é anterior à 1.17.0 (e responder
            // sem senha abriria um pedido de aceite lá) ou ninguém entrou na conta nele: para aqui mesmo.
            string? hostAccount = Ticket.Verify(challenge.Ticket, Security.KeyHash(remote.PublicKey.ExportSubjectPublicKeyInfo()), out string ticketProblem);
            if (hostAccount is null)
                throw new PonteException(challenge.Ticket is null
                    ? "O outro aparelho não está numa conta liberada do Ponte (ou ainda não tem a versão 1.17.0). Alguém precisa abrir o Ponte nele, atualizar e entrar na conta."
                    : $"O outro aparelho mandou um bilhete de conta que não vale ({ticketProblem}). Alguém precisa sair e entrar de novo na conta nele.");
            byte[] nonce = Convert.FromBase64String(challenge.Nonce), remoteHash = Security.CertificateHash(remote);
            if (nonce.Length != 32) throw new PonteException("O outro computador enviou um desafio inválido.");
            // A assinatura vai sempre: é ela que deixa o outro lado reconhecer este computador, agora ou numa próxima vez.
            string signature = Security.SignPairing(config.Certificate, nonce, remoteHash);

            string? proof = null;
            string? reconnectPassword = null;
            if (retrying && !challenge.Trusted && !challenge.HasPassword) throw new UnauthorizedAccessException("A autorização automática não está mais disponível.");
            if (challenge.HasPassword && !challenge.Trusted)
            {
                handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                string? password = await askPassword();
                if (password is null) { if (retrying) throw new UnauthorizedAccessException("A autorização foi revogada. Conecte novamente."); throw new PonteException("Conexão cancelada."); }
                reconnectPassword = password;
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
            if (!result.Ok)
            {
                string denied = result.Reason.Length > 0 ? result.Reason : "Conexão recusada.";
                if (retrying && !denied.Contains("sessão em andamento")) throw new UnauthorizedAccessException(denied);
                throw new PonteException(denied);
            }

            channel.Limit = Channel.MaxPayload;
            config.RecordConnection(key, shown, result.HostName, fingerprint, result.Paired);
            var connected = new ViewerSession(channel, key, shown, result, fingerprint, receiveDir) { HostAccount = hostAccount };
            if (config.AutoReconnect && connected.HostHasManagement && (result.Paired || !string.IsNullOrEmpty(reconnectPassword)))
                connected._reconnect = token => ConnectAsync(config, target, () => Task.FromResult(reconnectPassword),
                    () => Task.FromResult(false), receiveDir, token, retrying: true);
            return connected;
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
        try
        {
            bool first = true;
            while (first || !_stop.IsCancellationRequested)
            {
                first = false;
                _unexpected = false;
                await ReadOnceAsync();
                if (_stop.IsCancellationRequested || !_unexpected || _reconnect is null) break;
                _reconnecting = true;
                await _channel.DisposeAsync();
                bool restored = false;
                var until = DateTime.UtcNow.AddMinutes(10);
                for (int attempt = 1; DateTime.UtcNow < until && !_stop.IsCancellationRequested; attempt++)
                {
                    ConnectionStatusChanged?.Invoke($"{(_restartPending ? "Aguardando o computador reiniciar" : "Conexão interrompida")}. Reconectando... tentativa {attempt}. Feche esta janela para cancelar.");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), _stop.Token);
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        var next = await _reconnect(deadline.Token);
                        _channel = next._channel; Screens = next.Screens;
                        _lastReceived = System.Diagnostics.Stopwatch.GetTimestamp();
                        _reconnecting = false; _restartPending = false;
                        if (_lastSettings is { } settings) await _channel.SendJsonAsync(Msg.Settings, settings, _stop.Token);
                        ConnectionStatusChanged?.Invoke("Reconectado a " + HostName);
                        SessionLog.Write("acessando: sessão retomada após interrupção com " + HostName);
                        Reconnected?.Invoke(); restored = true; break;
                    }
                    catch (UnauthorizedAccessException ex) { _lastReason = ex.Message; break; }
                    catch (Exception ex) when (ex is PonteException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
                }
                if (!restored) { if (_lastReason.Length == 0) _lastReason = "Não foi possível retomar a conexão em dez minutos."; break; }
            }
        }
        catch (OperationCanceledException) { }
        finally { _reconnecting = false; if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(_stop.IsCancellationRequested ? "" : _lastReason); }
    }

    private async Task ReadOnceAsync()
    {
        string reason = "O outro computador encerrou a sessão.";
        // O motivo técnico, para o registro de sessões (o de cima é o que aparece na tela).
        string detail = "a conexão foi fechada sem aviso (pelo outro lado ou no caminho)";
        SessionLog.Write($"acessando: começou a sessão com {HostName} ({Target}, Ponte {HostVersion} lá)");
        try
        {
            _unexpected = true;
            using var files = new FileReceiver(_receiveDir);
            while (await _channel.ReadAsync(_stop.Token) is { } m)
            {
                Interlocked.Exchange(ref _lastReceived, System.Diagnostics.Stopwatch.GetTimestamp());
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
                        _unexpected = false;
                        // A 1.13.0 em diante diz o motivo quando foi ela que encerrou (por falta de uso, por exemplo).
                        string told = new(Encoding.UTF8.GetString(m.Payload.AsSpan(0, Math.Min(m.Payload.Length, 2048)))
                            .Where(c => !char.IsControl(c)).ToArray());
                        if (told.Length > 0) reason = told;
                        detail = "o outro computador encerrou" + (told.Length > 0 ? $": {told}" : "");
                        return;
                    case Msg.Pong when m.Payload.Length == 8:
                        long sent = BitConverter.ToInt64(m.Payload);
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (sent > 0 && sent <= now) RoundTripMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(sent, now).TotalMilliseconds;
                        break;
                    case Msg.RestartResult:
                        string restartError = Encoding.UTF8.GetString(m.Payload);
                        _restartPending = restartError.Length == 0;
                        Notice?.Invoke(restartError.Length > 0 ? restartError : "Reinício aceito. Aguardando o computador voltar...");
                        break;
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
            _lastReason = reason;
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

    /// <summary>Ctrl+Alt+Del de verdade no outro computador (1.17.0). Se não der, a resposta chega como aviso.</summary>
    public Task SendSecureAttentionAsync() => Quiet(_channel.SendAsync(Msg.SecureAttention, default, _stop.Token));

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
    /// <param name="idleSeconds">Tempo sem uso até lá encerrar a sessão (0 é nunca; nulo deixa valer o de lá). Um controlado anterior à 1.13.0 ignora.</param>
    public Task SendSettingsAsync(int screen, int quality, bool sound = false, VideoOptions? video = null, int? idleSeconds = null)
    {
        _lastSettings = new SettingsMsg(screen, quality, sound, HostHasVideo ? video?.Normalize() : null, idleSeconds);
        return _reconnecting ? Task.CompletedTask : Quiet(_channel.SendJsonAsync(Msg.Settings, _lastSettings, _stop.Token));
    }

    public Task RequestRestartAsync()
    {
        if (!HostHasManagement || _reconnecting) throw new PonteException("O computador remoto não está pronto para reiniciar pelo Ponte.");
        _restartPending = true;
        return _channel.SendAsync(Msg.Restart, default, _stop.Token);
    }
    public Task PingAsync()
    {
        if (!HostHasManagement || _reconnecting || _stop.IsCancellationRequested) return Task.CompletedTask;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceived)).TotalSeconds > 20)
            return _channel.DisposeAsync().AsTask();
        return Quiet(_channel.SendAsync(Msg.Ping, BitConverter.GetBytes(System.Diagnostics.Stopwatch.GetTimestamp()), _stop.Token));
    }

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
        _reconnect = null;
    }
}
