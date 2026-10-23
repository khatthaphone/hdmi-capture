using System.Diagnostics;
using HDMICaptain.Linux.Media.Recording;
using HDMICaptain.Linux.Media.Render;

namespace HDMICaptain.Linux.Media.Tests;

/// <summary>The ffmpeg recorder end to end: 3 s of synthetic 60 fps NV12 + a tone → MP4, checked with ffprobe.</summary>
public sealed class RecorderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hc-rec").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RecordsH264AndAacWithTheRightDuration()
    {
        if (FfmpegRecorder.Encoders.Best == null) return; // no ffmpeg on this machine
        var path = Path.Combine(_dir, "test.mp4");
        var recorder = FfmpegRecorder.Start(path, RenderPixelFormat.Nv12, 640, 360, 60, audio: true);
        var frame = new byte[640 * 360 * 3 / 2];
        var tone = new float[480 * 2];
        var clock = Stopwatch.StartNew();
        var n = 0;
        while (clock.Elapsed.TotalSeconds < 3)
        {
            frame.AsSpan(0, 640 * 360).Fill((byte)(16 + n % 200));
            recorder.WriteVideo(frame);
            for (var i = 0; i < 480; i++) tone[2 * i] = tone[2 * i + 1] = MathF.Sin((n * 480 + i) * 2 * MathF.PI * 1000 / 48000) * 0.2f;
            recorder.WriteAudio(tone); // 10 ms of audio per ~16.7 ms of video: fine for a format check
            n++;
            Thread.Sleep(16);
        }
        Assert.Equal(0, recorder.Stop());
        recorder.Dispose();

        var probe = Probe(path, "stream=codec_name,codec_type");
        Assert.Contains("h264", probe);
        Assert.Contains("aac", probe);
        var duration = double.Parse(Probe(path, "format=duration").Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 2.5, 3.6);
    }

    private static string Probe(string path, string entries)
    {
        var info = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", entries, "-of", "default=nw=1:nk=1", path }) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
