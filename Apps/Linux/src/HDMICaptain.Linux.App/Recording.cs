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
/// 2 s segments in the cache and joins the newest ones on save. UI thread, except the frame/audio taps.
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

    public Recording(FrameWriter writer, Func<LinuxAudioPipeline?> audio, Func<double> fps)
    {
        (_writer, _audio, _fps) = (writer, audio, fps);
        _writer.FrameReady = OnFrame;
        // The encoder probe takes a moment the first time; do it before anyone presses Record.
        _ = Task.Run(() => FfmpegRecorder.Encoders.Best);
    }

    public bool IsRecording => _file != null;
    public bool KeepsRecent => _recent != null;

    public static string VideosDir => Path.Combine(GLib.Functions.GetUserSpecialDir(GLib.UserDirectory.DirectoryVideos)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos"), "HDMI Captain");

    /// <summary>Starts a recording; returns why not, or null.</summary>
    public string? StartFile()
    {
        if (_file != null) return null;
        if (_writer.LastLayout is not { } layout) return "Connect a capture card to record";
        Directory.CreateDirectory(VideosDir);
        _filePath = Path.Combine(VideosDir, $"HDMI Captain {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
        try
        {
            _file = FfmpegRecorder.Start(_filePath, layout.Format, layout.Width, layout.Height, _fps(), _audio() != null);
        }
        catch (InvalidOperationException e)
        {
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
            return null;
        }
        if (_recent != null) return null;
        if (_writer.LastLayout is not { } layout) return "Connect a capture card to keep recent footage";
        try
        {
            _recent = FfmpegRecorder.Start(_recentDir, layout.Format, layout.Width, layout.Height, _fps(), _audio() != null, segments: true);
        }
        catch (InvalidOperationException e)
        {
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

    private void OnFrame(ReadOnlySpan<byte> frame, RenderPixelFormat format, int width, int height)
    {
        _file?.WriteVideo(frame);
        _recent?.WriteVideo(frame);
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
