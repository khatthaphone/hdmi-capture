using HDMICaptain.Core;
using HDMICaptain.Media.Recording;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;

namespace HDMICaptain.Media.Tests;

/// <summary>
/// Task 100: Recorder and RollingRecorder fed the SMPTE pattern through the D3D renderer (WARP → software H.264;
/// the default GPU → its hardware MFT) plus a 1 kHz tone, faster than real time with synthetic QPC timestamps,
/// then read back with a Source Reader.
/// </summary>
public class RecordingTests
{
    private const int AudioRate = 48_000;

    private static string TempFile(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "hdmicaptain-tests");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{name}-{Guid.NewGuid():N}.mp4");
    }

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>Renders <paramref name="seconds"/> of bars at <paramref name="fps"/> and the matching tone, interleaved by time.</summary>
    private static void Feed(D3DRenderer renderer, SourceRenderedHandler video, Audio.AudioSamplesHandler audio,
        int width, int height, double fps, double seconds)
    {
        var frame = TestPattern.Pack(TestPatternKind.SmpteBars, PixelFormat.Nv12, width, height).AsFrame();
        var origin = MediaClock.Now();
        var chunk = new float[480 * 2];
        long audioFrames = 0;
        var frames = (int)Math.Round(seconds * fps);
        for (var i = 0; i < frames; i++)
        {
            var frameTime = (long)(i * 10_000_000.0 / fps);
            renderer.Render(in frame);
            video(renderer.SourceTexture!, origin + frameTime);
            // Audio up to the end of this frame, 10 ms packets as WASAPI delivers them.
            var frameEnd = (long)((i + 1) * 10_000_000.0 / fps);
            while (audioFrames * 10_000_000 / AudioRate < frameEnd)
            {
                for (var n = 0; n < 480; n++)
                {
                    var v = 0.25f * MathF.Sin(2 * MathF.PI * 1000 * (audioFrames + n) / AudioRate);
                    chunk[2 * n] = chunk[2 * n + 1] = v;
                }
                audio(chunk, origin + audioFrames * 10_000_000 / AudioRate);
                audioFrames += 480;
            }
        }
    }

    /// <summary>Decodes a frame and checks the seven 75 % bars against BT.709 limited range (what the converter writes).</summary>
    private static void AssertBars(string path, int width, int height)
    {
        var pixel = MediaProbe.DecodeFrame(path, 10);
        for (var bar = 0; bar < 7; bar++)
        {
            var (r, g, b) = TestPattern.FullBarColors[bar];
            (r, g, b) = (r * 0.75, g * 0.75, b * 0.75);
            var y = 16 + 219 * (0.2126 * r + 0.7152 * g + 0.0722 * b);
            var cb = 128 + 224 * (-0.1146 * r - 0.3854 * g + 0.5 * b);
            var cr = 128 + 224 * (0.5 * r - 0.4542 * g - 0.0458 * b);
            var actual = pixel((int)((bar + 0.5) * width / 7), height / 3);
            Assert.True(Math.Abs(actual.Y - y) <= 6 && Math.Abs(actual.Cb - cb) <= 8 && Math.Abs(actual.Cr - cr) <= 8,
                $"bar {bar}: got {actual}, expected ({y:F0}, {cb:F0}, {cr:F0})");
        }
    }

    [Fact]
    public async Task RecordsThreeSecondsOfBarsAndToneInSoftware()
    {
        using var renderer = D3DRenderer.CreateWarp();
        var path = TempFile("record");
        await using var recorder = new Recorder();
        string? failure = null;
        recorder.Failed += m => failure = m;
        recorder.Start(path, new RecorderOptions { FrameRate = 30, DropWhenBusy = false });

        Feed(renderer, recorder.WriteVideo, recorder.WriteAudio, 1920, 1080, 30, 3);
        var saved = await recorder.StopAsync();

        Assert.Null(failure);
        Assert.Equal(path, saved);
        Log($"{recorder.EncoderName} hardware={recorder.IsHardwareEncoder} fragmented={recorder.IsFragmented} frames={recorder.FramesEncoded}");
        var info = MediaProbe.Read(path);
        Log($"{info}");
        Assert.Equal((1920, 1080), (info.Width, info.Height));
        Assert.Equal("H264", info.Video.Subtype);
        Assert.Equal(90, info.Video.Samples);
        Assert.InRange(info.Video.End.TotalSeconds, 2.9, 3.1);
        Assert.NotNull(info.Audio);
        Assert.Equal("AAC", info.Audio!.Subtype);
        Assert.Equal((48_000, 2), (info.AudioSampleRate, info.AudioChannels));
        Assert.InRange(info.Audio.End.TotalSeconds, 2.8, 3.1);
        Assert.InRange(info.Audio.Start.TotalSeconds, 0, 0.05);
        // Fragmented MP4: movie fragments ('moof' boxes), so an interrupted recording stays playable.
        Assert.True(recorder.IsFragmented);
        Assert.Contains("moof", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)));
        AssertBars(path, 1920, 1080);
        File.Delete(path);
    }

    [Fact]
    public async Task RecordsOnTheGpuEncoderWhenThereIsOne()
    {
        D3DRenderer renderer;
        try
        {
            renderer = D3DRenderer.Create(0);
        }
        catch (Exception e)
        {
            Assert.Skip($"no hardware D3D11 device: {e.Message}");
            return;
        }
        using (renderer)
        {
            var path = TempFile("record-hw");
            await using var recorder = new Recorder();
            string? failure = null;
            recorder.Failed += m => failure = m;
            recorder.Start(path, new RecorderOptions { FrameRate = 60, DropWhenBusy = false });

            Feed(renderer, recorder.WriteVideo, recorder.WriteAudio, 1920, 1080, 60, 3);
            await recorder.StopAsync();

            Log($"adapter {renderer.AdapterName}: {recorder.EncoderName} hardware={recorder.IsHardwareEncoder} " +
                $"{recorder.VideoBitrate / 1e6:F1} Mbps fragmented={recorder.IsFragmented} failure={failure}");
            Assert.Null(failure);
            var info = MediaProbe.Read(path);
            Log($"{info}");
            Assert.Equal((1920, 1080), (info.Width, info.Height));
            Assert.Equal(180, info.Video.Samples);
            Assert.InRange(info.Video.End.TotalSeconds, 2.9, 3.1);
            Assert.NotNull(info.Audio);
            AssertBars(path, 1920, 1080);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveRecentRemuxesTheLastFifteenSecondsWithoutReencoding()
    {
        using var renderer = D3DRenderer.CreateWarp();
        await using var rolling = new RollingRecorder(TimeSpan.FromSeconds(60), new RecorderOptions { FrameRate = 30, DropWhenBusy = false });
        string? failure = null;
        rolling.Failed += m => failure = m;
        rolling.Start();

        Feed(renderer, rolling.WriteVideo, rolling.WriteAudio, 1280, 720, 30, 20);
        // Let the encoder catch up with what was queued (the last frames are still in flight).
        for (var i = 0; i < 100 && rolling.BufferedDuration < TimeSpan.FromSeconds(19.8); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(failure == null, failure);
        Assert.InRange(rolling.BufferedDuration.TotalSeconds, 19.8, 20.01);

        var path = TempFile("recent");
        var result = await rolling.SaveAsync(TimeSpan.FromSeconds(15), path);

        Assert.Null(failure);
        Log($"{rolling.EncoderName}: {result}");
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1), $"save took {result.Elapsed}");
        Assert.InRange(result.Duration.TotalSeconds, 15, 17.01); // starts at the keyframe at or before the cut (every 2 s)
        var info = MediaProbe.Read(path);
        Log($"{info}");
        Assert.Equal((1280, 720), (info.Width, info.Height));
        // Passthrough: the file holds exactly the buffered samples (same count; same payload give or take start-code bytes).
        Assert.Equal(result.VideoSamples, info.Video.Samples);
        Assert.InRange(info.Video.Bytes, result.VideoBytes * 0.99, result.VideoBytes * 1.01);
        Assert.InRange(info.Video.End.TotalSeconds, result.Duration.TotalSeconds - 0.05, result.Duration.TotalSeconds + 0.05);
        Assert.True(info.Video.Keyframes >= 7, $"{info.Video.Keyframes} keyframes");
        Assert.NotNull(info.Audio);
        Assert.Equal(result.AudioSamples, info.Audio!.Samples);
        AssertBars(path, 1280, 720);
        File.Delete(path);
    }

    [Fact]
    public async Task RollingBufferTrimsWholeKeyframeIntervals()
    {
        using var renderer = D3DRenderer.CreateWarp();
        await using var rolling = new RollingRecorder(TimeSpan.FromSeconds(5), new RecorderOptions { FrameRate = 30, DropWhenBusy = false, IncludeAudio = false });
        rolling.Start();

        Feed(renderer, rolling.WriteVideo, (_, _) => { }, 320, 180, 30, 14);
        await Task.Delay(500, TestContext.Current.CancellationToken); // the last frames leave the encoder

        // Keeps at least 5 s, and drops a 2 s GOP as soon as the rest still covers 5 s.
        Assert.InRange(rolling.BufferedDuration.TotalSeconds, 5, 7.01);
    }
}
