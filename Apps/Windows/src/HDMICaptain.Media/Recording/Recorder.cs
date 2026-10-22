using HDMICaptain.Media.Capture;
using Vortice.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>
/// Records the clean feed (spec §13, task 100): the renderer's pass-1 texture, never UI, plus the card's audio,
/// to a fragmented MP4 (H.264/HEVC + AAC) through our own encoder MFTs and a passthrough Sink Writer.
/// Wiring: <c>renderer.SourceRendered += recorder.WriteVideo</c> and <c>audio.CapturedAudio += recorder.WriteAudio</c>,
/// then <see cref="Start"/> … <see cref="StopAsync"/>. When the card is unplugged frames simply stop; the file is
/// finalized on stop and, being fragmented, is playable up to the last fragment even if the app dies.
/// </summary>
public sealed class Recorder : IAsyncDisposable, IEncodedSink
{
    private const int MaxPendingAudio = 500;

    private readonly object _gate = new();
    private readonly List<IMFSample> _pendingAudio = [];
    private volatile EncodingSession? _session;
    private RecorderOptions _options = new();
    private Mp4Writer? _writer;
    private long _duration;
    private int _failed;
    private Task? _stopping;

    /// <summary>Recording hit an error (encoder, disk, device change). Raised once, on a worker thread; call <see cref="StopAsync"/>.</summary>
    public event Action<string>? Failed;

    public bool IsRecording => _session != null && _stopping == null;
    public string? FilePath { get; private set; }

    /// <summary>Media time written so far (the last video sample's end).</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _duration));

    // While recording these read the live session; after StopAsync, the last recording's values.
    public string? EncoderName => _session?.EncoderName ?? _last.Name;
    public bool IsHardwareEncoder => _session?.IsHardwareEncoder ?? _last.Hardware;
    public int VideoBitrate => _session?.VideoBitrate ?? _last.Bitrate;
    public (int Width, int Height) Size => _session?.Size ?? _last.Size;
    public long FramesEncoded => _session?.FramesEncoded ?? _last.Frames;
    public long DroppedFrames => _session?.DroppedFrames ?? _last.Dropped;

    private (string? Name, bool Hardware, int Bitrate, (int, int) Size, long Frames, long Dropped) _last;

    /// <summary>True when the file is fragmented MP4 (false: plain MP4, if the fMP4 sink was unavailable).</summary>
    public bool IsFragmented { get; private set; }

    /// <summary>
    /// Starts a recording to <paramref name="path"/> (see <see cref="RecordingPaths.NewFilePath"/>). The file is created
    /// when the first encoded frame is out; the encoder is chosen then, on the render device of the first frame.
    /// </summary>
    public void Start(string path, RecorderOptions? options = null)
    {
        lock (_gate)
        {
            if (_session != null) throw new InvalidOperationException("Already recording.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            FilePath = path;
            _options = options ?? new RecorderOptions();
            _duration = 0;
            _failed = 0;
            _stopping = null;
            var session = new EncodingSession(_options, this);
            session.Start();
            _session = session;
        }
    }

    /// <summary>A rendered frame: signature matches <see cref="Render.D3DRenderer.SourceRendered"/> (render thread, render lock held).</summary>
    public void WriteVideo(ID3D11Texture2D texture, long timestamp) => _session?.WriteVideo(texture, timestamp);

    /// <summary>Captured audio: signature matches <see cref="Audio.WasapiAudioPipeline.CapturedAudio"/> (capture thread).</summary>
    public void WriteAudio(ReadOnlySpan<float> interleavedStereo, long timestamp) => _session?.WriteAudio(interleavedStereo, timestamp);

    /// <summary>Drains the encoders and finalizes the file. Returns the path, or null when nothing was written.</summary>
    public async Task<string?> StopAsync()
    {
        EncodingSession? session;
        lock (_gate)
        {
            session = _session;
            if (session == null) return null;
            _stopping ??= Stop(session);
        }
        await _stopping.ConfigureAwait(false);
        return File.Exists(FilePath) ? FilePath : null;
    }

    private async Task Stop(EncodingSession session)
    {
        await session.StopAsync().ConfigureAwait(false);
        await Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    _writer?.Finish();
                }
                catch (Exception e)
                {
                    Fail($"Couldn't finish the file: {e.Message}");
                }
                _writer?.Dispose();
                _writer = null;
                foreach (var sample in _pendingAudio) MfInterop.Release(sample);
                _pendingAudio.Clear();
                _last = (session.EncoderName, session.IsHardwareEncoder, session.VideoBitrate, session.Size, session.FramesEncoded, session.DroppedFrames);
                session.Dispose();
                _session = null;
            }
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    void IEncodedSink.OnVideo(IMFSample sample, long time, long duration, bool keyframe)
    {
        lock (_gate)
        {
            try
            {
                if (_writer == null)
                {
                    if (!keyframe || _failed != 0 || _session is not { VideoType: { } videoType } session) return;
                    _writer = new Mp4Writer(FilePath!, _options.Fragmented, videoType, session.WaitAudioType());
                    IsFragmented = _writer.Fragmented;
                    foreach (var pending in _pendingAudio)
                    {
                        _writer.WriteAudio(pending);
                        MfInterop.Release(pending);
                    }
                    _pendingAudio.Clear();
                }
                _writer.WriteVideo(sample);
                Interlocked.Exchange(ref _duration, Math.Max(Interlocked.Read(ref _duration), time + duration));
            }
            catch (Exception e)
            {
                Fail($"Couldn't write the recording: {e.Message}");
            }
            finally
            {
                MfInterop.Release(sample);
            }
        }
    }

    void IEncodedSink.OnAudio(IMFSample sample, long time, long duration)
    {
        lock (_gate)
        {
            if (_writer == null && _failed == 0)
            {
                _pendingAudio.Add(sample);
                if (_pendingAudio.Count > MaxPendingAudio)
                {
                    MfInterop.Release(_pendingAudio[0]);
                    _pendingAudio.RemoveAt(0);
                }
                return;
            }
            try
            {
                _writer?.WriteAudio(sample);
            }
            catch (Exception e)
            {
                Fail($"Couldn't write audio: {e.Message}");
            }
            finally
            {
                MfInterop.Release(sample);
            }
        }
    }

    void IEncodedSink.OnFailed(string message) => Fail(message);

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        // Off the encoder thread, so a handler may await StopAsync (which joins that thread).
        if (Failed is { } failed) ThreadPool.QueueUserWorkItem(_ => failed(message));
    }
}
