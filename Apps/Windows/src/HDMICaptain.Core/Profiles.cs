using System.Text.Json;

namespace HDMICaptain.Core;

/// <summary>Picture looks (CaptureCore's ImageAdjustments.Preset), same values as on Apple.</summary>
public sealed record LookPreset(string Title, ImageAdjustments Values)
{
    public static readonly LookPreset Standard = new("Standard", new ImageAdjustments());
    public static readonly LookPreset Game = new("Game", new ImageAdjustments { Contrast = 1.08, Saturation = 1.15, Sharpness = 0.35 });
    public static readonly LookPreset Cinema = new("Cinema", new ImageAdjustments { Brightness = -0.02, Contrast = 1.05, Saturation = 0.92, Gamma = 0.95 });
    public static readonly LookPreset Vivid = new("Vivid", new ImageAdjustments { Contrast = 1.15, Saturation = 1.35, Sharpness = 0.2 });

    public static IReadOnlyList<LookPreset> All { get; } = [Standard, Game, Cinema, Vivid];

    /// <summary>The look these values match, if any.</summary>
    public static LookPreset? Of(ImageAdjustments adjustments) => All.FirstOrDefault(p => p.Values == adjustments);
}

/// <summary>The parts of a card's settings a profile captures (CaptureCore's ProfileSettings).</summary>
public sealed record ProfileSettings
{
    public FormatPreference Format { get; set; } = new FormatPreference.Auto();
    public ScaleMode Scale { get; set; } = ScaleMode.Fit;
    public FilterMode Filter { get; set; } = FilterMode.Linear;
    public ColorRange ColorRange { get; set; } = ColorRange.Auto;
    public ImageAdjustments Adjustments { get; set; } = new();
    public int AudioDelayMs { get; set; }
    public ChannelMode ChannelMode { get; set; } = ChannelMode.Auto;

    public ProfileSettings() { }

    public ProfileSettings(DevicePreset preset)
    {
        Format = preset.Format;
        Scale = preset.Scale;
        Filter = preset.Filter;
        ColorRange = preset.ColorRange;
        Adjustments = preset.Adjustments;
        AudioDelayMs = preset.AudioDelayMs;
        ChannelMode = preset.ChannelMode;
    }

    public DevicePreset ApplyTo(DevicePreset preset) => preset with
    {
        Format = Format,
        Scale = Scale,
        Filter = Filter,
        ColorRange = ColorRange,
        Adjustments = Adjustments,
        AudioDelayMs = AudioDelayMs,
        ChannelMode = ChannelMode,
    };
}

/// <summary>A named bundle of settings, e.g. "Retro" for an old console (CaptureCore's Profile).</summary>
public sealed record Profile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ProfileSettings Settings { get; set; } = new();
    public bool IsBuiltIn { get; set; }

    /// <summary>Same ids and settings as the Apple built-ins.</summary>
    public static IReadOnlyList<Profile> BuiltIns { get; } =
    [
        new() { Id = "00000000-0000-0000-0000-00000000B001", Name = "Modern Console", Settings = new ProfileSettings(), IsBuiltIn = true },
        new()
        {
            Id = "00000000-0000-0000-0000-00000000B002", Name = "Retro", IsBuiltIn = true,
            Settings = new ProfileSettings { Scale = ScaleMode.Integer, Filter = FilterMode.Nearest },
        },
        new()
        {
            Id = "00000000-0000-0000-0000-00000000B003", Name = "Camera", IsBuiltIn = true,
            Settings = new ProfileSettings { Adjustments = LookPreset.Cinema.Values },
        },
        new()
        {
            Id = "00000000-0000-0000-0000-00000000B004", Name = "Desktop", IsBuiltIn = true,
            Settings = new ProfileSettings { Format = new FormatPreference.Pinned(1920, 1080, null), Adjustments = new ImageAdjustments { Sharpness = 0.25 } },
        },
    ];

    /// <summary>Only user profiles are stored; the built-ins come from code.</summary>
    public static string Encode(IEnumerable<Profile> profiles) =>
        JsonSerializer.Serialize(profiles.Where(p => !p.IsBuiltIn).ToList(), CoreJsonContext.Default.ListProfile);

    /// <summary>Tolerant: a damaged list gives no user profiles rather than a crash.</summary>
    public static IReadOnlyList<Profile> Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            var list = JsonSerializer.Deserialize(text, CoreJsonContext.Default.ListProfile);
            return list?.Select(p => p with { IsBuiltIn = false }).ToList() ?? [];
        }
        catch (JsonException) { return []; }
    }
}
