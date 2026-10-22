using System.Diagnostics;
using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Core.Audio;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace HDMICaptain.Media.Audio;

/// <summary>
/// Card audio passthrough (spec §13.6, windows.md §5): the card's capture endpoint and the default output, both
/// WASAPI shared and event-driven at the engine's smallest period, joined by a ring buffer whose fill a drift
/// controller holds at the delay target by nudging the resample ratio. Each side runs on its own MMCSS
/// "Pro Audio" thread. Video never waits on audio: failures raise events and only this pipeline stops.
/// </summary>
public sealed class WasapiAudioPipeline : IDisposable
{
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const uint AudclntBufferFlagsSilent = 0x2;
    private const double BaseLatencyMs = 20; // ring fill kept on top of the user delay; absorbs capture packet jitter
    private const int RingSeconds = 2;

    private readonly string _captureId;
    private readonly AudioRingBuffer _ring;
    private readonly DriftController _drift;
    private readonly StreamResampler _resampler = new();
    private readonly object _threads = new();
    private Thread? _captureThread;
    private Thread? _renderThread;
    private volatile bool _running;
    private volatile bool _renderRestart;
    private volatile AudioSettings _settings = new();
    private volatile int _captureRate = 48_000;
    private DefaultDeviceListener? _listener;
    private float _peakL, _peakR;

    public WasapiAudioPipeline(string captureEndpointId)
    {
        _captureId = captureEndpointId;
        _ring = new AudioRingBuffer(48_000 * RingSeconds);
        _drift = new DriftController(48_000, BaseLatencyMs);
    }

    /// <summary>Delay, channel mode, volume and mute; read by the render thread every period.</summary>
    public AudioSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            _drift.TargetMs = BaseLatencyMs + Math.Clamp(value.DelayMs, 0, 500);
        }
    }

    /// <summary>Microphone privacy is off for desktop apps (E_ACCESSDENIED). Video keeps running.</summary>
    public event Action? AccessDenied;

    /// <summary>Capture stopped for good (card unplugged, endpoint gone): the HRESULT.</summary>
    public event Action<int>? Failed;

    public event Action<string>? Log;

    /// <summary>Peak level per channel since the last read, after volume (0…1). For meters.</summary>
    public (float Left, float Right) TakePeak()
    {
        var peak = (Volatile.Read(ref _peakL), Volatile.Read(ref _peakR));
        Volatile.Write(ref _peakL, 0);
        Volatile.Write(ref _peakR, 0);
        return peak;
    }

    /// <summary>Audio held between capture and render right now, in ms (the drift controller's smoothed fill).</summary>
    public double BufferedMs => _drift.SmoothedFillMs;

    public void Start()
    {
        lock (_threads) StartLocked();
    }

    private void StartLocked()
    {
        if (_running) return;
        _running = true;
        _ring.Clear();
        _resampler.Reset();
        _listener = DefaultDeviceListener.TryCreate(() => _renderRestart = true);
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "Audio capture", Priority = ThreadPriority.Highest };
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Audio render", Priority = ThreadPriority.Highest };
        _captureThread.Start();
        _renderThread.Start();
    }

    /// <summary>Safe to call more than once and from several threads (window close races a background dispose).</summary>
    public void Stop()
    {
        lock (_threads)
        {
            if (!_running) return;
            _running = false;
            _captureThread?.Join();
            _renderThread?.Join();
            _captureThread = _renderThread = null;
            _listener?.Dispose();
            _listener = null;
        }
    }

    public void Dispose() => Stop();

    // MARK: - Capture

    private unsafe void CaptureLoop()
    {
        var task = 0u;
        using var mmcss = PInvoke.AvSetMmThreadCharacteristics("Pro Audio", ref task);
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        Client? client = null;
        try
        {
            enumerator = AudioEndpoints.CreateEnumerator();
            device = AudioEndpoints.Open(enumerator, _captureId);
            client = Client.Open(device);
            var format = client.Format;
            // MS2109: reported 96 kHz mono is really 48 kHz stereo (docs/fixtures/ms2109-audio.json).
            var (actual, reinterpreted) = AudioFormatFix.Apply(new CaptureAudioFormat(format.Rate, format.Channels, 16));
            var channels = reinterpreted ? actual.Channels : format.Channels;
            _captureRate = reinterpreted ? actual.SampleRate : format.Rate;
            Log?.Invoke($"capture {format}{(reinterpreted ? " → MS2109 fix: 48000 Hz stereo" : "")}, period {client.PeriodFrames} frames");

            var capture = client.Service<IAudioCaptureClient>();
            var scratch = new float[48_000 * 2];
            client.Audio.Start();
            while (_running)
            {
                PInvoke.WaitForSingleObject(client.Event, 200);
                while (true)
                {
                    capture.GetNextPacketSize(out var packet);
                    if (packet == 0) break;
                    byte* data;
                    capture.GetBuffer(&data, out var frames, out var flags, null, null);
                    var count = (int)Math.Min(frames, (uint)(scratch.Length / 2));
                    if ((flags & AudclntBufferFlagsSilent) != 0)
                        Array.Clear(scratch, 0, count * 2);
                    else
                        count = SampleConvert.ToStereoFloat(new ReadOnlySpan<byte>(data, (int)frames * format.BlockAlign),
                            channels, format.Bits, format.IsFloat, scratch);
                    capture.ReleaseBuffer(frames);
                    _ring.Write(scratch.AsSpan(0, count * 2));
                }
            }
            client.Audio.Stop();
            Marshal.FinalReleaseComObject(capture);
        }
        catch (COMException e)
        {
            Log?.Invoke($"capture failed: 0x{e.HResult:X8}");
            if (e.HResult == unchecked((int)0x80070005)) AccessDenied?.Invoke();
            else Failed?.Invoke(e.HResult);
        }
        finally
        {
            client?.Dispose();
            if (device != null) Marshal.FinalReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    // MARK: - Render

    private void RenderLoop()
    {
        var task = 0u;
        using var mmcss = PInvoke.AvSetMmThreadCharacteristics("Pro Audio", ref task);
        while (_running)
        {
            _renderRestart = false;
            try
            {
                RenderTo();
            }
            catch (COMException e) when (e.HResult == AudclntEDeviceInvalidated || _renderRestart)
            {
                Log?.Invoke("output changed; reopening");
            }
            catch (COMException e)
            {
                Log?.Invoke($"render failed: 0x{e.HResult:X8}; retrying");
                Thread.Sleep(1000);
            }
        }
    }

    private unsafe void RenderTo()
    {
        var enumerator = AudioEndpoints.CreateEnumerator();
        IMMDevice? device = null;
        Client? client = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device);
            client = Client.Open(device);
            var format = client.Format;
            client.Audio.GetBufferSize(out var bufferFrames);
            Log?.Invoke($"render {format}, period {client.PeriodFrames} frames, buffer {bufferFrames}");
            var render = client.Service<IAudioRenderClient>();
            var scratch = new float[bufferFrames * 2];
            var clock = Stopwatch.StartNew();
            var lastLog = 0L;
            var primed = false;
            client.Audio.Start();
            while (_running && !_renderRestart)
            {
                PInvoke.WaitForSingleObject(client.Event, 200);
                client.Audio.GetCurrentPadding(out var padding);
                var frames = (int)(bufferFrames - padding);
                if (frames <= 0) continue;

                var settings = _settings;
                var elapsed = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var correction = _drift.Update(_ring.FillFrames, elapsed);
                var output = scratch.AsSpan(0, frames * 2);
                // Start once the ring holds the target, so the first periods don't underrun.
                primed |= _drift.SmoothedFillMs >= _drift.TargetMs * 0.9 || _ring.FillFrames * 1000.0 / _captureRate >= _drift.TargetMs;
                if (primed)
                {
                    _resampler.Process(output, (double)_captureRate / format.Rate * correction, _ring);
                    ChannelMapper.Apply(settings.ChannelMode, output);
                }
                else
                {
                    output.Clear();
                }

                var volume = settings.Muted ? 0f : settings.Volume;
                byte* data;
                render.GetBuffer((uint)frames, &data);
                SampleConvert.FromStereoFloat(output, new Span<byte>(data, frames * format.BlockAlign), format.Channels, format.Bits, format.IsFloat, volume);
                render.ReleaseBuffer((uint)frames, 0);

                var meter = AudioMeter.Measure(output);
                Volatile.Write(ref _peakL, Math.Max(Volatile.Read(ref _peakL), meter.PeakL * volume));
                Volatile.Write(ref _peakR, Math.Max(Volatile.Read(ref _peakR), meter.PeakR * volume));
#if DEBUG
                if (Environment.TickCount64 - lastLog > 1000)
                {
                    lastLog = Environment.TickCount64;
                    Log?.Invoke($"fill {_drift.SmoothedFillMs:F1} ms (target {_drift.TargetMs:F0}), ratio {correction:F6}, underflow {_ring.UnderflowFrames}, overflow {_ring.OverflowFrames}");
                }
#endif
            }
            client.Audio.Stop();
            Marshal.FinalReleaseComObject(render);
        }
        finally
        {
            client?.Dispose();
            if (device != null) Marshal.FinalReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    // MARK: - WASAPI client

    /// <summary>The endpoint's shared-mode mix format, reduced to what conversion needs.</summary>
    private readonly record struct MixFormat(int Rate, int Channels, int Bits, bool IsFloat, int BlockAlign)
    {
        public override string ToString() => $"{Rate} Hz {Channels} ch {Bits}-bit {(IsFloat ? "float" : "PCM")}";
    }

    /// <summary>An initialised IAudioClient(3) with its event; minimum engine period when the endpoint allows it.</summary>
    private sealed unsafe class Client : IDisposable
    {
        private const uint EventCallback = 0x00040000; // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
        private const ushort WaveFormatIeeeFloat = 3;
        private const ushort WaveFormatExtensible = 0xFFFE;
        private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00AA00389B71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

        private readonly SafeFileHandle _event;

        private Client(IAudioClient audio, MixFormat format, uint period, SafeFileHandle handle)
        {
            Audio = audio;
            Format = format;
            PeriodFrames = period;
            _event = handle;
        }

        public IAudioClient Audio { get; }
        public MixFormat Format { get; }
        public uint PeriodFrames { get; }
        public HANDLE Event => (HANDLE)_event.DangerousGetHandle();

        public static Client Open(IMMDevice device)
        {
            var audio = Activate(device);
            WAVEFORMATEX* mix;
            audio.GetMixFormat(&mix);
            try
            {
                var format = Describe(mix);
                uint period = 0;
                if (audio is IAudioClient3 audio3)
                {
                    try
                    {
                        audio3.GetSharedModeEnginePeriod(mix, out _, out _, out var minPeriod, out _);
                        audio3.InitializeSharedAudioStream(EventCallback, minPeriod, mix, null);
                        period = minPeriod;
                    }
                    catch (COMException)
                    {
                        // AUDCLNT_E_ENGINE_PERIODICITY_LOCKED and friends: a fresh client on the classic path.
                        Marshal.FinalReleaseComObject(audio);
                        audio = Activate(device);
                    }
                }
                if (period == 0)
                {
                    audio.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, EventCallback, 0, 0, mix, null);
                    long defaultPeriod;
                    audio.GetDevicePeriod(&defaultPeriod, null);
                    period = (uint)(defaultPeriod * format.Rate / 10_000_000);
                }
                var handle = PInvoke.CreateEvent(null, false, false, null);
                audio.SetEventHandle((HANDLE)handle.DangerousGetHandle());
                return new Client(audio, format, period, handle);
            }
            finally
            {
                PInvoke.CoTaskMemFree(mix);
            }
        }

        private static IAudioClient Activate(IMMDevice device)
        {
            var iid = typeof(IAudioClient3).GUID;
            try
            {
                device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out var client);
                return (IAudioClient)client;
            }
            catch (InvalidCastException)
            {
                iid = typeof(IAudioClient).GUID;
                device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out var client);
                return (IAudioClient)client;
            }
        }

        private static MixFormat Describe(WAVEFORMATEX* format)
        {
            var isFloat = format->wFormatTag == WaveFormatIeeeFloat
                || (format->wFormatTag == WaveFormatExtensible && ((WAVEFORMATEXTENSIBLE*)format)->SubFormat == IeeeFloatSubtype);
            return new MixFormat((int)format->nSamplesPerSec, format->nChannels, format->wBitsPerSample, isFloat, format->nBlockAlign);
        }

        public T Service<T>() where T : class
        {
            var iid = typeof(T).GUID;
            Audio.GetService(&iid, out var pointer);
            try
            {
                return (T)Marshal.GetObjectForIUnknown((nint)pointer);
            }
            finally
            {
                Marshal.Release((nint)pointer);
            }
        }

        public void Dispose()
        {
            Marshal.FinalReleaseComObject(Audio);
            _event.Dispose();
        }
    }

    /// <summary>Restarts the render side when the default output changes (headphones plugged in, user pick).</summary>
    private sealed class DefaultDeviceListener : IMMNotificationClient, IDisposable
    {
        private readonly IMMDeviceEnumerator _enumerator;
        private readonly Action _changed;

        private DefaultDeviceListener(IMMDeviceEnumerator enumerator, Action changed)
        {
            _enumerator = enumerator;
            _changed = changed;
        }

        public static DefaultDeviceListener? TryCreate(Action changed)
        {
            try
            {
                var listener = new DefaultDeviceListener(AudioEndpoints.CreateEnumerator(), changed);
                listener._enumerator.RegisterEndpointNotificationCallback(listener);
                return listener;
            }
            catch (COMException)
            {
                return null;
            }
        }

        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
        {
            if (flow == EDataFlow.eRender && role == ERole.eConsole) _changed();
        }

        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) { }
        public void OnDeviceAdded(PCWSTR pwstrDeviceId) { }
        public void OnDeviceRemoved(PCWSTR pwstrDeviceId) { }
        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key) { }

        public void Dispose()
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            Marshal.ReleaseComObject(_enumerator);
        }
    }
}
