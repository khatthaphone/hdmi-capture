using System.Diagnostics;
using System.Globalization;

namespace HDMICaptain.Media.Recording;

public enum VideoCodec { H264, Hevc }

/// <summary>Encoder settings for <see cref="Recorder"/> and <see cref="RollingRecorder"/>.</summary>
public sealed record RecorderOptions
{
    /// <summary>HEVC needs a hardware encoder (the software HEVC MFT is a paid Store extension); H.264 falls back to software.</summary>
    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>Bits per second; null picks one from size and frame rate.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Nominal frame rate (the capture type's). Only rate control uses it: sample times come from QPC.</summary>
    public double FrameRate { get; init; } = 60;

    /// <summary>Encoded size; null uses the first frame's size (rounded down to even). Other sizes are scaled to it.</summary>
    public (int Width, int Height)? Size { get; init; }

    public bool IncludeAudio { get; init; } = true;

    /// <summary>Sample rate of the audio passed to WriteAudio (<c>WasapiAudioPipeline.CaptureSampleRate</c>); 44.1 or 48 kHz.</summary>
    public int AudioSampleRate { get; init; } = 48_000;

    /// <summary>Use the GPU's encoder MFT (NVENC/AMF/QSV) when the render device has one; otherwise software H.264.</summary>
    public bool PreferHardware { get; init; } = true;

    /// <summary>Fragmented MP4, so a crash or power loss leaves a playable file (Recorder only).</summary>
    public bool Fragmented { get; init; } = true;

    /// <summary>A keyframe at least this often (GOP size plus a forced keyframe).</summary>
    public TimeSpan KeyframeInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Live use drops a frame when every texture in the pool is still queued for the encoder. Tests feed faster
    /// than real time and set this to false, so WriteVideo waits instead.
    /// </summary>
    public bool DropWhenBusy { get; init; } = true;

    /// <summary>Overrides the codec's default bits per pixel (Save Recent uses less, since it lives in memory).</summary>
    internal double? BitsPerPixel { get; init; }

    internal int BitrateFor(int width, int height, double bitsPerPixel) =>
        Bitrate ?? (int)Math.Clamp(width * height * Math.Max(1, FrameRate) * bitsPerPixel, 1_000_000, 80_000_000);
}

/// <summary>QPC in 100 ns units: the one clock both video (render time) and audio (WASAPI QPC position) use.</summary>
public static class MediaClock
{
    private static readonly long Frequency = Stopwatch.Frequency;

    public static long Now() => FromQpc(Stopwatch.GetTimestamp());

    public static long FromQpc(long ticks) => Frequency == 10_000_000 ? ticks : (long)((Int128)ticks * 10_000_000 / Frequency);
}

/// <summary>Where recordings go: <c>Videos\HDMI Captain\HDMI Captain yyyy-MM-dd at HH.mm.ss.mp4</c>.</summary>
public static class RecordingPaths
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "HDMI Captain");

    /// <summary>A new file name in <paramref name="folder"/> (created if missing), " 2", " 3"… when taken.</summary>
    public static string NewFilePath(string? folder = null, DateTime? now = null)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        var stem = "HDMI Captain " + (now ?? DateTime.Now).ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, stem + ".mp4");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} {n}.mp4");
        return path;
    }
}
