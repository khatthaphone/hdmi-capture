using HDMICaptain.Core;

namespace HDMICaptain.Linux.App;

/// <summary>What the main window shows instead of video (port of the Mac/Windows ViewerModel.Phase).</summary>
public abstract record ViewerPhase
{
    private ViewerPhase() { }

    public sealed record Scanning : ViewerPhase;
    public sealed record Connecting(string Name) : ViewerPhase;
    public sealed record Waiting(string Name) : ViewerPhase;
    public sealed record Recovering(string Reason) : ViewerPhase;
    public sealed record Paused(PauseReason Reason) : ViewerPhase;
    public sealed record Failed(string Detail) : ViewerPhase;
    public sealed record NoSignal(bool Black) : ViewerPhase;
    public sealed record Live : ViewerPhase;

    /// <summary>The controller's state seen from the window (the device name for Waiting comes from the current card).</summary>
    public static ViewerPhase From(CaptureState state, string? deviceName) => state switch
    {
        CaptureState.Idle => new Scanning(),
        CaptureState.Connecting c => new Connecting(c.DeviceName),
        CaptureState.Live => new Live(),
        CaptureState.Recovering { Reason: RecoveryReason.Waiting } when deviceName != null => new Waiting(deviceName),
        CaptureState.Recovering { Reason: RecoveryReason.Waiting } => new Scanning(),
        CaptureState.Recovering r => new Recovering(r.Reason),
        CaptureState.Paused p => new Paused(p.Reason),
        CaptureState.Failed f => new Failed(f.Message),
        _ => new Scanning(),
    };
}

public enum StatusActionId { Help, Retry }

public sealed record StatusAction(string Title, StatusActionId Id)
{
    public static readonly StatusAction Help = new(Strings.ActionHelp, StatusActionId.Help);
    public static readonly StatusAction CheckAgain = new(Strings.ActionCheckAgain, StatusActionId.Retry);
    public static readonly StatusAction Reconnect = new(Strings.ActionReconnect, StatusActionId.Retry);
}

/// <summary>A status card: symbolic icon name, words and buttons.</summary>
public sealed record StatusCopy(string Icon, string Title, string Message, string? Detail = null, bool Spinner = false,
    StatusAction? Primary = null, StatusAction? Secondary = null)
{
    public static StatusCopy? For(ViewerPhase phase) => phase switch
    {
        ViewerPhase.Scanning => new("video-display-symbolic", Strings.StatusScanningTitle, Strings.StatusScanningMessage, Secondary: StatusAction.Help),
        ViewerPhase.Connecting c => new("", Strings.Format(Strings.StatusConnectingTitle, c.Name), Strings.StatusConnectingMessage, Spinner: true),
        ViewerPhase.Waiting w => new("media-eject-symbolic", Strings.Format(Strings.StatusWaitingTitle, w.Name), Strings.StatusWaitingMessage),
        ViewerPhase.Recovering r => r.Reason switch
        {
            RecoveryReason.NoSignal => new("network-offline-symbolic", Strings.StatusNoSignalTitle, Strings.StatusNoSignalMessage, Secondary: StatusAction.Help),
            RecoveryReason.Disconnected => new("media-eject-symbolic", Strings.StatusDisconnectedTitle, Strings.StatusDisconnectedMessage),
            _ => new("", Strings.StatusReconnectingTitle, Strings.StatusReconnectingMessage, Spinner: true),
        },
        ViewerPhase.Paused { Reason: PauseReason.PrivacyToggle } => new("dialog-password-symbolic", Strings.StatusNoAccessTitle, Strings.PauseNoAccess,
            Primary: StatusAction.CheckAgain, Secondary: StatusAction.Help),
        ViewerPhase.Paused p => new("media-playback-pause-symbolic", Strings.StatusPausedTitle, p.Reason switch
        {
            PauseReason.InUseByAnotherApp => Strings.PauseInUse,
            PauseReason.Sleep => Strings.PauseSleep,
            _ => Strings.PauseDefault,
        }),
        ViewerPhase.Failed f => new("dialog-warning-symbolic", Strings.StatusFailedTitle, Strings.StatusFailedMessage, f.Detail, Primary: StatusAction.Reconnect),
        ViewerPhase.NoSignal n => new("network-offline-symbolic", Strings.StatusNoSignalTitle,
            n.Black ? Strings.StatusNoSignalBlackMessage : Strings.StatusNoSignalMessage, Secondary: StatusAction.Help),
        _ => null,
    };
}

/// <summary>DEBUG <c>-qaPhase</c> names → the screen to show without a card (screenshots, accessibility checks).</summary>
public static class QaPhases
{
    public static readonly string[] All =
        ["scanning", "connecting", "waiting", "noSignal", "black", "reconnecting", "disconnected", "inUse", "sleep", "noAccess", "failed", "audioMissing"];

    public static ViewerPhase? Parse(string name) => name switch
    {
        "scanning" => new ViewerPhase.Scanning(),
        "connecting" => new ViewerPhase.Connecting("UGREEN-25854"),
        "waiting" => new ViewerPhase.Waiting("UGREEN-25854"),
        "noSignal" => new ViewerPhase.NoSignal(false),
        "black" => new ViewerPhase.NoSignal(true),
        "reconnecting" => new ViewerPhase.Recovering(RecoveryReason.NoFrames),
        "disconnected" => new ViewerPhase.Recovering(RecoveryReason.Disconnected),
        "inUse" => new ViewerPhase.Paused(PauseReason.InUseByAnotherApp),
        "sleep" => new ViewerPhase.Paused(PauseReason.Sleep),
        "noAccess" or "privacyOff" or "permission" => new ViewerPhase.Paused(PauseReason.PrivacyToggle),
        "failed" => new ViewerPhase.Failed("EIO (VIDIOC_STREAMON)"),
        "audioMissing" or "micDenied" => new ViewerPhase.Live(),
        _ => null,
    };
}
