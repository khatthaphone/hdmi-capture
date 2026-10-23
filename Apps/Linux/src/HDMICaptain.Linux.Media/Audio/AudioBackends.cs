using System.Runtime.InteropServices;

namespace HDMICaptain.Linux.Media.Audio;

/// <summary>Interleaved PCM as a capture backend delivers it.</summary>
public readonly record struct PcmFormat(int Rate, int Channels, int Bits, bool IsFloat)
{
    public int BlockAlign => Channels * Bits / 8;
    public override string ToString() => $"{Rate} Hz {Channels} ch {Bits}-bit {(IsFloat ? "float" : "PCM")}";
}

/// <summary>A blocking audio source: the card through ALSA or PulseAudio, or a generator in tests.</summary>
public interface IAudioCapture : IDisposable
{
    PcmFormat Format { get; }
    /// <summary>Frames per read (the packet the drift floor accounts for).</summary>
    int PeriodFrames { get; }
    string Description { get; }
    /// <summary>Blocks for up to one period; returns frames read, or -errno (ENODEV: the card went away).</summary>
    int Read(Span<byte> buffer);
}

/// <summary>A blocking audio sink. Write blocks while the server's buffer is full, which paces the render loop.</summary>
public interface IAudioPlayback : IDisposable
{
    int Rate { get; }
    int PeriodFrames { get; }
    string Description { get; }
    /// <summary>Interleaved stereo float32. Returns 0 or -errno.</summary>
    int Write(ReadOnlySpan<float> stereo);
}

/// <summary>ALSA capture (libasound): the card's own PCM, for WSL and wherever no sound server holds it.</summary>
public sealed unsafe partial class AlsaCapture : IAudioCapture
{
    private const string Lib = "libasound.so.2";
    private const int StreamCapture = 1, FormatS16Le = 2, AccessRwInterleaved = 3;
    private nint _pcm;

    private AlsaCapture(nint pcm, PcmFormat format, int period, string name)
    {
        _pcm = pcm;
        Format = format;
        PeriodFrames = period;
        Description = $"ALSA {name}";
    }

    public PcmFormat Format { get; }
    public int PeriodFrames { get; }
    public string Description { get; }

    /// <summary>Opens <paramref name="device"/> (e.g. plughw:1,0) as 16-bit stereo at 48 kHz with 10 ms periods. Returns -errno on failure.</summary>
    public static int TryOpen(string device, out AlsaCapture? capture, int rate = 48_000, int channels = 2)
    {
        capture = null;
        nint pcm;
        var r = snd_pcm_open(&pcm, device, StreamCapture, 0);
        if (r < 0) return r;
        // 40 ms buffer: four 10 ms periods. soft_resample lets plughw convert a card that isn't 48 kHz.
        r = snd_pcm_set_params(pcm, FormatS16Le, AccessRwInterleaved, (uint)channels, (uint)rate, 1, 40_000);
        if (r < 0)
        {
            snd_pcm_close(pcm);
            return r;
        }
        nuint buffer, period;
        snd_pcm_get_params(pcm, &buffer, &period);
        capture = new AlsaCapture(pcm, new PcmFormat(rate, channels, 16, false), (int)period, device);
        return 0;
    }

    public int Read(Span<byte> buffer)
    {
        var frames = Math.Min(PeriodFrames, buffer.Length / Format.BlockAlign);
        fixed (byte* p = buffer)
        {
            var n = snd_pcm_readi(_pcm, p, (nuint)frames);
            if (n >= 0) return (int)n;
            // Overrun (-EPIPE) or resume (-ESTRPIPE): recover and report no data; anything else is fatal.
            var r = snd_pcm_recover(_pcm, (int)n, 1);
            return r < 0 ? r : 0;
        }
    }

    public void Dispose()
    {
        if (_pcm != 0) snd_pcm_close(_pcm);
        _pcm = 0;
    }

    public static string Error(int errno) => Marshal.PtrToStringUTF8(snd_strerror(errno)) ?? $"error {errno}";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int snd_pcm_open(nint* pcm, string name, int stream, int mode);
    [LibraryImport(Lib)] private static partial int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate, int softResample, uint latencyUs);
    [LibraryImport(Lib)] private static partial int snd_pcm_get_params(nint pcm, nuint* bufferSize, nuint* periodSize);
    [LibraryImport(Lib)] private static partial nint snd_pcm_readi(nint pcm, void* buffer, nuint frames);
    [LibraryImport(Lib)] private static partial int snd_pcm_recover(nint pcm, int err, int silent);
    [LibraryImport(Lib)] private static partial int snd_pcm_close(nint pcm);
    [LibraryImport(Lib)] private static partial nint snd_strerror(int errnum);
}

/// <summary>PulseAudio simple API (libpulse-simple): pipewire-pulse on desktops, WSLg's server in WSL.</summary>
public static unsafe partial class PulseSimple
{
    private const string Lib = "libpulse-simple.so.0";
    private const string Core = "libpulse.so.0";
    public const int Playback = 1, Record = 2;
    public const int S16Le = 3, Float32Le = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr
    {
        public uint MaxLength, TLength, PreBuf, MinReq, FragSize;
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pa_simple_new(string? server, string name, int dir, string? dev, string streamName, SampleSpec* ss, void* map, BufferAttr* attr, int* error);
    [LibraryImport(Lib)] public static partial int pa_simple_write(nint s, void* data, nuint bytes, int* error);
    [LibraryImport(Lib)] public static partial int pa_simple_read(nint s, void* data, nuint bytes, int* error);
    [LibraryImport(Lib)] public static partial ulong pa_simple_get_latency(nint s, int* error);
    [LibraryImport(Lib)] public static partial void pa_simple_free(nint s);
    [LibraryImport(Core)] private static partial nint pa_strerror(int error);

    public static string Error(int error) => Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"error {error}";
}

/// <summary>Card capture through the sound server (when it owns the card: PipeWire desktops).</summary>
public sealed unsafe class PulseCapture : IAudioCapture
{
    private nint _s;

    private PulseCapture(nint s, string source)
    {
        _s = s;
        Description = $"Pulse source {source}";
    }

    public PcmFormat Format { get; } = new(48_000, 2, 16, false);
    public int PeriodFrames => 480;
    public string Description { get; }

    public static int TryOpen(string source, out PulseCapture? capture)
    {
        capture = null;
        var spec = new PulseSimple.SampleSpec { Format = PulseSimple.S16Le, Rate = 48_000, Channels = 2 };
        var attr = new PulseSimple.BufferAttr { MaxLength = uint.MaxValue, TLength = uint.MaxValue, PreBuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = 480 * 4 };
        int error;
        var s = PulseSimple.pa_simple_new(null, "HDMI Captain", PulseSimple.Record, source, "HDMI input", &spec, null, &attr, &error);
        if (s == 0) return -Math.Abs(error);
        capture = new PulseCapture(s, source);
        return 0;
    }

    public int Read(Span<byte> buffer)
    {
        var bytes = Math.Min(PeriodFrames * Format.BlockAlign, buffer.Length);
        int error;
        fixed (byte* p = buffer)
            if (PulseSimple.pa_simple_read(_s, p, (nuint)bytes, &error) < 0) return -Capture.Errno.EIO;
        return bytes / Format.BlockAlign;
    }

    public void Dispose()
    {
        if (_s != 0) PulseSimple.pa_simple_free(_s);
        _s = 0;
    }
}

/// <summary>Playback to the default sink, float32 stereo, ~10 ms writes into a ~20 ms server buffer.</summary>
public sealed unsafe class PulsePlayback : IAudioPlayback
{
    private nint _s;

    private PulsePlayback(nint s, int rate, int period)
    {
        _s = s;
        Rate = rate;
        PeriodFrames = period;
    }

    public int Rate { get; }
    public int PeriodFrames { get; }
    public string Description => $"Pulse default sink, {Rate} Hz, {PeriodFrames}-frame writes";

    /// <summary>Server-side latency right now (µs), for the stats HUD.</summary>
    public ulong LatencyUs
    {
        get
        {
            int error;
            return PulseSimple.pa_simple_get_latency(_s, &error);
        }
    }

    public static int TryOpen(out PulsePlayback? playback, int rate = 48_000, double bufferMs = 20, double periodMs = 10)
    {
        playback = null;
        var spec = new PulseSimple.SampleSpec { Format = PulseSimple.Float32Le, Rate = (uint)rate, Channels = 2 };
        var period = (int)(rate * periodMs / 1000);
        var attr = new PulseSimple.BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = (uint)(rate * bufferMs / 1000) * 8,
            PreBuf = uint.MaxValue,
            MinReq = (uint)period * 8,
            FragSize = uint.MaxValue,
        };
        int error;
        var s = PulseSimple.pa_simple_new(null, "HDMI Captain", PulseSimple.Playback, null, "HDMI audio", &spec, null, &attr, &error);
        if (s == 0) return -Math.Abs(error);
        playback = new PulsePlayback(s, rate, period);
        return 0;
    }

    public int Write(ReadOnlySpan<float> stereo)
    {
        int error;
        fixed (float* p = stereo)
            return PulseSimple.pa_simple_write(_s, p, (nuint)(stereo.Length * sizeof(float)), &error) < 0 ? -Capture.Errno.EIO : 0;
    }

    public void Dispose()
    {
        if (_s != 0) PulseSimple.pa_simple_free(_s);
        _s = 0;
    }
}

/// <summary>A 1 kHz tone at 48 kHz stereo, paced like a card (for the pipeline test without hardware).</summary>
public sealed class ToneCapture : IAudioCapture
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly double _ppm;
    private long _produced;
    private double _phase;

    /// <param name="clockPpm">How fast this "card" clock runs against the system clock (drift to correct).</param>
    public ToneCapture(double clockPpm = 0) => _ppm = clockPpm;

    public PcmFormat Format { get; } = new(48_000, 2, 16, false);
    public int PeriodFrames => 480;
    public string Description => $"tone ({_ppm:+0;-0} ppm)";

    public int Read(Span<byte> buffer)
    {
        // Wait until the card clock has produced the next packet.
        var due = (_produced + PeriodFrames) / (48_000 * (1 + _ppm / 1e6));
        var wait = due - _clock.Elapsed.TotalSeconds;
        if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer);
        for (var i = 0; i < PeriodFrames; i++)
        {
            var v = (short)(Math.Sin(_phase) * 8000);
            _phase += 2 * Math.PI * 1000 / 48_000;
            samples[2 * i] = v;
            samples[2 * i + 1] = v;
        }
        _produced += PeriodFrames;
        return PeriodFrames;
    }

    public void Dispose() { }
}
