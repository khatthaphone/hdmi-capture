using System.Text.Json.Serialization;

namespace HDMICaptain.Core;

/// <summary>A frame-rate range a capture format supports, in frames per second.</summary>
public readonly record struct FrameRateRange(double Min, double Max);

/// <summary>
/// Framework-free description of a capture format, so selection logic stays pure and testable.
/// Port of CaptureCore's FormatDescriptor / the Kotlin one. Four-character codes use UVC names
/// (NV12, YUY2, MJPG) or Apple names (420v, yuvs, dmb1); <see cref="MediaSubtypes"/> maps Media Foundation GUIDs to them.
/// </summary>
public sealed record FormatDescriptor(int Width, int Height, string FourCC, IReadOnlyList<FrameRateRange> FrameRateRanges)
{
    public static readonly IReadOnlySet<string> Compressed =
        new HashSet<string> { "dmb1", "jpeg", "mjpa", "mjpg", "avc1", "hvc1", "MJPG", "H264", "H265" };

    public static readonly IReadOnlySet<string> BiPlanar420 =
        new HashSet<string> { "420v", "420f", "NV12", "NV21", "I420" };

    public int PixelCount => Width * Height;

    public double MaxFrameRate => FrameRateRanges.Count == 0 ? 0.0 : FrameRateRanges.Max(r => r.Max);

    public bool IsCompressed => Compressed.Contains(FourCC);

    /// <summary>Exactly 16:9, what nearly every HDMI source outputs.</summary>
    public bool IsWidescreen => (long)Width * 9 == (long)Height * 16;

    /// <summary>Bytes per uncompressed frame on the wire; null for compressed formats.</summary>
    public double? BytesPerFrame =>
        IsCompressed ? null : BiPlanar420.Contains(FourCC) ? PixelCount * 1.5 : PixelCount * 2.0;

    /// <summary>The supported rate closest to <paramref name="target"/> (the first one on ties).</summary>
    public double NearestSupportedRate(double target)
    {
        var best = 0.0;
        var bestDistance = double.PositiveInfinity;
        foreach (var r in FrameRateRanges)
        {
            var v = Math.Clamp(target, r.Min, r.Max);
            var d = Math.Abs(v - target);
            if (d < bestDistance) { best = v; bestDistance = d; }
        }
        return best;
    }

    /// <summary>The same format limited to the rates <paramref name="link"/> can carry (unchanged when the link is null).</summary>
    public FormatDescriptor LimitedTo(UsbLink? link)
    {
        if (BytesPerFrame is not { } bytes || link is not { } l) return this;
        var maxRate = l.BytesPerSecond() / bytes;
        var ranges = FrameRateRanges.Where(r => r.Min <= maxRate).Select(r => r with { Max = Math.Min(r.Max, maxRate) }).ToList();
        return this with { FrameRateRanges = ranges };
    }
}

/// <summary>
/// Usable bandwidth of the card's USB connection (the Android rule, reused on Windows when the hub
/// reports the link speed). Budgets are estimates. Shared fixture: docs/fixtures/format-bandwidth.json.
/// </summary>
public enum UsbLink { Usb2, Usb3 }

public static class UsbLinkExtensions
{
    /// <summary>Usb2: high-speed, high-bandwidth isochronous (3 x 1024 B per 125 us microframe).</summary>
    public static double BytesPerSecond(this UsbLink link) => link == UsbLink.Usb2 ? 24_000_000.0 : 300_000_000.0;
}

/// <summary>Auto: smooth frame rate first, then 16:9, then highest resolution. Pinned: a resolution, optionally at a frame rate.</summary>
[JsonConverter(typeof(FormatPreferenceConverter))]
public abstract record FormatPreference
{
    private FormatPreference() { }

    public sealed record Auto : FormatPreference;

    public sealed record Pinned(int Width, int Height, double? FrameRate) : FormatPreference;
}

/// <param name="Index">Index into the device's format list.</param>
/// <param name="Format">The format as the link allows it (rates clipped to the USB budget).</param>
/// <param name="FrameRate">The frame rate to request.</param>
public sealed record FormatChoice(int Index, FormatDescriptor Format, double FrameRate);

/// <summary>
/// Port of CaptureCore's FormatSelector plus the USB link limit (from the Kotlin port).
/// Shared fixtures: docs/fixtures/format-selection.json, format-bandwidth.json.
/// </summary>
public static class FormatSelector
{
    /// <summary>Formats at or above this rate are treated as "smooth" and win over higher resolutions below it.</summary>
    public const double SmoothFrameRate = 49.9;

    private static readonly HashSet<string> Yuv422 = ["yuvs", "2vuy", "yuv2", "YUY2", "UYVY"];

    public static FormatChoice? Select(IReadOnlyList<FormatDescriptor> formats, FormatPreference? preference = null, UsbLink? link = null)
    {
        preference ??= new FormatPreference.Auto();
        var limited = formats.Select(f => f.LimitedTo(link)).ToList();
        var usable = Enumerable.Range(0, limited.Count).Where(i => limited[i].MaxFrameRate > 0).ToList();
        if (usable.Count == 0) return null;

        if (preference is FormatPreference.Pinned p)
        {
            var matches = usable.Where(i => limited[i].Width == p.Width && limited[i].Height == p.Height).ToList();
            if (matches.Count > 0) return Pinned(matches, limited, p.FrameRate);
        }
        var best = Best(usable, limited, AutoRank);
        return new FormatChoice(best, limited[best], limited[best].MaxFrameRate);
    }

    /// <summary>Distinct resolution/frame-rate combinations, best first, for a settings picker.</summary>
    public static IReadOnlyList<FormatPreference.Pinned> Options(IReadOnlyList<FormatDescriptor> formats, UsbLink? link = null)
    {
        var seen = new HashSet<(int, int, double)>();
        var result = new List<FormatPreference.Pinned>();
        foreach (var f in formats.Select(f => f.LimitedTo(link)).Where(f => f.MaxFrameRate > 0)
                     .OrderByDescending(f => f.PixelCount).ThenByDescending(f => f.MaxFrameRate))
        {
            var rate = Math.Floor(f.MaxFrameRate * 100 + 0.5) / 100.0;
            if (seen.Add((f.Width, f.Height, rate))) result.Add(new FormatPreference.Pinned(f.Width, f.Height, rate));
        }
        return result;
    }

    private static FormatChoice Pinned(List<int> matches, List<FormatDescriptor> formats, double? frameRate)
    {
        if (frameRate is not { } target)
        {
            var b = Best(matches, formats, AutoRank);
            return new FormatChoice(b, formats[b], formats[b].MaxFrameRate);
        }
        // Nearest achievable rate first, then prefer uncompressed.
        var best = Best(matches, formats, f => [-Math.Abs(f.NearestSupportedRate(target) - target), EncodingScore(f)]);
        return new FormatChoice(best, formats[best], formats[best].NearestSupportedRate(target));
    }

    private static double[] AutoRank(FormatDescriptor f) =>
    [
        f.MaxFrameRate >= SmoothFrameRate ? 1.0 : 0.0,
        f.IsWidescreen ? 1.0 : 0.0,
        f.PixelCount,
        f.MaxFrameRate,
        EncodingScore(f),
    ];

    /// <summary>Uncompressed YCbCr avoids an MJPEG decode step (latency + CPU).</summary>
    private static double EncodingScore(FormatDescriptor f) =>
        FormatDescriptor.BiPlanar420.Contains(f.FourCC) ? 3.0
        : Yuv422.Contains(f.FourCC) ? 2.0
        : f.IsCompressed ? 0.0
        : 1.0;

    /// <summary>Lexicographic max over a rank list; ties keep the earlier index, like Swift's max(by:).</summary>
    private static int Best(List<int> indices, List<FormatDescriptor> formats, Func<FormatDescriptor, double[]> rank)
    {
        var best = indices[0];
        var bestRank = rank(formats[best]);
        foreach (var i in indices.Skip(1))
        {
            var r = rank(formats[i]);
            if (Compare(r, bestRank) > 0) { best = i; bestRank = r; }
        }
        return best;
    }

    private static int Compare(double[] a, double[] b)
    {
        for (var k = 0; k < a.Length; k++)
        {
            var c = a[k].CompareTo(b[k]);
            if (c != 0) return c;
        }
        return 0;
    }
}
