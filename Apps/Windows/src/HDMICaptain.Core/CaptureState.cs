namespace HDMICaptain.Core;

/// <summary>What the capture card is delivering right now.</summary>
public sealed record LiveInfo(string DeviceName, int Width, int Height, double FrameRate, string FourCC);

/// <summary>Port of CaptureCore's CaptureState (and the Kotlin one); the UI renders one screen per case.</summary>
public abstract record CaptureState
{
    private CaptureState() { }

    public sealed record Idle : CaptureState;

    public sealed record Connecting(string DeviceName) : CaptureState;

    public sealed record Live(LiveInfo Info) : CaptureState;

    /// <summary>Lost the stream; the controller is retrying on its own.</summary>
    public sealed record Recovering(string Reason) : CaptureState;

    /// <summary>Windows took the camera away for now (in use by another app, sleep); capture resumes by itself when the reason goes away.</summary>
    public sealed record Paused(PauseReason Reason) : CaptureState;

    public sealed record Failed(string Message) : CaptureState;
}

/// <summary>Why capture is paused. Mirrors CaptureCore's PauseReason with the causes Windows reports.</summary>
public enum PauseReason
{
    /// <summary>Another app took the camera (MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED).</summary>
    InUseByAnotherApp,
    /// <summary>The system is going to sleep.</summary>
    Sleep,
    /// <summary>Camera privacy is off for this app; the card would show nothing.</summary>
    PrivacyToggle,
    Other,
}

/// <summary>Recovery reasons shown to the user; same strings as the Apple engine (see StatusCopy).</summary>
public static class RecoveryReason
{
    public const string NoSignal = "No signal";
    public const string Disconnected = "Device disconnected";
    public const string Waiting = "Waiting for device";
    public const string Stopped = "Capture stopped";
    /// <summary>Streaming started but no frame arrived.</summary>
    public const string NoFrames = "No frames";
}

public static class RecoveryBackoff
{
    /// <summary>Restart delay for the n-th consecutive attempt (1-based): 0.25 s doubling, capped at 4 s. Shared fixture: recovery-backoff.json.</summary>
    public static double RestartDelaySeconds(int attempt) => Math.Min(0.25 * Math.Pow(2.0, attempt - 1), 4.0);
}
