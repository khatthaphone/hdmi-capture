#if DEBUG
using System.Diagnostics;
using HDMICaptain.Core;
using HDMICaptain.Media.Audio;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Recording;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;

/// <summary>
/// <c>--record N</c>: the real card through the same path the app uses (MfSource → D3DRenderer on the default GPU →
/// SourceRendered → Recorder and RollingRecorder; WASAPI CapturedAudio, muted output), then reads both files back.
/// </summary>
internal static class RecordTest
{
    public static async Task Run(VideoDevice card, int seconds, FormatPreference? pin, bool ignoreLink, MjpegPolicy mjpeg)
    {
        Console.Error.WriteLine($"record {card.Name} for {seconds} s");
        using var renderer = D3DRenderer.Create(0);
        using var source = new MfSource(card.SymbolicLink, pin, ignoreLink ? null : card.Usb?.Link, mjpeg);
        var endpoint = card.Usb?.ContainerId is { } container ? AudioEndpoints.CaptureFor(container) : null;
        using var audio = endpoint != null ? new WasapiAudioPipeline(endpoint.Id) { Settings = new AudioSettings(Muted: true) } : null;
        await using var recorder = new Recorder();
        await using var rolling = new RollingRecorder(TimeSpan.FromSeconds(90));
        recorder.Failed += m => Console.Error.WriteLine($"  recorder failed: {m}");
        rolling.Failed += m => Console.Error.WriteLine($"  rolling failed: {m}");
        source.Failed += e => Console.Error.WriteLine($"  capture stopped: {e}");
        if (audio != null) audio.Log += l => Console.Error.WriteLine($"  audio: {l}");

        renderer.SourceRendered += recorder.WriteVideo;
        renderer.SourceRendered += rolling.WriteVideo;
        source.FrameArrived += (in VideoFrame frame) => renderer.Render(in frame);
        if (audio != null)
        {
            audio.CapturedAudio += recorder.WriteAudio;
            audio.CapturedAudio += rolling.WriteAudio;
        }

        source.Start();
        audio?.Start();
        await Task.Delay(500); // the capture thread reads the endpoint's format
        var type = source.CurrentType!;
        var options = new RecorderOptions
        {
            FrameRate = type.FrameRate,
            IncludeAudio = audio != null,
            AudioSampleRate = audio?.CaptureSampleRate ?? 48_000,
        };
        Console.Error.WriteLine($"  {type.Width}x{type.Height} {type.FourCC} @ {type.FrameRate:F2} on {renderer.AdapterName}; " +
                                $"audio {(endpoint == null ? "none" : $"{endpoint.Name} {options.AudioSampleRate} Hz")}");
        var path = RecordingPaths.NewFilePath();
        var clock = Stopwatch.StartNew();
        recorder.Start(path, options);
        rolling.Start();
        for (var s = 1; s <= seconds; s++)
        {
            var wait = TimeSpan.FromSeconds(s) - clock.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            Console.Error.WriteLine($"  t={s}s recorded {recorder.Duration.TotalSeconds:F1} s, frames {recorder.FramesEncoded}, dropped {recorder.DroppedFrames}, " +
                                    $"buffer {rolling.BufferedDuration.TotalSeconds:F1} s / {rolling.BufferedBytes / 1e6:F1} MB");
        }

        var recentPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " (recent).mp4");
        SaveResult? saved = null;
        try
        {
            saved = await rolling.SaveAsync(TimeSpan.FromSeconds(Math.Max(1, seconds / 2)), recentPath);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"  save recent: {e.Message}");
        }
        var rollingEncoder = rolling.EncoderName;
        var stop = Stopwatch.StartNew();
        var recorded = await recorder.StopAsync();
        Console.Error.WriteLine($"  stop took {stop.ElapsedMilliseconds} ms; encoder {recorder.EncoderName} (hardware={recorder.IsHardwareEncoder}, " +
                                $"{recorder.VideoBitrate / 1e6:F1} Mbps, fragmented={recorder.IsFragmented}), frames {recorder.FramesEncoded}, dropped {recorder.DroppedFrames}");
        source.Stop();
        audio?.Stop();
        await rolling.StopAsync();

        Console.Error.WriteLine(recorded == null ? "  nothing recorded" : $"  {path}\n    {MediaProbe.Read(path)}");
        if (saved != null) Console.Error.WriteLine($"  save recent {saved.Duration.TotalSeconds:F2} s in {saved.Elapsed.TotalMilliseconds:F0} ms ({rollingEncoder}): {recentPath}\n    {MediaProbe.Read(recentPath)}");
    }
}
#endif
