using System.Text.Json;

namespace HDMICaptain.Core;

/// <summary>
/// Window/app-level preferences (per-card settings live in <see cref="DevicePreset"/>); port of
/// Apps/Shared/Model/ViewerPrefs.swift with the same JSON keys, minus the Mac-only ones (allSpaces,
/// menu-bar item, streaming). Missing keys take defaults, unknown keys are ignored.
/// </summary>
public sealed record ViewerPrefs
{
    public bool FloatOnTop { get; set; }
    public bool CleanMode { get; set; }
    public bool LockAspect { get; set; } = true;
    public bool ShowStats { get; set; }
    public bool IncludeNonExternalCameras { get; set; }
    public GuideSettings Guides { get; set; } = new();

    /// <summary>"hevc" or "h264" (Recorder.Codec raw values).</summary>
    public string RecordingCodec { get; set; } = "hevc";
    public bool ShowRecordingIndicator { get; set; } = true;

    /// <summary>Sound follows the focused window or grid tile when several cards are open.</summary>
    public bool AudioFollowsFocus { get; set; } = true;

    /// <summary>Pro "Save Recent": keep the last RecentMinutes on disk.</summary>
    public bool RecentEnabled { get; set; }
    public int RecentMinutes { get; set; } = 15;

    /// <summary>Floating control bar when the cursor nears the bottom edge of a viewer window.</summary>
    public bool ShowControlsOnHover { get; set; } = true;

    public string Encode() => JsonSerializer.Serialize(this, CoreJsonContext.Default.ViewerPrefs);

    /// <summary>Tolerant: damaged or missing data gives the defaults.</summary>
    public static ViewerPrefs Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ViewerPrefs();
        try { return JsonSerializer.Deserialize(text, CoreJsonContext.Default.ViewerPrefs) ?? new ViewerPrefs(); }
        catch (JsonException) { return new ViewerPrefs(); }
    }
}
