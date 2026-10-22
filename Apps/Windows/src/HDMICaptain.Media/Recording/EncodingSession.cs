using HDMICaptain.Media.Capture;
using Vortice.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>Where a session's encoded samples go. Called on the encoder threads; the sink owns each sample.</summary>
internal interface IEncodedSink
{
    void OnVideo(IMFSample sample, long time, long duration, bool keyframe);
    void OnAudio(IMFSample sample, long time, long duration);
    void OnFailed(string message);
}

/// <summary>
/// The input half shared by <see cref="Recorder"/> and <see cref="RollingRecorder"/>: GPU NV12 conversion into a
/// texture pool, the video and audio encoder threads, and the timeline. Times are QPC (100 ns) minus the first
/// video frame's, so audio before the first frame is trimmed and both tracks start at zero.
/// </summary>
internal sealed class EncodingSession : IDisposable
{
    private const int PoolSize = 8;
    private const long AudioResyncThreshold = 400_000; // 40 ms: a capture gap; jump the audio clock forward

    private readonly RecorderOptions _options;
    private readonly IEncodedSink _sink;
    private readonly object _videoGate = new();
    private readonly object _audioGate = new();
    private Nv12Converter? _converter;
    private VideoEncoder? _video;
    private AudioEncoder? _audio;
    private nint _devicePointer;
    private nint _lastSource;
    private long _origin;
    private volatile bool _hasOrigin;
    private long _lastVideoTime = -1;
    private long _audioBase;
    private long _audioFrames = -1;
    private volatile bool _stopped;
    private int _failed;
    private long _frames;
    private long _dropped;
    private bool _started;

    public EncodingSession(RecorderOptions options, IEncodedSink sink)
    {
        _options = options;
        _sink = sink;
    }

    public long FramesEncoded => Interlocked.Read(ref _frames);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public string? EncoderName => _video?.Name;
    public bool IsHardwareEncoder => _video?.IsHardware == true;
    public int VideoBitrate => _video?.Bitrate ?? 0;
    public (int Width, int Height) Size => _converter is { } c ? (c.Width, c.Height) : default;
    public bool IsFailed => Volatile.Read(ref _failed) != 0;

    /// <summary>The encoder's output type with parameter sets; set before the first OnVideo call.</summary>
    public IMFMediaType? VideoType => _video?.OutputType;

    public void Start()
    {
        MediaFoundation.Startup();
        _started = true;
        if (_options.IncludeAudio)
        {
            _audio = new AudioEncoder(_options.AudioSampleRate, (s, t, d, _) => _sink.OnAudio(s, t, d), Fail);
            _audio.Start();
        }
    }

    /// <summary>The AAC output type, waiting briefly for the audio encoder to come up; null without audio.</summary>
    public IMFMediaType? WaitAudioType()
    {
        if (_audio == null) return null;
        _audio.Ready.Wait(TimeSpan.FromSeconds(2));
        return _audio.OutputType;
    }

    public void WriteVideo(ID3D11Texture2D source, long timestamp)
    {
        lock (_videoGate)
        {
            if (_stopped || IsFailed) return;
            try
            {
                if (_converter == null)
                {
                    var device = source.Device; // the converter keeps this reference
                    var desc = source.Description;
                    var (w, h) = _options.Size ?? ((int)desc.Width, (int)desc.Height);
                    _converter = new Nv12Converter(device, w & ~1, h & ~1, PoolSize);
                    _devicePointer = device.NativePointer;
                    _lastSource = source.NativePointer;
                    _video = new VideoEncoder(_converter, _options, _sink.OnVideo, Fail);
                    _video.Start();
                    _origin = timestamp;
                    _hasOrigin = true;
                }
                else if (source.NativePointer != _lastSource)
                {
                    // The renderer made a new source texture (size change or device rebuild).
                    using var device = source.Device;
                    if (device.NativePointer != _devicePointer)
                    {
                        Fail(new InvalidOperationException("The render device changed (GPU reset or the window moved to another GPU)."));
                        return;
                    }
                    _lastSource = source.NativePointer;
                }

                var time = timestamp - _origin;
                if (time <= _lastVideoTime) return;
                var slot = _converter.Convert(source, time, wait: !_options.DropWhenBusy);
                if (slot == null)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }
                _lastVideoTime = time;
                Interlocked.Increment(ref _frames);
                _video!.Enqueue(slot);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }
    }

    public void WriteAudio(ReadOnlySpan<float> stereo, long timestamp)
    {
        lock (_audioGate)
        {
            if (_stopped || IsFailed || _audio == null || !_hasOrigin) return;
            var rate = _options.AudioSampleRate;
            var frames = stereo.Length / AudioEncoder.Channels;
            var time = timestamp - _origin;
            var skip = 0;
            if (time < 0)
            {
                skip = (int)Math.Min(frames, (-time * rate + 9_999_999) / 10_000_000);
                time += skip * 10_000_000L / rate;
            }
            if (skip >= frames) return;
            var expected = _audioBase + _audioFrames * 10_000_000 / rate;
            if (_audioFrames < 0 || time - expected > AudioResyncThreshold)
            {
                _audioBase = time;
                _audioFrames = 0;
            }
            var stamped = _audioBase + _audioFrames * 10_000_000 / rate;
            _audioFrames += frames - skip;
            _audio.Enqueue(stereo[(skip * AudioEncoder.Channels)..], stamped);
        }
    }

    /// <summary>Stops taking input and drains both encoders (everything queued reaches the sink).</summary>
    public Task StopAsync()
    {
        lock (_videoGate)
            lock (_audioGate)
                _stopped = true;
        return Task.Run(() =>
        {
            _video?.Complete();
            _audio?.Complete();
            _video?.Join();
            _audio?.Join();
        });
    }

    private void Fail(Exception e)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        var message = e is System.Runtime.InteropServices.COMException com ? $"{e.Message} ({MfErrors.Name(com.HResult)})" : e.Message;
        _sink.OnFailed(message);
    }

    public void Dispose()
    {
        _converter?.Dispose();
        _converter = null;
        MfInterop.Release(_video?.OutputType);
        MfInterop.Release(_audio?.OutputType);
        if (_started) MediaFoundation.Shutdown();
        _started = false;
    }
}
