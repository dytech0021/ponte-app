using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Ponte;

// O FFmpeg roda em processo separado: um defeito nele não derruba o Ponte, e ele só recebe argumentos fixos.
internal sealed class VideoProcess : IDisposable
{
    public Process Process { get; }
    private readonly Task _errors;
    private string _error = "";
    /// <summary>Última linha de erro que o FFmpeg escreveu.</summary>
    public string Error => _error;

    public VideoProcess(IEnumerable<string> arguments)
    {
        string path = VideoRuntime.VerifiedPath();
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        try { Process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o vídeo."); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("Não foi possível iniciar o vídeo: " + ex.Message); }
        _errors = Task.Run(async () =>
        {
            try
            {
                while (await Process.StandardError.ReadLineAsync() is { } line)
                    if (line.Trim().Length > 0) _error = line.Length > 300 ? line[..300] : line;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        });
    }

    // Chamado ao trocar de modo e ao encerrar, inclusive de dentro do cancelamento: não pode falhar. O FFmpeg não
    // abre outros processos, então basta ele. (O "encerrar a árvore" às vezes falhava com AggregateException,
    // que subia pelo cancelamento até a leitura da sessão e a derrubava na troca de modo de vídeo.)
    public void Kill()
    {
        try { if (!Process.HasExited) Process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
    }

    public void Dispose()
    {
        Kill();
        try { Process.WaitForExit(1500); } catch (InvalidOperationException) { }
        Process.Dispose();
    }
}

/// <summary>De onde vem a imagem: a duplicação da área de trabalho do DirectX (pela GPU) ou o GDI.</summary>
internal sealed record VideoSource(string Name, Rectangle Bounds, int Adapter = -1, int Output = -1)
{
    public bool Dxgi => Adapter >= 0;

    /// <summary>Na ordem de preferência: DXGI quando a tela é encontrada nele, e o GDI, que sempre existe.</summary>
    public static IEnumerable<VideoSource> For(Screen screen)
    {
        var bounds = screen.Bounds;
        bounds.Width &= ~1;
        bounds.Height &= ~1;
        // Monitor girado (em pé): o DirectX entrega a imagem sem girar, e o FFmpeg (ddagrab) não a gira de volta; a
        // captura sairia com a medida e a orientação erradas. A do GDI já vem como a tela aparece.
        if (DisplayOutputs.Find(screen.DeviceName) is { Rotated: false } dxgi) yield return new VideoSource("DXGI", bounds, dxgi.Adapter, dxgi.Output);
        yield return new VideoSource("GDI", bounds);
    }
}

/// <summary>Em que placa e em que saída dela o DirectX enxerga cada tela, na mesma ordem que o FFmpeg usa.</summary>
internal static unsafe class DisplayOutputs
{
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop, Rotation;
        public IntPtr Monitor;
    }

    private static readonly Guid Factory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    private static IntPtr Method(IntPtr com, int slot) => (*(IntPtr**)com)[slot];
    private static void Release(IntPtr com) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Method(com, 2))(com);

    // DXGI_MODE_ROTATION: 0 = não informada, 1 = sem rotação, 2, 3 e 4 = girada 90°, 180° e 270°.
    public static (int Adapter, int Output, bool Rotated)? Find(string deviceName)
    {
        var raw = stackalloc byte[Marshal.SizeOf<OutputDesc>()];
        try
        {
            if (CreateDXGIFactory1(Factory1, out IntPtr factory) < 0) return null;
            try
            {
                // IDXGIFactory::EnumAdapters, IDXGIAdapter::EnumOutputs e IDXGIOutput::GetDesc ficam na posição 7 de cada interface.
                for (uint a = 0; a < 16; a++)
                {
                    IntPtr adapter;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Method(factory, 7))(factory, a, &adapter) < 0) break;
                    try
                    {
                        for (uint o = 0; o < 16; o++)
                        {
                            IntPtr output;
                            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Method(adapter, 7))(adapter, o, &output) < 0) break;
                            try
                            {
                                if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Method(output, 7))(output, raw) < 0) continue;
                                var desc = Marshal.PtrToStructure<OutputDesc>((IntPtr)raw);
                                if (desc.AttachedToDesktop != 0 && string.Equals(desc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                                    return ((int)a, (int)o, desc.Rotation > 1);
                            }
                            finally { Release(output); }
                        }
                    }
                    finally { Release(adapter); }
                }
                return null;
            }
            finally { Release(factory); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return null; }
    }
}

internal static class VideoEncoder
{
    private static readonly ConcurrentDictionary<string, string> Encoders = new();
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Vendor(string encoder) =>
        encoder.EndsWith("_nvenc") ? "NVIDIA" : encoder.EndsWith("_amf") ? "AMD" : encoder.EndsWith("_qsv") ? "Intel" : encoder;

    private static string PixelFormat(VideoOptions o, string encoder) => o.Chroma444 ? (encoder.EndsWith("_qsv") ? "vuyx" : "yuv444p") : "nv12";

    // A conversão de RGB para YUV é feita aqui, com a matriz BT.709 e faixa limitada declaradas, e o
    // decodificador desfaz com a mesma: as cores não dependem do que cada placa de vídeo supõe.
    internal static string Conversion(VideoOptions o, string encoder) =>
        $"scale=out_color_matrix=bt709:out_range=tv,format={PixelFormat(o, encoder)}";


    internal static List<string> CodecArguments(VideoOptions options, string encoder)
    {
        var o = options.Normalize();
        long bitrate = o.BitrateMbps * 1_000_000L;
        // O "+" recusa a troca automática de formato: um 4:4:4 que a placa não faz falha, em vez de virar 4:2:0 calado.
        var args = new List<string> { "-an", "-c:v", encoder, "-pix_fmt", "+" + PixelFormat(o, encoder),
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            // Sem quadros B (que esperam os seguintes) e com quadro-chave a cada 10 s: a conexão não perde dados.
            "-bf", "0", "-g", N(o.Fps * 10), "-b:v", N(bitrate), "-maxrate", N(bitrate),
            // A tela é lida no ritmo pedido mesmo parada (o tempo entre mudar e ser vista fica estável), mas, com a
            // qualidade mínima limitada, a imagem parada para de ser refinada e cada quadro igual vira poucos bytes:
            // menos de 0,1 Mbit/s com a tela parada, contra 3 a 10 Mbit/s sem o limite.
            "-qmin", "20",
            "-bufsize", N(o.UltraLowLatency ? bitrate / o.Fps : bitrate / 2) };
        if (encoder.EndsWith("_nvenc"))
        {
            args.AddRange(["-preset", o.UltraLowLatency ? "p1" : "p4", "-tune", o.UltraLowLatency ? "ull" : "ll",
                "-rc", "cbr", "-rc-lookahead", "0", "-zerolatency", "1", "-delay", "0"]);
            if (o.Chroma444) args.AddRange(["-profile:v", o.Codec == "h264" ? "high444p" : "rext"]);
        }
        else if (encoder.EndsWith("_amf"))
        {
            // Por padrão o FFmpeg deixa até 16 quadros dentro do codificador da AMD: quase 300 ms a 60 FPS.
            // Profundidade 1: o quadro sai do codificador sem esperar os seguintes.
            args.AddRange(["-usage", o.UltraLowLatency ? "ultralowlatency" : "lowlatency", "-quality", "speed", "-rc", "cbr",
                "-async_depth", "1", "-preanalysis", "0", "-filler_data", "0"]);
        }
        else
        {
            args.AddRange(["-preset", "veryfast", "-async_depth", "1", "-low_power", "0"]);
            if (o.Codec == "h264") args.AddRange(["-look_ahead", "0"]);
            if (o.Chroma444) args.AddRange(["-profile:v", "rext"]);
        }
        return args;
    }

    /// <summary>A primeira placa que codifica a combinação pedida, ou null. Lembra a resposta enquanto o Ponte estiver aberto.</summary>
    public static async Task<string?> FindAsync(VideoOptions o, CancellationToken ct)
    {
        string key = $"{o.Codec}/{o.Chroma444}/{o.UltraLowLatency}";
        if (Encoders.TryGetValue(key, out string? cached)) return cached.Length == 0 ? null : cached;
        await ProbeGate.WaitAsync(ct);
        try
        {
            if (Encoders.TryGetValue(key, out cached)) return cached.Length == 0 ? null : cached;
            string codec = o.Codec == "h265" ? "hevc" : "h264";
            foreach (string vendor in new[] { "nvenc", "amf", "qsv" })
            {
                // A AMD não codifica 4:4:4, nem a Intel em H.264.
                if (o.Chroma444 && (vendor == "amf" || (vendor == "qsv" && codec == "h264"))) continue;
                string encoder = codec + "_" + vendor;
                if (await ProbeAsync(o, encoder, ct)) { Encoders[key] = encoder; return encoder; }
            }
            Encoders[key] = "";
            return null;
        }
        finally { ProbeGate.Release(); }
    }

    private static async Task<bool> ProbeAsync(VideoOptions options, string encoder, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-frames:v", "3", "-vf", Conversion(options, encoder) };
        args.AddRange(CodecArguments(options, encoder));
        args.AddRange(["-f", "null", "-"]);
        using var proc = new VideoProcess(args);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await proc.Process.WaitForExitAsync(timeout.Token); return proc.Process.ExitCode == 0; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    internal static List<string> CaptureArguments(VideoSource source, VideoOptions o, string encoder)
    {
        var b = source.Bounds;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (source.Dxgi)
        {
            // A imagem desce da GPU uma vez; a conversão de cor fixa é feita aqui e a codificação volta para a GPU.
            args.AddRange(["-init_hw_device", $"d3d11va=tela:{source.Adapter}", "-filter_hw_device", "tela", "-filter_complex",
                $"ddagrab=output_idx={source.Output}:framerate={o.Fps}:draw_mouse=0:video_size={b.Width}x{b.Height},hwdownload,format=bgra,{Conversion(o, encoder)}[v]",
                "-map", "[v]"]);
        }
        else
        {
            args.AddRange(["-f", "gdigrab", "-draw_mouse", "0", "-framerate", N(o.Fps), "-offset_x", N(b.X), "-offset_y", N(b.Y),
                "-video_size", $"{b.Width}x{b.Height}", "-i", "desktop", "-vf", Conversion(o, encoder)]);
        }
        args.AddRange(CodecArguments(o, encoder));
        // O AVI vem pelo cano em blocos com o tamanho declarado: cada bloco é um quadro inteiro, que segue na hora.
        args.AddRange(["-fps_mode", "passthrough", "-f", "avi", "-flush_packets", "1", "pipe:1"]);
        return args;
    }

    /// <summary>
    /// Captura, codifica e entrega cada quadro inteiro a <paramref name="send"/> até ser cancelado. Falha com
    /// IOException quando o FFmpeg para. Sem fila própria: o limite é o cano do processo e a escrita na rede.
    /// </summary>
    public static async Task RunAsync(VideoSource source, VideoOptions o, string encoder, Func<byte[], Task> send, CancellationToken ct)
    {
        using var process = new VideoProcess(CaptureArguments(source, o, encoder));
        using var stop = ct.Register(process.Kill);
        var reader = new AviFrameReader(process.Process.StandardOutput.BaseStream);
        bool first = true;
        while (true)
        {
            byte[]? frame;
            try
            {
                var next = reader.NextAsync(ct);
                // Só o primeiro quadro tem prazo: depois, com a tela parada, pode passar muito tempo sem nenhum.
                frame = first ? await next.WaitAsync(TimeSpan.FromSeconds(10), ct) : await next;
            }
            catch (TimeoutException) { throw new IOException($"A captura {source.Name} não mandou imagem."); }
            ct.ThrowIfCancellationRequested();
            if (frame is null)
            {
                await Task.Delay(200, CancellationToken.None);
                throw new IOException($"A captura {source.Name} parou" + (process.Error.Length > 0 ? ": " + process.Error : "."));
            }
            first = false;
            await send(frame);
        }
    }
}

/// <summary>
/// Lê, do AVI que o FFmpeg escreve no cano, um quadro por vez. As listas (RIFF e LIST, que no cano vêm sem
/// tamanho certo) são só atravessadas; os blocos "##dc" são os quadros; o resto (cabeçalhos, índices) é pulado.
/// </summary>
internal sealed class AviFrameReader(Stream input)
{
    public const int MaxFrame = 16 * 1024 * 1024;
    private readonly byte[] _header = new byte[12];

    public async Task<byte[]?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            if (!await FillAsync(_header.AsMemory(0, 8), ct)) return null;
            string id = System.Text.Encoding.ASCII.GetString(_header, 0, 4);
            if (id is "RIFF" or "LIST")
            {
                if (!await FillAsync(_header.AsMemory(8, 4), ct)) return null;
                continue;
            }
            uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4));
            if (size > MaxFrame) throw new IOException("Quadro de vídeo grande demais.");
            var data = new byte[size];
            if (!await FillAsync(data, ct)) return null;
            if ((size & 1) != 0 && !await FillAsync(_header.AsMemory(0, 1), ct)) return null;
            if (size > 0 && id[2] == 'd' && id[3] is 'c' or 'b' && char.IsAsciiDigit(id[0]) && char.IsAsciiDigit(id[1])) return data;
        }
    }

    private async Task<bool> FillAsync(Memory<byte> buffer, CancellationToken ct)
    {
        for (int read = 0; read < buffer.Length;)
        {
            int n = await input.ReadAsync(buffer[read..], ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// Decodifica o vídeo num FFmpeg à parte, que devolve os quadros em BGRA. Os quadros vão para dois
/// bitmaps que se revezam: enquanto um aparece na tela, o outro recebe o próximo, sem alocar nada por quadro.
/// </summary>
internal sealed class VideoDecoder : IAsyncDisposable
{
    private readonly VideoProcess _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Threading.Channels.Channel<byte[]> _packets;
    private readonly Task _writer, _reader;
    private readonly Action<string> _failed;
    private readonly Bitmap[] _frames = new Bitmap[2];
    private readonly byte[][] _memory = new byte[2][];
    private readonly NamedPipeServerStream _frameStream;
    private readonly byte[] _yuv;
    private readonly int _width, _height;
    private readonly bool _chroma444;
    private int _failure;
    public int Id { get; }

    /// <param name="present">Mostra o bitmap; devolve falso se ele não serve mais (vídeo trocado).</param>
    public VideoDecoder(VideoStartMsg start, Func<Bitmap, bool> present, Action<string> failed)
    {
        if (start.Width < 16 || start.Height < 16 || start.Width > 8192 || start.Height > 8192 || (long)start.Width * start.Height > 16_777_216
            || start.Codec is not ("h264" or "h265") || start.Id < 1)
            throw new InvalidDataException("Configuração de vídeo inválida.");
        Id = start.Id;
        _failed = failed;
        int w = start.Width & ~1, h = start.Height & ~1, size = checked(w * h * 4);
        _width = w;
        _height = h;
        _chroma444 = start.Chroma444;
        // O quadro vem no formato do próprio vídeo (YUV, 3 MB em 1080p 4:2:0) e a conversão para RGB é feita aqui,
        // dividida entre os núcleos: no FFmpeg ela custava 12 ms de processador por quadro e 8 MB de cano.
        _yuv = GC.AllocateUninitializedArray<byte>(w * h + 2 * (_chroma444 ? w * h : w / 2 * (h / 2)), pinned: true);
        for (int i = 0; i < 2; i++)
        {
            // Memória fixa (não muda de lugar na coleta de lixo), lida direto pelo GDI+ como o conteúdo do bitmap.
            _memory[i] = GC.AllocateUninitializedArray<byte>(size, pinned: true);
            _frames[i] = new Bitmap(w, h, w * 4, PixelFormat.Format32bppRgb, Marshal.UnsafeAddrOfPinnedArrayElement(_memory[i], 0));
        }
        _packets = System.Threading.Channels.Channel.CreateBounded<byte[]>(start.UltraLowLatency ? 8 : 32);
        bool hevc = start.Codec == "h265";
        byte[] delimiter = hevc ? [0, 0, 0, 1, 0x46, 0x01, 0x50] : [0, 0, 0, 1, 0x09, 0xF0];
        // Os quadros prontos voltam por um pipe nomeado com buffer grande: pela saída padrão, que tem buffer de
        // 4 KB, eles não passavam de uns 40 por segundo.
        string pipe = "ponte-video-" + Guid.NewGuid().ToString("N");
        _frameStream = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 16 * 1024 * 1024, 4096);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin",
            // Sem análise prévia do vídeo: por padrão o FFmpeg junta 5 MB antes de mostrar o primeiro quadro, e esse
            // atraso não some mais. ("-fflags nobuffer" trava a saída com MPEG-TS vindo pelo cano.)
            "-flags", "low_delay", "-probesize", "32", "-analyzeduration", "0", "-fpsprobesize", "0",
            "-f", hevc ? "hevc" : "h264", "-raw_packet_size", "1048576", "-c:v", hevc ? "hevc" : "h264", "-i", "pipe:0", "-map", "0:v:0", "-an",
            "-vf", $"scale={w}:{h},format={(_chroma444 ? "yuv444p" : "yuv420p")}",
            // Uma thread só na saída: com várias, o FFmpeg segura cada quadro até o seguinte chegar (16 ms a mais a 60 FPS).
            "-fps_mode", "passthrough", "-threads", "1", "-f", "rawvideo", "-flush_packets", "1", "-y", $@"\\.\pipe\{pipe}" };
        try { _process = new VideoProcess(args); }
        catch { _frameStream.Dispose(); throw; }
        _writer = Task.Run(async () =>
        {
            try
            {
                var input = _process.Process.StandardInput.BaseStream;
                await foreach (byte[] packet in _packets.Reader.ReadAllAsync(_stop.Token))
                {
                    // O FFmpeg só entrega um quadro quando vê o começo do seguinte. Com a tela parada o seguinte pode
                    // demorar, então o delimitador que abriria o próximo quadro vai logo depois deste, no lugar do dele.
                    await input.WriteAsync(packet.AsMemory(AfterDelimiter(packet, hevc)), _stop.Token);
                    await input.WriteAsync(delimiter, _stop.Token);
                    await input.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Fail(ex.Message); }
        });
        _reader = Task.Run(async () =>
        {
            try
            {
                await _frameStream.WaitForConnectionAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
                // Sem prazo entre quadros: com a tela parada lá, nenhum quadro chega, e isso é normal.
                for (int next = 0; !_stop.IsCancellationRequested; next ^= 1)
                {
                    await _frameStream.ReadExactlyAsync(_yuv, _stop.Token);
                    ToBgra(_yuv, _memory[next], _width, _height, _chroma444);
                    if (!present(_frames[next])) return;
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException or TimeoutException)
            { Fail(_process.Error.Length > 0 ? _process.Error : ex.Message); }
        });
    }

    // YUV (BT.709, faixa limitada, como o outro lado converteu) para BGRA, em ponto fixo com 8 bits de fração.
    private static readonly int[] LumaTable = Table(i => 298 * (i - 16) + 128), RedV = Table(i => 459 * (i - 128)),
        GreenU = Table(i => -55 * (i - 128)), GreenV = Table(i => -136 * (i - 128)), BlueU = Table(i => 541 * (i - 128));

    private static int[] Table(Func<int, int> f) => Enumerable.Range(0, 256).Select(f).ToArray();

    /// <summary>Converte um quadro YUV planar (4:2:0 ou 4:4:4) em BGRA, em faixas de linhas paralelas.</summary>
    internal static unsafe void ToBgra(byte[] yuv, byte[] bgra, int w, int h, bool chroma444)
    {
        int cw = chroma444 ? w : w / 2, ch = chroma444 ? h : h / 2, shift = chroma444 ? 0 : 1;
        int uOffset = w * h, vOffset = uOffset + cw * ch;
        int bands = Math.Clamp(Environment.ProcessorCount, 1, 16);
        Parallel.For(0, bands, band =>
        {
            int y0 = h * band / bands, y1 = h * (band + 1) / bands;
            fixed (byte* src = yuv, dst = bgra)
            fixed (int* luma = LumaTable, rv = RedV, gu = GreenU, gv = GreenV, bu = BlueU)
            {
                for (int y = y0; y < y1; y++)
                {
                    byte* row = src + y * w, us = src + uOffset + (y >> shift) * cw, vs = src + vOffset + (y >> shift) * cw;
                    uint* outRow = (uint*)(dst + (long)y * w * 4);
                    for (int x = 0; x < w; x++)
                    {
                        int l = luma[row[x]], u = us[x >> shift], v = vs[x >> shift];
                        int r = (l + rv[v]) >> 8, g = (l + gu[u] + gv[v]) >> 8, b = (l + bu[u]) >> 8;
                        r = r < 0 ? 0 : r > 255 ? 255 : r;
                        g = g < 0 ? 0 : g > 255 ? 255 : g;
                        b = b < 0 ? 0 : b > 255 ? 255 : b;
                        outRow[x] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
                    }
                }
            }
        });
    }

    /// <summary>Onde o quadro começa depois do delimitador (AUD) inicial, se houver.</summary>
    internal static int AfterDelimiter(byte[] au, bool hevc)
    {
        int start = au.Length >= 4 && au[0] == 0 && au[1] == 0 && au[2] == 0 && au[3] == 1 ? 4
                  : au.Length >= 3 && au[0] == 0 && au[1] == 0 && au[2] == 1 ? 3 : -1;
        if (start < 0 || start >= au.Length) return 0;
        bool isAud = hevc ? ((au[start] >> 1) & 0x3F) == 35 : (au[start] & 0x1F) == 9;
        if (!isAud) return 0;
        for (int i = start + 1; i + 2 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1) return i > start + 1 && au[i - 1] == 0 ? i - 1 : i;
        }
        return au.Length;
    }

    /// <summary>Se o bitmap é um dos deste decodificador (e some com ele).</summary>
    public bool Owns(Bitmap? bitmap) => bitmap is not null && (ReferenceEquals(bitmap, _frames[0]) || ReferenceEquals(bitmap, _frames[1]));

    public async Task FeedAsync(byte[] packet, CancellationToken ct)
    {
        if (Volatile.Read(ref _failure) != 0) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await _packets.Writer.WriteAsync(packet, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { Fail("Este computador não deu conta de decodificar o vídeo a tempo."); }
    }

    private void Fail(string reason)
    {
        if (!_stop.IsCancellationRequested && Interlocked.Exchange(ref _failure, 1) == 0) _failed(reason);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _packets.Writer.TryComplete();
        _process.Kill();
        try { await Task.WhenAll(_reader, _writer).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
        catch (Exception ex) { ErrorLog.Write(ex); }
        _process.Dispose();
        _frameStream.Dispose();
        foreach (var frame in _frames) frame.Dispose();
    }
}
