namespace HDMICaptain.Core;

public enum TestPatternKind { SmpteBars, FullBars, Grid, GrayRamp }

/// <summary>
/// Launch arguments shared with the Apple apps (`-testPattern smpteBars`, `-qaAction screenshot`, …).
/// `-testPattern` works in every build; the `-qa*` and `-debugUnlockPro` switches only in DEBUG.
/// </summary>
public sealed record LaunchOptions
{
    public static readonly LaunchOptions Default = new();

    public TestPatternKind? TestPattern { get; init; }
    public string? QaAction { get; init; }
    /// <summary>Shows one status screen without a card (screenshots of every state, task 094); capture doesn't start.</summary>
    public string? QaPhase { get; init; }
    public bool QaShowStats { get; init; }
    public bool QaPinControls { get; init; }
    public bool QaBuiltInCameras { get; init; }
    public int? QaSoakMinutes { get; init; }
    public bool DebugUnlockPro { get; init; }
    /// <summary>Windows only (DEBUG): `-videoSurface hwnd` uses the HWND swap chain instead of the SwapChainPanel (task 092).</summary>
    public string? VideoSurface { get; init; }
    /// <summary>Windows only (DEBUG): `-testPatternFormat nv12|yuy2|i420|i422|i444|bgra` picks the pattern's pixel layout.</summary>
    public string? TestPatternFormat { get; init; }

    public static LaunchOptions Parse(IReadOnlyList<string> args, bool allowQa)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var key = args[i];
            if (key.Length < 2 || key[0] != '-' || key[1] == '-') continue;
            var value = i + 1 < args.Count && !IsKey(args[i + 1]) ? args[++i] : "YES";
            values[key[1..]] = value;
        }

        var options = new LaunchOptions
        {
            TestPattern = values.TryGetValue("testPattern", out var p) ? ParsePattern(p) : null,
        };
        if (!allowQa) return options;
        return options with
        {
            QaAction = values.GetValueOrDefault("qaAction"),
            QaPhase = values.GetValueOrDefault("qaPhase"),
            QaShowStats = Flag(values, "qaShowStats"),
            QaPinControls = Flag(values, "qaPinControls"),
            QaBuiltInCameras = Flag(values, "qaBuiltInCameras"),
            QaSoakMinutes = values.TryGetValue("qaSoakMinutes", out var m) && int.TryParse(m, out var n) && n > 0 ? n : null,
            DebugUnlockPro = Flag(values, "debugUnlockPro"),
            VideoSurface = values.GetValueOrDefault("videoSurface"),
            TestPatternFormat = values.GetValueOrDefault("testPatternFormat"),
        };
    }

    // Negative numbers are values, not keys.
    private static bool IsKey(string s) => s.Length > 1 && s[0] == '-' && !char.IsAsciiDigit(s[1]);

    private static bool Flag(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && v.ToUpperInvariant() is "YES" or "TRUE" or "1";

    private static TestPatternKind? ParsePattern(string name) => name.ToLowerInvariant() switch
    {
        "smptebars" => TestPatternKind.SmpteBars,
        "fullbars" => TestPatternKind.FullBars,
        "grid" => TestPatternKind.Grid,
        "grayramp" => TestPatternKind.GrayRamp,
        _ => null,
    };
}
