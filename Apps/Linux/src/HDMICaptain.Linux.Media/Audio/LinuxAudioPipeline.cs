using System.Diagnostics;
using HDMICaptain.Core;
using HDMICaptain.Core.Audio;

namespace HDMICaptain.Linux.Media.Audio;

/// <summary>Interleaved stereo float samples and the monotonic time (ns) of the first frame.</summary>
public delegate void AudioSamplesHandler(ReadOnlySpan<float> interleavedStereo, long timestampNs);

/// <summary>
/// Card audio passthrough on Linux (spec §14.6): a blocking capture backend and a blocking playback backend,
/// each on its own thread, joined by the shared ring buffer whose fill the drift controller holds at the delay
/// target by nudging the resample ratio (the WASAPI pipeline's design, task 091). Video never waits on audio:
/// failures raise <see cref="Failed"/> and only this pipeline stops.
/// </summary>
public sealed class LinuxAudioPipeline : IDisposable
{
    // Least ring fill on top of the user delay; raised to DriftController.MinimumTargetMs. WSLg's sink pulls in bursts: 40 ms there.
    private static readonly double BaseLatencyMs = IsWsl ? 40 : 20;
    private const int RingSeconds = 2;

    private readonly Func<IAudioCapture> _openCapture;
    private readonly Func<IAudioPlayback> _openPlayback;
    private readonly AudioRingBuffer _ring;
    private readonly DriftController _drift;
    private readonly StreamResampler _resampler = new();
    private readonly object _threads = new();
    private Thread? _captureThread, _renderThread;
    private volatile bool _running;
    private volatile AudioSettings _settings = new();
    private volatile int _captureRate = 48_000;
    private double _capturePacketMs = 10, _renderPeriodMs = 10; // Volatile access
    private long _lastCaptureTicks;
    private float _peakL, _peakR;

    public LinuxAudioPipeline(Func<IAudioCapture> openCapture, Func<IAudioPlayback> openPlayback)
    {
        _openCapture = openCapture;
        _openPlayback = openPlayback;
        _ring = new AudioRingBuffer(48_000 * RingSeconds);
        // WSLg's RDP sink runs ~600 ppm fast: settle 4× faster there (same damping), real desktops keep the tuned loop.
        _drift = new DriftController(48_000, BaseLatencyMs, IsWsl ? 4 : 1);
    }

    public AudioSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            UpdateTarget();
        }
    }

    private void UpdateTarget()
    {
        var floor = DriftController.MinimumTargetMs(Volatile.Read(ref _capturePacketMs), Volatile.Read(ref _renderPeriodMs));
        _drift.TargetMs = Math.Max(BaseLatencyMs, floor) + Math.Clamp(_settings.DelayMs, 0, 500);
    }

    /// <summary>Capture stopped for good (card gone, device busy): the errno (negative).</summary>
    public event Action<int>? Failed;

    public event Action<string>? Log;

    /// <summary>Captured stereo float at <see cref="CaptureSampleRate"/>, before delay and volume (recorders). Capture thread.</summary>
    public event AudioSamplesHandler? CapturedAudio;

    public int CaptureSampleRate => _captureRate;

    public static bool IsWsl => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"));

    public double BufferedMs => _drift.SmoothedFillMs;
    public double TargetMs => _drift.TargetMs;
    public long UnderflowFrames => _ring.UnderflowFrames;
    public long OverflowFrames => _ring.OverflowFrames;
    /// <summary>The last resample correction (1 + drift); ~1.00002 means the card runs 20 ppm slow.</summary>
    public double Correction { get; private set; } = 1;

    public (float Left, float Right) TakePeak()
    {
        var peak = (Volatile.Read(ref _peakL), Volatile.Read(ref _peakR));
        Volatile.Write(ref _peakL, 0);
        Volatile.Write(ref _peakR, 0);
        return peak;
    }

    public void Start()
    {
        lock (_threads)
        {
            if (_running) return;
            _running = true;
            _ring.Clear();
            _resampler.Reset();
            _drift.Reset();
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Audio capture", Priority = ThreadPriority.Highest };
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Audio render", Priority = ThreadPriority.Highest };
            _captureThread.Start();
            _renderThread.Start();
        }
    }

    public void Stop()
    {
        lock (_threads)
        {
            if (!_running) return;
            _running = false;
            _captureThread?.Join(2000);
            _renderThread?.Join(2000);
            _captureThread = _renderThread = null;
        }
    }

    public void Dispose() => Stop();

    private void CaptureLoop()
    {
        IAudioCapture capture;
        try
        {
            capture = _openCapture();
        }
        catch (AudioOpenException e)
        {
            Log?.Invoke($"capture open failed: {e.Message}");
            _running = false;
            Failed?.Invoke(e.Errno);
            return;
        }
        using (capture)
        {
            var format = capture.Format;
            // MS2109 on a kernel without the snd-usb-audio quirk: 96 kHz mono is really 48 kHz stereo (ms2109-audio.json).
            var (actual, reinterpreted) = AudioFormatFix.Apply(new CaptureAudioFormat(format.Rate, format.Channels, format.Bits));
            var channels = reinterpreted ? actual.Channels : format.Channels;
            _captureRate = reinterpreted ? actual.SampleRate : format.Rate;
            Log?.Invoke($"capture {capture.Description}: {format}{(reinterpreted ? " → MS2109 fix: 48000 Hz stereo" : "")}, period {capture.PeriodFrames} frames");
            Volatile.Write(ref _capturePacketMs, capture.PeriodFrames * 1000.0 / _captureRate);
            UpdateTarget();
            var raw = new byte[capture.PeriodFrames * format.BlockAlign * 2];
            var scratch = new float[capture.PeriodFrames * 4];
            while (_running)
            {
                var frames = capture.Read(raw);
                if (frames < 0)
                {
                    Log?.Invoke($"capture failed: {Capture.Errno.Name(frames)}");
                    Failed?.Invoke(frames);
                    break;
                }
                if (frames == 0) continue;
                var count = SampleConvert.ToStereoFloat(raw.AsSpan(0, frames * format.BlockAlign), channels, format.Bits, format.IsFloat, scratch);
                var now = Stopwatch.GetTimestamp();
                CapturedAudio?.Invoke(scratch.AsSpan(0, count * 2), Render.GlesRenderer.MonotonicNs - (long)(count * 1e9 / _captureRate));
                _ring.Write(scratch.AsSpan(0, count * 2));
                Volatile.Write(ref _lastCaptureTicks, now);
            }
        }
    }

    private void RenderLoop()
    {
        while (_running)
        {
            IAudioPlayback playback;
            try
            {
                playback = _openPlayback();
            }
            catch (AudioOpenException e)
            {
                Log?.Invoke($"playback open failed: {e.Message}; retrying");
                Thread.Sleep(1000);
                continue;
            }
            using (playback)
            {
                Log?.Invoke($"render {playback.Description}");
                Volatile.Write(ref _renderPeriodMs, playback.PeriodFrames * 1000.0 / playback.Rate);
                UpdateTarget();
                var output = new float[playback.PeriodFrames * 2];
                var clock = Stopwatch.StartNew();
                var primed = false;
                var lastLog = Environment.TickCount64;
                while (_running)
                {
                    var settings = _settings;
                    var elapsed = clock.Elapsed.TotalSeconds;
                    clock.Restart();
                    var fill = _ring.FillFrames;
                    var sincePacketMs = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCaptureTicks)).TotalMilliseconds;
                    var correction = _drift.Update(DriftController.PhaseFreeFillFrames(fill, sincePacketMs, Volatile.Read(ref _capturePacketMs), _captureRate), elapsed);
                    Correction = correction;
                    // Start once the ring holds the target, so the first periods don't underrun.
                    primed |= _ring.FillFrames * 1000.0 / _captureRate >= _drift.TargetMs;
                    if (primed)
                    {
                        _resampler.Process(output, (double)_captureRate / playback.Rate * correction, _ring);
                        ChannelMapper.Apply(settings.ChannelMode, output);
                    }
                    else
                    {
                        Array.Clear(output);
                    }
                    var volume = settings.Muted ? 0f : settings.Volume;
                    if (volume != 1f) for (var i = 0; i < output.Length; i++) output[i] *= volume;
                    var meter = AudioMeter.Measure(output);
                    Volatile.Write(ref _peakL, Math.Max(Volatile.Read(ref _peakL), meter.PeakL));
                    Volatile.Write(ref _peakR, Math.Max(Volatile.Read(ref _peakR), meter.PeakR));
                    if (playback.Write(output) < 0)
                    {
                        Log?.Invoke("playback write failed; reopening");
                        break;
                    }
#if DEBUG
                    if (Environment.TickCount64 - lastLog > 5000)
                    {
                        lastLog = Environment.TickCount64;
                        Log?.Invoke($"fill {_drift.SmoothedFillMs:F1} ms (target {_drift.TargetMs:F0}), ratio {correction:F6}, underflow {_ring.UnderflowFrames}, overflow {_ring.OverflowFrames}");
                    }
#endif
                }
            }
        }
    }
}

/// <summary>A backend could not be opened: errno (negative) and a readable reason.</summary>
public sealed class AudioOpenException(int errno, string message) : Exception(message)
{
    public int Errno { get; } = errno;
}
