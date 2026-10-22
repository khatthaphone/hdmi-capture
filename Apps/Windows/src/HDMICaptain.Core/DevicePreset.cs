using System.Text.Json;

namespace HDMICaptain.Core;

/// <summary>Picture look, as CaptureCore's ImageAdjustments; identity = (0, 1, 1, 1, 0).</summary>
public sealed record ImageAdjustments
{
    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public double Gamma { get; set; } = 1.0;
    public double Sharpness { get; set; }

    public bool IsIdentity => this == new ImageAdjustments();
}

/// <summary>
/// Per-card settings (CaptureCore's DevicePreset, Kotlin DevicePreset), remembered by the card's stable
/// key (<see cref="DeviceKey"/>) so a card gets its own format, picture and sound back whenever it is
/// plugged in. JSON keys match the Swift Codable (camelCase, enum raw values camelCase); missing keys take
/// defaults and unknown keys are ignored, so saved presets survive upgrades both ways.
/// </summary>
public sealed record DevicePreset
{
    public FormatPreference Format { get; set; } = new FormatPreference.Auto();
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public ScaleMode Scale { get; set; } = ScaleMode.Fit;
    public FilterMode Filter { get; set; } = FilterMode.Linear;
    public ColorRange ColorRange { get; set; } = ColorRange.Auto;
    public int AudioDelayMs { get; set; }
    public ChannelMode ChannelMode { get; set; } = ChannelMode.Auto;
    public ImageAdjustments Adjustments { get; set; } = new();

    /// <summary>Path of a .cube file the user picked (Swift keeps a security-scoped bookmark, Android a content URI).</summary>
    public string? LutPath { get; set; }
    public string? LutName { get; set; }
    public double LutIntensity { get; set; } = 1.0;

    public RenderOptions RenderOptions => new(Scale, Filter, ColorRange);

    public AudioSettings AudioSettings => new(AudioDelayMs, ChannelMode, Volume, Muted);

    public DevicePreset With(RenderOptions options) => this with { Scale = options.Scale, Filter = options.Filter, ColorRange = options.ColorRange };

    public DevicePreset With(AudioSettings audio) =>
        this with { AudioDelayMs = audio.DelayMs, ChannelMode = audio.ChannelMode, Volume = audio.Volume, Muted = audio.Muted };

    public string Encode() => JsonSerializer.Serialize(this, CoreJsonContext.Default.DevicePreset);

    /// <summary>Tolerant: damaged or missing data gives the default preset.</summary>
    public static DevicePreset Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new DevicePreset();
        try { return JsonSerializer.Deserialize(text, CoreJsonContext.Default.DevicePreset) ?? new DevicePreset(); }
        catch (JsonException) { return new DevicePreset(); }
    }
}
