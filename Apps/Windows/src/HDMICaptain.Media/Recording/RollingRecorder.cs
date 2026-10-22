using System.Diagnostics;
using HDMICaptain.Media.Capture;
using Vortice.Direct3D11;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>What <see cref="RollingRecorder.SaveAsync"/> wrote.</summary>
public sealed record SaveResult(string Path, TimeSpan Duration, int VideoSamples, long VideoBytes, int AudioSamples, TimeSpan Elapsed);

/// <summary>
/// Save Recent (task 100, the Mac's RollingRecorder): one encoder runs for as long as the buffer is on, and its
/// encoded samples (not frames) stay in memory for <see cref="KeepDuration"/>, trimmed at keyframes, which come
/// every <see cref="RecorderOptions.KeyframeInterval"/>. <see cref="SaveAsync"/> remuxes the newest span into an
/// MP4 through a passthrough Sink Writer: nothing is re-encoded, so it takes milliseconds. The saved span starts
/// at the keyframe at or before the cut, so it runs up to one keyframe interval longer than asked.
/// </summary>
public sealed class RollingRecorder : IAsyncDisposable, IEncodedSink
{
    /// <summary>Bits per pixel for the buffer: about 8.7 Mbps at 1080p60 (65 MB per minute).</summary>
    private const double BufferBitsPerPixel = 0.07;

    private readonly object _ring = new();
    private readonly List<Encoded> _video = [];
    private readonly List<Encoded> _audio = [];
    private readonly RecorderOptions _options;
    private volatile EncodingSession? _session;
    private IMFMediaType? _videoType;
    private long _bytes;
    private int _failed;

    private readonly record struct Encoded(byte[] Data, long Time, long Duration, bool Keyframe);

    /// <param name="keep">How much to keep; default 15 min. Memory is about bitrate × keep (65 MB/min at 1080p60).</param>
    /// <param name="options">Encoder settings; Fragmented is ignored (saves are plain MP4).</param>
    public RollingRecorder(TimeSpan? keep = null, RecorderOptions? options = null)
    {
        KeepDuration = keep ?? TimeSpan.FromMinutes(15);
        _options = (options ?? new RecorderOptions()) with { Fragmented = false };
        if (_options.BitsPerPixel == null && _options.Bitrate == null) _options = _options with { BitsPerPixel = BufferBitsPerPixel };
    }

    public TimeSpan KeepDuration { get; }

    /// <summary>Raised once on a worker thread when the encoder fails; the buffer stops filling (Stop and Start to retry).</summary>
    public event Action<string>? Failed;

    public bool IsRunning => _session != null;
    public string? EncoderName => _session?.EncoderName;
    public bool IsHardwareEncoder => _session?.IsHardwareEncoder == true;

    /// <summary>Span of video held right now.</summary>
    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_ring)
                return _video.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_video[^1].Time + _video[^1].Duration - _video[0].Time);
        }
    }

    public long BufferedBytes => Interlocked.Read(ref _bytes);

    public void Start()
    {
        lock (_ring)
        {
            if (_session != null) return;
            _failed = 0;
            var session = new EncodingSession(_options, this);
            session.Start();
            _session = session;
        }
    }

    /// <summary>Signature matches <see cref="Render.D3DRenderer.SourceRendered"/>.</summary>
    public void WriteVideo(ID3D11Texture2D texture, long timestamp) => _session?.WriteVideo(texture, timestamp);

    /// <summary>Signature matches <see cref="Audio.WasapiAudioPipeline.CapturedAudio"/>.</summary>
    public void WriteAudio(ReadOnlySpan<float> interleavedStereo, long timestamp) => _session?.WriteAudio(interleavedStereo, timestamp);

    /// <summary>Stops the encoder and drops the buffer.</summary>
    public async Task StopAsync()
    {
        EncodingSession? session;
        lock (_ring)
        {
            session = _session;
            _session = null;
        }
        if (session == null) return;
        await session.StopAsync().ConfigureAwait(false);
        lock (_ring)
        {
            _video.Clear();
            _audio.Clear();
            _bytes = 0;
            MfInterop.Release(_videoType);
            _videoType = null;
            session.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Writes the newest <paramref name="span"/> (from the keyframe at or before its start) to <paramref name="path"/>
    /// without re-encoding. Encoding keeps running. Throws <see cref="InvalidOperationException"/> when nothing is buffered.
    /// </summary>
    public Task<SaveResult> SaveAsync(TimeSpan span, string path)
    {
        var clock = Stopwatch.StartNew();
        Encoded[] video, audio;
        IMFMediaType videoType;
        IMFMediaType? audioType;
        lock (_ring)
        {
            if (_session is not { } session || _video.Count == 0 || _videoType == null)
                throw new InvalidOperationException("Nothing is buffered yet.");
            var end = _video[^1].Time + _video[^1].Duration;
            var cut = end - span.Ticks;
            var start = 0;
            for (var i = _video.Count - 1; i >= 0; i--)
            {
                if (_video[i].Keyframe && _video[i].Time <= cut)
                {
                    start = i;
                    break;
                }
            }
            video = _video.GetRange(start, _video.Count - start).ToArray();
            var first = video[0].Time;
            audio = _audio.Where(a => a.Time >= first && a.Time < end).ToArray();
            videoType = MfMedia.Clone(_videoType);
            audioType = audio.Length > 0 && session.WaitAudioType() is { } a ? MfMedia.Clone(a) : null;
        }
        return Task.Run(() =>
        {
            try
            {
                return Write(path, video, audio, videoType, audioType, clock);
            }
            finally
            {
                MfInterop.Release(videoType);
                MfInterop.Release(audioType);
            }
        });
    }

    private static SaveResult Write(string path, Encoded[] video, Encoded[] audio, IMFMediaType videoType, IMFMediaType? audioType, Stopwatch clock)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var origin = video[0].Time;
        long bytes = 0;
        using var writer = new Mp4Writer(path, fragmented: false, videoType, audioType);
        int v = 0, a = 0;
        while (v < video.Length || a < audio.Length)
        {
            // Interleave by time, so the muxer never has to hold one track back.
            var takeVideo = a >= audio.Length || (v < video.Length && video[v].Time <= audio[a].Time);
            var item = takeVideo ? video[v++] : audio[a++];
            var sample = MfMedia.CreateSample(item.Data, item.Time - origin, item.Duration);
            try
            {
                if (takeVideo)
                {
                    if (item.Keyframe) sample.SetUInt32(PInvoke.MFSampleExtension_CleanPoint, 1);
                    writer.WriteVideo(sample);
                    bytes += item.Data.Length;
                }
                else
                {
                    writer.WriteAudio(sample);
                }
            }
            finally
            {
                MfInterop.Release(sample);
            }
        }
        writer.Finish();
        var last = video[^1];
        return new SaveResult(path, TimeSpan.FromTicks(last.Time + last.Duration - origin), video.Length, bytes,
            audioType != null ? audio.Length : 0, clock.Elapsed);
    }

    void IEncodedSink.OnVideo(IMFSample sample, long time, long duration, bool keyframe)
    {
        try
        {
            var data = MfMedia.CopyBytes(sample);
            lock (_ring)
            {
                if (_video.Count == 0 && !keyframe) return; // the buffer always starts at a keyframe
                if (_videoType == null && _session?.VideoType is { } type) _videoType = MfMedia.Clone(type);
                _video.Add(new Encoded(data, time, duration, keyframe));
                _bytes += data.Length;
                Trim();
            }
        }
        finally
        {
            MfInterop.Release(sample);
        }
    }

    void IEncodedSink.OnAudio(IMFSample sample, long time, long duration)
    {
        try
        {
            var data = MfMedia.CopyBytes(sample);
            lock (_ring)
            {
                _audio.Add(new Encoded(data, time, duration, true));
                _bytes += data.Length;
            }
        }
        finally
        {
            MfInterop.Release(sample);
        }
    }

    void IEncodedSink.OnFailed(string message)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        if (Failed is { } failed) ThreadPool.QueueUserWorkItem(_ => failed(message));
    }

    /// <summary>Drops whole GOPs from the front while the rest still covers <see cref="KeepDuration"/>.</summary>
    private void Trim()
    {
        var newest = _video[^1].Time + _video[^1].Duration;
        var keep = KeepDuration.Ticks;
        var cut = 0;
        for (var i = 1; i < _video.Count; i++)
        {
            if (!_video[i].Keyframe) continue;
            if (newest - _video[i].Time < keep) break;
            cut = i;
        }
        if (cut > 0)
        {
            for (var i = 0; i < cut; i++) _bytes -= _video[i].Data.Length;
            _video.RemoveRange(0, cut);
        }
        var first = _video[0].Time;
        var audioCut = 0;
        while (audioCut < _audio.Count && _audio[audioCut].Time < first) _bytes -= _audio[audioCut++].Data.Length;
        if (audioCut > 0) _audio.RemoveRange(0, audioCut);
    }
}
