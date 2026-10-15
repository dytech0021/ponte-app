using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Ponte;

/// <summary>
/// Som do computador controlado. Cada mensagem Audio se decodifica sozinha:
/// formato (1 byte: 1 = PCM de 16 bits, 2 = ADPCM IMA de 4 bits), canais (1 byte), amostras por segundo (int)
/// e os dados. No ADPCM, antes dos dados vem o estado de cada canal (amostra prevista em short, índice do
/// passo em 1 byte e 1 byte vago), e cada byte traz uma amostra de cada canal: esquerdo nos 4 bits baixos.
/// O ADPCM ocupa um quarto do PCM, com um chiado leve; é o padrão. O PCM vai na qualidade Alta.
/// </summary>
internal static class AudioCodec
{
    public const byte Pcm = 1, Adpcm = 2;
    public const int Rate = 48000, Channels = 2, HeaderSize = 6, MaxPayload = 1024 * 1024;

    private static readonly int[] IndexStep = { -1, -1, -1, -1, 2, 4, 6, 8 };
    private static readonly int[] Steps =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130,
        143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282,
        1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630,
        9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    };

    /// <summary>Estado do ADPCM de um canal: continua de uma mensagem para a seguinte.</summary>
    public struct State
    {
        public int Predicted, Index;
    }

    private static int EncodeSample(ref State s, int sample)
    {
        int step = Steps[s.Index], difference = sample - s.Predicted, code = 0;
        if (difference < 0)
        {
            code = 8;
            difference = -difference;
        }
        int change = step >> 3;
        if (difference >= step) { code |= 4; difference -= step; change += step; }
        if (difference >= step >> 1) { code |= 2; difference -= step >> 1; change += step >> 1; }
        if (difference >= step >> 2) { code |= 1; change += step >> 2; }
        Advance(ref s, code, change);
        return code;
    }

    private static int DecodeSample(ref State s, int code)
    {
        int step = Steps[s.Index], change = step >> 3;
        if ((code & 4) != 0) change += step;
        if ((code & 2) != 0) change += step >> 1;
        if ((code & 1) != 0) change += step >> 2;
        Advance(ref s, code, change);
        return s.Predicted;
    }

    private static void Advance(ref State s, int code, int change)
    {
        s.Predicted = Math.Clamp(s.Predicted + ((code & 8) != 0 ? -change : change), short.MinValue, short.MaxValue);
        s.Index = Math.Clamp(s.Index + IndexStep[code & 7], 0, Steps.Length - 1);
    }

    /// <param name="stereo">Amostras intercaladas, esquerda e direita.</param>
    /// <param name="state">Estado dos dois canais; é atualizado.</param>
    public static byte[] Encode(ReadOnlySpan<short> stereo, int rate, bool lossless, State[] state)
    {
        int frames = stereo.Length / Channels;
        var payload = new byte[HeaderSize + (lossless ? frames * Channels * 2 : Channels * 4 + frames)];
        payload[0] = lossless ? Pcm : Adpcm;
        payload[1] = Channels;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), rate);
        if (lossless)
        {
            MemoryMarshal.AsBytes(stereo[..(frames * Channels)]).CopyTo(payload.AsSpan(HeaderSize));
            return payload;
        }
        for (int c = 0; c < Channels; c++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(HeaderSize + c * 4), (short)state[c].Predicted);
            payload[HeaderSize + c * 4 + 2] = (byte)state[c].Index;
        }
        int at = HeaderSize + Channels * 4;
        for (int i = 0; i < frames; i++)
            payload[at + i] = (byte)(EncodeSample(ref state[0], stereo[i * 2]) | EncodeSample(ref state[1], stereo[i * 2 + 1]) << 4);
        return payload;
    }

    /// <summary>Amostras de 16 bits intercaladas, ou null se a mensagem não serve.</summary>
    public static short[]? Decode(byte[] p, out int rate, out int channels)
    {
        rate = channels = 0;
        if (p.Length < HeaderSize || p.Length > MaxPayload) return null;
        channels = p[1];
        rate = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(2));
        if (rate is < 8000 or > 192000) return null;

        if (p[0] == Pcm && channels is 1 or 2)
        {
            var samples = new short[(p.Length - HeaderSize) / (channels * 2) * channels];
            MemoryMarshal.Cast<byte, short>(p.AsSpan(HeaderSize, samples.Length * 2)).CopyTo(samples);
            return samples;
        }
        if (p[0] == Adpcm && channels == Channels && p.Length >= HeaderSize + Channels * 4)
        {
            var state = new State[Channels];
            for (int c = 0; c < Channels; c++)
            {
                state[c].Predicted = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(HeaderSize + c * 4));
                state[c].Index = Math.Min(p[HeaderSize + c * 4 + 2], (byte)(Steps.Length - 1));
            }
            int at = HeaderSize + Channels * 4, frames = p.Length - at;
            var samples = new short[frames * Channels];
            for (int i = 0; i < frames; i++)
            {
                samples[i * 2] = (short)DecodeSample(ref state[0], p[at + i] & 15);
                samples[i * 2 + 1] = (short)DecodeSample(ref state[1], p[at + i] >> 4);
            }
            return samples;
        }
        return null;
    }
}

/// <summary>
/// Captura o que o computador está tocando na saída de som padrão (não o microfone) e entrega em pedaços
/// de 20 ms, já no formato de envio. Enquanto nada toca, nada é entregue.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private readonly Action<byte[]> _deliver;
    private readonly Thread _thread;
    private readonly AudioCodec.State[] _state = new AudioCodec.State[AudioCodec.Channels];
    private readonly short[] _pending = new short[AudioCodec.Rate * AudioCodec.Channels];
    private int _count, _quietChunks;
    private volatile bool _stop;

    /// <summary>Sem compressão (qualidade Alta) em vez de ADPCM.</summary>
    public volatile bool Lossless;

    /// <summary>Vazio enquanto a captura funciona; senão, o motivo de não haver som.</summary>
    public string Problem { get; private set; } = "iniciando";

    // Transmissão de tela: o som vai bruto (16 bits, dois canais, 48 kHz), sem pausar no silêncio.
    private readonly Action<short[]>? _raw;

    public AudioCapture(Action<byte[]> deliver) : this(deliver, null) { }

    /// <summary>Entrega o som sem compressão e sem cortar os silêncios, em pedaços de 20 ms.</summary>
    public AudioCapture(Action<short[]> raw) : this(null, raw) { }

    private AudioCapture(Action<byte[]>? deliver, Action<short[]>? raw)
    {
        _deliver = deliver ?? (_ => { });
        _raw = raw;
        _thread = new Thread(Run) { IsBackground = true, Name = "ponte-som" };
        _thread.Start();
    }

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                CaptureDefaultOutput();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
            {
                Problem = ex.Message;
            }
            // Sem saída de som, ou ela mudou (um fone conectado, por exemplo): tenta de novo com a que for a padrão.
            for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(50);
        }
    }

    private void CaptureDefaultOutput()
    {
        var enumerator = (Native.IMMDeviceEnumerator)new Native.MMDeviceEnumerator();
        Native.IMMDevice? device = null;
        Native.IAudioClient? client = null;
        Native.IAudioCaptureClient? capture = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
            Marshal.ThrowExceptionForHR(device.GetId(out string endpointId));
            Guid clientId = typeof(Native.IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref clientId, 23, IntPtr.Zero, out object activated));
            client = (Native.IAudioClient)activated;

            // Primeiro pede o som já no formato de envio, com o Windows convertendo. Se ele recusar,
            // a captura vem no formato da mixagem e a conversão é feita aqui.
            var source = new SourceFormat(AudioCodec.Rate, AudioCodec.Channels, 16, false);
            const long bufferDuration = 2_000_000;   // 200 ms, em unidades de 100 ns
            IntPtr wanted = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WAVEFORMATEX>());
            int result;
            try
            {
                Marshal.StructureToPtr(Native.WAVEFORMATEX.Pcm16(AudioCodec.Rate, AudioCodec.Channels), wanted, false);
                result = client.Initialize(0, Native.AUDCLNT_STREAMFLAGS_LOOPBACK | Native.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                                              Native.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, bufferDuration, 0, wanted, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(wanted);
            }
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(client.GetMixFormat(out IntPtr mix));
                try
                {
                    source = SourceFormat.Read(mix);
                    Marshal.ThrowExceptionForHR(client.Initialize(0, Native.AUDCLNT_STREAMFLAGS_LOOPBACK, bufferDuration, 0, mix, IntPtr.Zero));
                }
                finally
                {
                    Native.CoTaskMemFree(mix);
                }
            }

            Guid captureId = typeof(Native.IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref captureId, out object service));
            capture = (Native.IAudioCaptureClient)service;
            Marshal.ThrowExceptionForHR(client.Start());
            Problem = "";
            _count = 0;

            int chunk = source.Rate / 50 * AudioCodec.Channels;
            long nextEndpointCheck = Environment.TickCount64 + 1000;
            while (!_stop)
            {
                Thread.Sleep(10);
                if (Environment.TickCount64 >= nextEndpointCheck)
                {
                    nextEndpointCheck = Environment.TickCount64 + 1000;
                    Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 0, out var currentDevice));
                    try
                    {
                        Marshal.ThrowExceptionForHR(currentDevice.GetId(out string currentId));
                        if (currentId != endpointId) { client.Stop(); return; }
                    }
                    finally { Marshal.ReleaseComObject(currentDevice); }
                }
                while (true)
                {
                    // Um erro aqui é a saída de som deixando de existir; quem chamou tenta de novo.
                    if (capture.GetNextPacketSize(out uint frames) < 0) return;
                    if (frames == 0) break;
                    if (capture.GetBuffer(out IntPtr data, out frames, out uint flags, out _, out _) < 0) return;
                    Append(source, data, (int)frames, (flags & Native.AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                    capture.ReleaseBuffer(frames);
                    while (_count >= chunk) Deliver(chunk, source.Rate);
                }
            }
            client.Stop();
        }
        finally
        {
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Formato em que a captura chega.</summary>
    private readonly record struct SourceFormat(int Rate, int Channels, int Bits, bool Float)
    {
        public static SourceFormat Read(IntPtr format)
        {
            var wave = Marshal.PtrToStructure<Native.WAVEFORMATEX>(format);
            int tag = wave.wFormatTag;
            // No formato "extensível" o tipo de verdade fica no começo de um identificador, depois do cabeçalho comum.
            if (tag == 0xFFFE && wave.cbSize >= 22) tag = Marshal.ReadInt32(format, 24);
            bool supported = (tag == 3 && wave.wBitsPerSample == 32) || (tag == 1 && wave.wBitsPerSample is 16 or 24 or 32);
            if (!supported || wave.nChannels == 0 || wave.nSamplesPerSec is < 8000 or > 192000)
                throw new NotSupportedException("A saída de som deste computador usa um formato que o Ponte não conhece.");
            return new SourceFormat((int)wave.nSamplesPerSec, wave.nChannels, wave.wBitsPerSample, tag == 3);
        }
    }

    // Acrescenta ao que está pendente, sempre em 16 bits e dois canais.
    private unsafe void Append(SourceFormat source, IntPtr data, int frames, bool silent)
    {
        frames = Math.Min(frames, (_pending.Length - _count) / AudioCodec.Channels);
        if (frames <= 0) return;
        var target = _pending.AsSpan(_count, frames * AudioCodec.Channels);
        _count += target.Length;
        if (silent || data == IntPtr.Zero)
        {
            target.Clear();
            return;
        }
        int bytes = source.Bits / 8, stride = bytes * source.Channels, right = source.Channels > 1 ? bytes : 0;
        byte* p = (byte*)data;
        for (int i = 0; i < frames; i++, p += stride)
        {
            target[i * 2] = Sample(p, source);
            target[i * 2 + 1] = Sample(p + right, source);
        }
    }

    private static unsafe short Sample(byte* p, SourceFormat source) => source switch
    {
        { Float: true } => (short)Math.Clamp(*(float*)p * 32767f, short.MinValue, short.MaxValue),
        { Bits: 16 } => *(short*)p,
        { Bits: 24 } => (short)(p[1] | (sbyte)p[2] << 8),
        _ => (short)(*(int*)p >> 16),
    };

    private void Deliver(int chunk, int rate)
    {
        var samples = _pending.AsSpan(0, chunk);
        if (_raw is not null)
        {
            // A transmissão quer 48 kHz; outra taxa (saída de som que o Windows não converte) fica de fora.
            if (rate == AudioCodec.Rate) _raw(samples.ToArray());
            _pending.AsSpan(chunk, _count - chunk).CopyTo(_pending);
            _count -= chunk;
            return;
        }
        bool quiet = true;
        foreach (short s in samples)
        {
            if (s is >= -1 and <= 1) continue;
            quiet = false;
            break;
        }
        // Passado um instante de silêncio, para de enviar até voltar a tocar alguma coisa.
        _quietChunks = quiet ? _quietChunks + 1 : 0;
        if (_quietChunks <= 10) _deliver(AudioCodec.Encode(samples, rate, Lossless, _state));
        _pending.AsSpan(chunk, _count - chunk).CopyTo(_pending);
        _count -= chunk;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1500);
    }
}

/// <summary>
/// Toca o som que chega. Guarda uma folga pequena antes de começar, para os pedaços que a rede atrasa
/// não virarem falhas, e descarta o que chegar quando o atraso acumulado passa do aceitável.
/// </summary>
internal sealed class AudioPlayer : IDisposable
{
    private const int LeadMs = 150, MaxQueuedMs = 600;
    private static readonly int HeaderSize = Marshal.SizeOf<Native.WAVEHDR>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<Native.WAVEHDR>(nameof(Native.WAVEHDR.dwFlags));

    private readonly Lock _gate = new();
    private readonly Queue<(IntPtr Block, int Bytes)> _queued = new();
    private IntPtr _device;
    private int _rate, _channels, _queuedBytes;
    private bool _disposed, _failed;

    /// <param name="samples">Amostras de 16 bits intercaladas.</param>
    public void Play(short[] samples, int rate, int channels)
    {
        lock (_gate)
        {
            if (_disposed || samples.Length == 0) return;
            if (_device == IntPtr.Zero || rate != _rate || channels != _channels)
            {
                Close();
                // Sem saída de som neste computador não há o que fazer; não adianta tentar a cada pedaço.
                if (_failed) return;
                var format = Native.WAVEFORMATEX.Pcm16(rate, channels);
                if (Native.waveOutOpen(out _device, Native.WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                {
                    _device = IntPtr.Zero;
                    _failed = true;
                    return;
                }
                _rate = rate;
                _channels = channels;
            }
            Reclaim();
            int bytesPerMs = _rate * _channels * 2 / 1000;
            if (_queuedBytes > MaxQueuedMs * bytesPerMs) return;
            if (_queuedBytes == 0) Write(new byte[LeadMs * bytesPerMs]);
            Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        }
    }

    // O cabeçalho e os dados vão num bloco só, fora da memória gerenciada, porque o Windows fica com o ponteiro.
    private void Write(ReadOnlySpan<byte> data)
    {
        IntPtr block = Marshal.AllocHGlobal(HeaderSize + data.Length);
        Marshal.StructureToPtr(new Native.WAVEHDR { lpData = block + HeaderSize, dwBufferLength = (uint)data.Length }, block, false);
        unsafe { data.CopyTo(new Span<byte>((void*)(block + HeaderSize), data.Length)); }
        if (Native.waveOutPrepareHeader(_device, block, HeaderSize) != 0 || Native.waveOutWrite(_device, block, HeaderSize) != 0)
        {
            Native.waveOutUnprepareHeader(_device, block, HeaderSize);
            Marshal.FreeHGlobal(block);
            return;
        }
        _queued.Enqueue((block, data.Length));
        _queuedBytes += data.Length;
    }

    // Libera os blocos que já tocaram.
    private void Reclaim()
    {
        while (_queued.TryPeek(out var first) && (Marshal.ReadInt32(first.Block, FlagsOffset) & Native.WHDR_DONE) != 0)
        {
            _queued.Dequeue();
            Native.waveOutUnprepareHeader(_device, first.Block, HeaderSize);
            Marshal.FreeHGlobal(first.Block);
            _queuedBytes -= first.Bytes;
        }
    }

    private void Close()
    {
        if (_device == IntPtr.Zero) return;
        Native.waveOutReset(_device);
        while (_queued.TryDequeue(out var item))
        {
            Native.waveOutUnprepareHeader(_device, item.Block, HeaderSize);
            Marshal.FreeHGlobal(item.Block);
        }
        _queuedBytes = 0;
        Native.waveOutClose(_device);
        _device = IntPtr.Zero;
    }

    public void Silence()
    {
        lock (_gate) Close();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Close();
        }
    }
}
