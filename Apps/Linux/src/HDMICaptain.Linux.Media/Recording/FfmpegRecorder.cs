using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HDMICaptain.Linux.Media.Render;

namespace HDMICaptain.Linux.Media.Recording;

/// <summary>
/// Recording and Save Recent through an ffmpeg process (task 120): the decoded source frames go to its stdin as raw
/// video, the card's audio (stereo float 48 kHz) through a FIFO, both stamped with wall-clock time so A/V stay in step.
/// The capture thread only copies the frame into a pooled buffer; a writer thread feeds ffmpeg, and frames are dropped
/// (counted) if ffmpeg falls behind, never the capture. Output: fragmented MP4 (H.264 + AAC), or 2 s segments for
/// Save Recent. The encoder is the first that passes a probe: NVENC, VA-API, x264, OpenH264.
/// </summary>
public sealed partial class FfmpegRecorder : IDisposable
{
    private const int QueueFrames = 30; // half a second: covers ffmpeg's start-up

    private readonly Process _process;
    private readonly BlockingCollection<(byte[] Buffer, int Length)> _video = new(QueueFrames);
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly Thread _writer;
    private readonly string? _fifo;
    private FileStream? _audio;
    private readonly int _frameBytes;
    private long _dropped, _written;

    private FfmpegRecorder(Process process, int frameBytes, string? fifo)
    {
        _process = process;
        _frameBytes = frameBytes;
        _fifo = fifo;
        _writer = new Thread(WriteLoop) { Name = "Recorder", IsBackground = true };
        _writer.Start();
    }

    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long WrittenFrames => Interlocked.Read(ref _written);
    public string Encoder { get; private init; } = "";

    /// <summary>
    /// Starts ffmpeg. <paramref name="output"/> is an .mp4 path, or a directory for Save Recent segments
    /// (<paramref name="segments"/>). Throws <see cref="InvalidOperationException"/> when ffmpeg can't start.
    /// </summary>
    public static FfmpegRecorder Start(string output, RenderPixelFormat format, int width, int height, double fps, bool audio, bool segments = false)
    {
        var encoder = Encoders.Best ?? throw new InvalidOperationException("No H.264 encoder in ffmpeg");
        string? fifo = null;
        if (audio)
        {
            fifo = Path.Combine(Path.GetTempPath(), $"hdmicaptain-audio-{Environment.ProcessId}-{Guid.NewGuid():N}.f32");
            if (mkfifo(fifo, 0x180) != 0) throw new InvalidOperationException($"mkfifo failed ({Marshal.GetLastPInvokeError()})");
        }
        var pix = format switch
        {
            RenderPixelFormat.Nv12 => "nv12",
            RenderPixelFormat.Yuy2 => "yuyv422",
            RenderPixelFormat.I420 => "yuv420p",
            _ => "yuv422p",
        };
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(encoder.Device);
        args.AddRange(["-thread_queue_size", "64", "-use_wallclock_as_timestamps", "1", "-f", "rawvideo", "-pix_fmt", pix,
            "-s", $"{width}x{height}", "-framerate", fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", "pipe:0"]);
        if (fifo != null)
            args.AddRange(["-thread_queue_size", "64", "-use_wallclock_as_timestamps", "1", "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", fifo]);
        args.AddRange(encoder.Video);
        args.AddRange(["-g", ((int)Math.Round(fps * 2)).ToString(System.Globalization.CultureInfo.InvariantCulture), "-fps_mode", "passthrough"]);
        if (fifo != null) args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        if (segments)
        {
            Directory.CreateDirectory(output);
            args.AddRange(["-f", "segment", "-segment_time", "2", "-reset_timestamps", "1", "-segment_format", "mp4",
                "-segment_format_options", "movflags=+frag_keyframe+empty_moov", Path.Combine(output, "seg%06d.mp4")]);
        }
        else
        {
            args.AddRange(["-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-y", output]);
        }
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg didn't start");
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) LinuxLog.Write($"ffmpeg: {e.Data}"); };
        process.BeginErrorReadLine();
        var recorder = new FfmpegRecorder(process, GlesRenderer.FrameBytes(format, width, height), fifo) { Encoder = encoder.Name };
        if (fifo != null)
        {
            // Opening a FIFO for writing blocks until ffmpeg opens it for reading; do it aside.
            var path = fifo;
            _ = Task.Run(() => recorder._audio = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read, 1 << 16));
        }
        LinuxLog.Write($"recorder: {encoder.Name} {width}x{height} {pix} @ {fps:0.##} → {output}");
        return recorder;
    }

    /// <summary>Capture thread: one frame in the layout given at start. Copies and returns; drops if ffmpeg is behind.</summary>
    public void WriteVideo(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < _frameBytes || _video.IsAddingCompleted) return;
        if (!_pool.TryTake(out var buffer) || buffer.Length < _frameBytes) buffer = new byte[_frameBytes];
        frame[.._frameBytes].CopyTo(buffer);
        if (!_video.TryAdd((buffer, _frameBytes)))
        {
            Interlocked.Increment(ref _dropped);
            _pool.Add(buffer);
        }
    }

    /// <summary>Audio thread: interleaved stereo float at 48 kHz.</summary>
    public void WriteAudio(ReadOnlySpan<float> stereo)
    {
        try
        {
            _audio?.Write(MemoryMarshal.AsBytes(stereo));
        }
        catch (IOException)
        {
            // ffmpeg went away; Stop reports it.
        }
    }

    private void WriteLoop()
    {
        var stdin = _process.StandardInput.BaseStream;
        try
        {
            foreach (var (buffer, length) in _video.GetConsumingEnumerable())
            {
                stdin.Write(buffer, 0, length);
                Interlocked.Increment(ref _written);
                _pool.Add(buffer);
            }
        }
        catch (IOException e)
        {
            LinuxLog.Write($"recorder: ffmpeg input closed: {e.Message}");
        }
    }

    /// <summary>Ends the file cleanly (closes the inputs, waits for ffmpeg). Returns ffmpeg's exit code.</summary>
    public int Stop()
    {
        _video.CompleteAdding();
        _writer.Join(5000);
        try
        {
            _process.StandardInput.Close();
            _audio?.Dispose();
        }
        catch (IOException)
        {
        }
        if (!_process.WaitForExit(10_000)) _process.Kill();
        if (_fifo != null) File.Delete(_fifo);
        LinuxLog.Write($"recorder: stopped, {WrittenFrames} frames written, {DroppedFrames} dropped, exit {_process.ExitCode}");
        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (!_process.HasExited) Stop();
        _process.Dispose();
    }

    /// <summary>Save Recent: joins the newest segments covering <paramref name="seconds"/> into one MP4 (stream copy).</summary>
    public static bool SaveRecent(string segmentDir, double seconds, string output)
    {
        var segments = Directory.GetFiles(segmentDir, "seg*.mp4").Order().ToList();
        if (segments.Count > 1) segments.RemoveAt(segments.Count - 1); // the one being written
        var take = segments.TakeLast((int)Math.Ceiling(seconds / 2)).ToList();
        if (take.Count == 0) return false;
        var list = Path.Combine(segmentDir, "concat.txt");
        File.WriteAllLines(list, take.Select(s => $"file '{s.Replace("'", "'\\''")}'"));
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", "-y", output })
            info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (err.Length > 0) LinuxLog.Write($"save recent: {err.Trim()}");
        return p.ExitCode == 0;
    }

    /// <summary>Keeps the segment ring at <paramref name="seconds"/> (oldest deleted).</summary>
    public static void TrimSegments(string segmentDir, double seconds)
    {
        var segments = Directory.GetFiles(segmentDir, "seg*.mp4").Order().ToList();
        var keep = (int)Math.Ceiling(seconds / 2) + 2;
        foreach (var old in segments.Take(Math.Max(0, segments.Count - keep))) File.Delete(old);
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int mkfifo(string path, uint mode);

    /// <summary>The H.264 encoders ffmpeg offers here, probed once with a tiny real encode.</summary>
    public static class Encoders
    {
        public sealed record Choice(string Name, string[] Device, string[] Video);

        private static readonly Lazy<Choice?> BestChoice = new(Probe);

        public static Choice? Best => BestChoice.Value;

        private static Choice? Probe()
        {
            var candidates = new[]
            {
                new Choice("h264_nvenc", [], ["-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-b:v", "12M"]),
                new Choice("h264_vaapi", ["-vaapi_device", "/dev/dri/renderD128"], ["-vf", "format=nv12,hwupload", "-c:v", "h264_vaapi", "-b:v", "12M"]),
                new Choice("libx264", [], ["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-crf", "20", "-pix_fmt", "yuv420p"]),
                new Choice("libopenh264", [], ["-c:v", "libopenh264", "-b:v", "12M", "-pix_fmt", "yuv420p"]),
            };
            foreach (var c in candidates)
            {
                var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error" }.Concat(c.Device)
                             .Concat(["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-frames:v", "5"]).Concat(c.Video).Concat(["-f", "null", "-"]))
                    info.ArgumentList.Add(a);
                try
                {
                    using var p = Process.Start(info)!;
                    p.StandardError.ReadToEnd();
                    if (p.WaitForExit(15_000) && p.ExitCode == 0)
                    {
                        LinuxLog.Write($"recorder: encoder {c.Name}");
                        return c;
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    LinuxLog.Write("recorder: ffmpeg not found");
                    return null;
                }
            }
            return null;
        }
    }
}
