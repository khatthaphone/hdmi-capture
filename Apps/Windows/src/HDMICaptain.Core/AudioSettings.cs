namespace HDMICaptain.Core;

/// <summary>How the two captured channels map to the output (CaptureCore's ChannelMode).</summary>
public enum ChannelMode { Auto, Normal, Swap, LeftToBoth, RightToBoth, Mono }

/// <summary>Card audio settings, as in CaptureCore's AudioPipeline.Settings. DelayMs is 0..500 to line sound up with a slower display.</summary>
public sealed record AudioSettings(
    int DelayMs = 0,
    ChannelMode ChannelMode = ChannelMode.Auto,
    float Volume = 1f,
    bool Muted = false);
