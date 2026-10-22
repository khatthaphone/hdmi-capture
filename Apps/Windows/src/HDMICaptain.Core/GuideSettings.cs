using System.Text.Json.Serialization;

namespace HDMICaptain.Core;

/// <summary>Aspect masks for the guides overlay. JSON uses the Swift raw values ("2.39:1" ...).</summary>
public enum GuideMask
{
    [JsonStringEnumMemberName("2.39:1")] Scope,
    [JsonStringEnumMemberName("1.85:1")] Flat,
    [JsonStringEnumMemberName("4:3")] Classic,
    [JsonStringEnumMemberName("9:16")] Vertical,
    [JsonStringEnumMemberName("1:1")] Square,
}

/// <summary>
/// Framing overlays drawn above the video (not into it); port of CaptureCore's GuideSettings and the
/// Kotlin one. JSON keys are the same as the Swift Codable.
/// </summary>
public sealed record GuideSettings
{
    public bool Thirds { get; set; }
    public bool Center { get; set; }
    public bool SafeAreas { get; set; }
    public GuideMask? Mask { get; set; }
    public double Opacity { get; set; } = 0.6;

    [JsonIgnore]
    public bool IsEmpty => !Thirds && !Center && !SafeAreas && Mask is null;

    public static string Title(GuideMask mask) => mask switch
    {
        GuideMask.Scope => "2.39:1",
        GuideMask.Flat => "1.85:1",
        GuideMask.Classic => "4:3",
        GuideMask.Vertical => "9:16",
        _ => "1:1",
    };

    public static double Ratio(GuideMask mask) => mask switch
    {
        GuideMask.Scope => 2.39,
        GuideMask.Flat => 1.85,
        GuideMask.Classic => 4.0 / 3.0,
        GuideMask.Vertical => 9.0 / 16.0,
        _ => 1.0,
    };

    /// <summary>The visible region for a mask inside a video rect (letterbox or pillarbox).</summary>
    public static Rect MaskRect(GuideMask mask, Rect video)
    {
        var videoRatio = video.Width / video.Height;
        var ratio = Ratio(mask);
        if (ratio >= videoRatio)
        {
            var h = video.Width / ratio;
            return new Rect(video.X, video.Y + (video.Height - h) / 2, video.Width, h);
        }
        var w = video.Height * ratio;
        return new Rect(video.X + (video.Width - w) / 2, video.Y, w, video.Height);
    }
}
