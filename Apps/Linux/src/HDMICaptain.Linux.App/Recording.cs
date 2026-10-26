using HDMICaptain.Core;
using HDMICaptain.Linux.Media;
using HDMICaptain.Linux.Media.Audio;
using HDMICaptain.Linux.Media.Recording;
using HDMICaptain.Linux.Media.Render;
using HDMICaptain.Linux.Media.Video;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Recording and Save Recent for one viewer (Pro, task 120). Both take the decoded source frames from the window's
/// <see cref="FrameWriter"/> and the card's audio from its pipeline; files go to Videos/HDMI Captain. Save Recent keeps
/// 2 s segments in the cache and joins the newest ones on save. The frames go through a <see cref="RecordingRenderer"/>
/// first, so files carry the picture look like screenshots do (Windows/Mac behaviour), even with the window hidden;
/// without a headless GL context the raw source is recorded. UI thread, except the frame/audio taps.
/// </summary>
public sealed class Recording : IDisposable
{
    private readonly FrameWriter _writer;
    private readonly Func<LinuxAudioPipeline?> _audio;
    private readonly Func<double> _fps;
    private FfmpegRecorder? _file, _recent;
    private string? _filePath;
    private readonly string _recentDir = Path.Combine(Path.GetTempPath(), $"hdmicaptain-recent-{Environment.ProcessId}-{Guid.NewGuid():N}");
    private LinuxAudioPipeline? _tappedAudio;
    private RecordingRenderer? _gpu;
    private bool _gpuFailed;
    private (int Width, int Height) _size;
    private (RenderOptions Options, ImageAdjustments Adjustments, CubeLUT? Lut, double LutIntensity)? _look;

    public Recording(FrameWriter writer, Func<LinuxAudioPipeline?> audio, Func<double> fps)
    {
        (_writer, _audio, _fps) = (writer, audio, fps);
        _writer.FrameReady = OnFrame;
        // The encoder probe takes a moment the first time; do it before anyone presses Record.
        _ = Task.Run(() => FfmpegRecorder.Encoders.Best);
    }

    public bool IsRecording => _file != null;
    public bool KeepsRecent => _recent != null;

    /// <summary>The window's look and colour options; recordings follow every change, also mid-recording.</summary>
    public void SetLook(RenderOptions options, ImageAdjustments adjustments, CubeLUT? lut, double lutIntensity)
    {
        _look = (options, adjustments, lut, lutIntensity);
        if (_gpu != null) ApplyLook(_gpu);
    }

    private void ApplyLook(RecordingRenderer gpu)
    {
        if (_look is not { } look) return;
        gpu.Renderer.SetOptions(look.Options);
        gpu.Renderer.SetLook(look.Adjustments, look.Lut, look.LutIntensity);
    }

    /// <summary>
    /// What the recorders are given for this layout: NV12 from the GPU path (look applied, colours tagged), or the raw
    /// source frames when the GPU path can't run.
    /// </summary>
    private (RenderPixelFormat Format, string? Colorspace) Feed((RenderPixelFormat Format, int Width, int Height) layout)
    {
        if (_file != null || _recent != null) return _gpu != null ? (RenderPixelFormat.Nv12, Colorspace(_size.Height)) : (layout.Format, null);
        _size = (layout.Width, layout.Height);
        if (!_gpuFailed && RecordingRenderer.Supports(layout.Width, layout.Height))
        {
            var gpu = new RecordingRenderer();
            if (gpu.WaitReady())
            {
                ApplyLook(gpu);
                gpu.Packed = OnPacked;
                _gpu = gpu;
                return (RenderPixelFormat.Nv12, Colorspace(layout.Height));
            }
            gpu.Dispose();
            _gpuFailed = true;
            LinuxLog.Write("recording: no GPU path, recording the source without the look");
        }
        return (layout.Format, null);
    }

    private static string Colorspace(int height) => height >= 720 ? "bt709" : "smpte170m";

    /// <summary>Both recorders stopped: free the GPU path.</summary>
    private void ReleaseFeed()
    {
        if (_file != null || _recent != null || _gpu == null) return;
        var gpu = _gpu;
        _gpu = null;
        LinuxLog.Write($"recording renderer: {gpu.PackedFrames} packed, {gpu.Skipped} skipped, {gpu.Unpackable} unpackable");
        gpu.Dispose();
    }

    public static string VideosDir => Path.Combine(GLib.Functions.GetUserSpecialDir(GLib.UserDirectory.DirectoryVideos)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos"), "HDMI Captain");

    /// <summary>Starts a recording; returns why not, or null.</summary>
    public string? StartFile()
    {
        if (_file != null) return null;
        if (_writer.LastLayout is not { } layout) return "Connect a capture card to record";
        Directory.CreateDirectory(VideosDir);
        _filePath = Path.Combine(VideosDir, $"HDMI Captain {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        var (format, colorspace) = Feed(layout);
        try
        {
            _file = FfmpegRecorder.Start(_filePath, format, _size.Width, _size.Height, _fps(), _audio() != null, colorspace: colorspace);
        }
        catch (InvalidOperationException e)
        {
            ReleaseFeed();
            return $"Recording failed: {e.Message}";
        }
        TapAudio();
        return null;
    }

    /// <summary>Ends the recording; returns the saved file (null if none).</summary>
    public string? StopFile()
    {
        var recorder = _file;
        _file = null;
        if (recorder == null) return null;
        var ok = recorder.Stop() == 0;
        recorder.Dispose();
        TapAudio();
        ReleaseFeed();
        return ok ? _filePath : null;
    }

    /// <summary>Keep Recent Footage on/off (the segment ring runs while the card is live).</summary>
    public string? SetKeepRecent(bool on)
    {
        if (!on)
        {
            _recent?.Stop();
            _recent?.Dispose();
            _recent = null;
            TapAudio();
            ReleaseFeed();
            return null;
        }
        if (_recent != null) return null;
        if (_writer.LastLayout is not { } layout) return "Connect a capture card to keep recent footage";
        var (format, colorspace) = Feed(layout);
        try
        {
            _recent = FfmpegRecorder.Start(_recentDir, format, _size.Width, _size.Height, _fps(), _audio() != null, segments: true, colorspace: colorspace);
        }
        catch (InvalidOperationException e)
        {
            ReleaseFeed();
            return $"Recording failed: {e.Message}";
        }
        TapAudio();
        return null;
    }

    /// <summary>Every few seconds while keeping recent footage: drop segments older than the window.</summary>
    public void Trim(double minutes)
    {
        if (_recent != null && Directory.Exists(_recentDir)) FfmpegRecorder.TrimSegments(_recentDir, minutes * 60);
    }

    public string? SaveRecent(double seconds)
    {
        if (_recent == null) return null;
        Directory.CreateDirectory(VideosDir);
        var path = Path.Combine(VideosDir, $"HDMI Captain Recent {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        return FfmpegRecorder.SaveRecent(_recentDir, seconds, path) ? path : null;
    }

    /// <summary>The audio pipeline changed (card switched, restarted): re-attach the recorders to it.</summary>
    public void TapAudio()
    {
        if (_tappedAudio != null) _tappedAudio.CapturedAudio -= OnAudio;
        _tappedAudio = _file != null || _recent != null ? _audio() : null;
        if (_tappedAudio != null) _tappedAudio.CapturedAudio += OnAudio;
    }

    private void OnFrame(ReadOnlySpan<byte> frame, RenderPixelFormat format, int width, int height, long timestampNs, bool fullRange, FrameMatrix matrix)
    {
        // A recording keeps the size it started with; frames of another size (the source changed) are left out.
        if (_file == null && _recent == null || (width, height) != _size) return;
        if (_gpu is { } gpu)
        {
            gpu.Push(frame, format, width, height, timestampNs, fullRange, matrix);
            return;
        }
        _file?.WriteVideo(frame);
        _recent?.WriteVideo(frame);
    }

    // The recording renderer's thread: one NV12 frame with the look.
    private void OnPacked(ReadOnlySpan<byte> nv12, int width, int height, long timestampNs)
    {
        if ((width, height) != _size) return;
        _file?.WriteVideo(nv12);
        _recent?.WriteVideo(nv12);
    }

    private void OnAudio(ReadOnlySpan<float> stereo, long timestampNs)
    {
        _file?.WriteAudio(stereo);
        _recent?.WriteAudio(stereo);
    }

    public void Dispose()
    {
        StopFile();
        SetKeepRecent(false);
        _writer.FrameReady = null;
        try
        {
            if (Directory.Exists(_recentDir)) Directory.Delete(_recentDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
