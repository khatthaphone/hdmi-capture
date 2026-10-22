using HDMICaptain.Media.Audio;
using HDMICaptain.Media.Recording;
using HDMICaptain.Media.Render;
using Vortice.Direct3D11;

namespace HDMICaptain.App.Recording;

/// <summary>
/// Record and Save Recent for the main window (task 100). Feeds the encoders from the renderer's clean pass-1
/// texture and the card's captured audio, so the control bar, guides and diagnostics never reach a file.
/// The audio pipeline is replaced whenever the card changes, so it is re-attached through <see cref="AttachAudio"/>.
/// </summary>
public sealed class RecordingController : IDisposable
{
    /// <summary>Save Recent keeps this much in memory (about 65 MB a minute at 1080p60), enough for its longest span.</summary>
    public static readonly TimeSpan KeepRecent = TimeSpan.FromSeconds(90);

    public static readonly (string Title, TimeSpan Span)[] RecentSpans =
    [
        ("Last 15 Seconds", TimeSpan.FromSeconds(15)),
        ("Last 30 Seconds", TimeSpan.FromSeconds(30)),
        ("Last 1 Minute", TimeSpan.FromMinutes(1)),
    ];

    private readonly D3DRenderer _renderer;
    private WasapiAudioPipeline? _audio;
    private Recorder? _recorder;
    private RollingRecorder? _rolling;

    public RecordingController(D3DRenderer renderer)
    {
        _renderer = renderer;
        _renderer.SourceRendered += OnVideo;
    }

    /// <summary>A recorder stopped on its own (encoder or device error): message for the toast. Any thread.</summary>
    public event Action<string>? Failed;

    public bool IsRecording => _recorder?.IsRecording == true;

    public TimeSpan RecordingDuration => _recorder?.Duration ?? TimeSpan.Zero;

    public bool IsKeepingRecent => _rolling != null;

    public TimeSpan RecentBuffered => _rolling?.BufferedDuration ?? TimeSpan.Zero;

    /// <summary>The audio the files get (null: video only). Safe to call while recording; later samples use it.</summary>
    public void AttachAudio(WasapiAudioPipeline? audio)
    {
        if (_audio != null) _audio.CapturedAudio -= OnAudio;
        _audio = audio;
        if (_audio != null) _audio.CapturedAudio += OnAudio;
    }

    public void StartRecording(double frameRate)
    {
        if (IsRecording) return;
        var recorder = new Recorder();
        recorder.Failed += message => Failed?.Invoke(message);
        recorder.Start(RecordingPaths.NewFilePath(), Options(frameRate));
        _recorder = recorder;
    }

    /// <summary>Finishes the file; its path, or null when nothing was written.</summary>
    public async Task<string?> StopRecordingAsync()
    {
        var recorder = Interlocked.Exchange(ref _recorder, null);
        return recorder is null ? null : await recorder.StopAsync();
    }

    public void StartKeepingRecent(double frameRate)
    {
        if (_rolling != null) return;
        var rolling = new RollingRecorder(KeepRecent, Options(frameRate));
        rolling.Failed += message => Failed?.Invoke(message);
        rolling.Start();
        _rolling = rolling;
    }

    public async Task StopKeepingRecentAsync()
    {
        var rolling = Interlocked.Exchange(ref _rolling, null);
        if (rolling != null) await rolling.StopAsync();
    }

    /// <summary>Writes the buffered <paramref name="span"/> to a new file without re-encoding.</summary>
    public Task<SaveResult> SaveRecentAsync(TimeSpan span) =>
        _rolling?.SaveAsync(span, RecordingPaths.NewFilePath()) ?? throw new InvalidOperationException("Keep Recent Footage is off.");

    private RecorderOptions Options(double frameRate) => new()
    {
        FrameRate = frameRate > 1 ? frameRate : 60,
        IncludeAudio = _audio != null,
        AudioSampleRate = _audio?.CaptureSampleRate ?? 48_000,
    };

    // Render thread (inside the renderer's lock) and WASAPI capture thread: hand straight to the encoders.
    private void OnVideo(ID3D11Texture2D texture, long timestamp)
    {
        _recorder?.WriteVideo(texture, timestamp);
        _rolling?.WriteVideo(texture, timestamp);
    }

    private void OnAudio(ReadOnlySpan<float> samples, long timestamp)
    {
        _recorder?.WriteAudio(samples, timestamp);
        _rolling?.WriteAudio(samples, timestamp);
    }

    public void Dispose()
    {
        _renderer.SourceRendered -= OnVideo;
        AttachAudio(null);
        StopRecordingAsync().GetAwaiter().GetResult();
        StopKeepingRecentAsync().GetAwaiter().GetResult();
    }
}
