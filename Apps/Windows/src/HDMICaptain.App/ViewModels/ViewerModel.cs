using CommunityToolkit.Mvvm.ComponentModel;
using HDMICaptain.Core;
using Microsoft.UI.Xaml;

namespace HDMICaptain.App.ViewModels;

/// <summary>What the main window shows instead of video (port of the Mac's ViewerModel.Phase).</summary>
public abstract record ViewerPhase
{
    private ViewerPhase() { }

    public sealed record Scanning : ViewerPhase;
    /// <summary>First run: explains the camera/microphone prompts before capture starts (task 062 on Mac).</summary>
    public sealed record Welcome : ViewerPhase;
    public sealed record PermissionDenied : ViewerPhase;
    public sealed record Connecting(string Name) : ViewerPhase;
    public sealed record Waiting(string Name) : ViewerPhase;
    public sealed record Recovering(string Reason) : ViewerPhase;
    public sealed record Paused(PauseReason Reason) : ViewerPhase;
    public sealed record Failed(string Detail) : ViewerPhase;
    public sealed record Live : ViewerPhase;
}

/// <summary>A status card's words and buttons; copy from docs/copy.md with Windows wording.</summary>
public sealed record StatusCopy(string Glyph, string Title, string Message, string? Detail = null, bool Spinner = false,
    StatusAction? Primary = null, StatusAction? Secondary = null)
{
    public const string Device = "PC";

    /// <summary>The card is live but shows no picture (its own "no signal" screen, or black): NoSignalOverlay.</summary>
    public static StatusCopy NoSignal(SignalContent content) => new("\uE7F4", Strings.Get("Status_NoSignalTitle"),
        content is SignalContent.Black
            ? Strings.Get("Status_NoSignalBlackMessage")
            : Strings.Get("Status_NoSignalMessage"),
        Secondary: StatusAction.Help);

    public static StatusCopy? For(ViewerPhase phase) => phase switch
    {
        ViewerPhase.Welcome => new("\uE714", Strings.Get("Status_WelcomeTitle"),
            Strings.Format("Status_WelcomeMessage", Device),
            Primary: StatusAction.Continue),
        ViewerPhase.Scanning => new("\uE714", Strings.Get("Status_ScanningTitle"),
            Strings.Format("Status_ScanningMessage", Device),
            Secondary: StatusAction.Help),
        ViewerPhase.PermissionDenied => new("\uE8A6", Strings.Get("Status_PermissionTitle"),
            Strings.Get("Status_PermissionMessage"),
            Primary: StatusAction.OpenPrivacySettings, Secondary: StatusAction.CheckAgain),
        ViewerPhase.Connecting c => new("", Strings.Format("Status_ConnectingTitle", c.Name), Strings.Get("Status_ConnectingMessage"), Spinner: true),
        ViewerPhase.Waiting w => new("\uE8CD", Strings.Format("Status_WaitingTitle", w.Name), Strings.Get("Status_WaitingMessage")),
        ViewerPhase.Recovering r => Recovering(r.Reason),
        ViewerPhase.Paused p => new("\uE769", Strings.Get("Status_PausedTitle"), PauseMessage(p.Reason)),
        ViewerPhase.Failed f => new("\uE7BA", Strings.Get("Status_FailedTitle"),
            Strings.Get("Status_FailedMessage"), f.Detail, Primary: StatusAction.Reconnect),
        _ => null,
    };

    private static StatusCopy Recovering(string reason) => reason switch
    {
        RecoveryReason.NoSignal => new("\uE7F4", Strings.Get("Status_NoSignalTitle"), Strings.Get("Status_NoSignalMessage"),
            Secondary: StatusAction.Help),
        RecoveryReason.Disconnected or RecoveryReason.Waiting => new("\uE8CD", Strings.Get("Status_DisconnectedTitle"), Strings.Get("Status_DisconnectedMessage")),
        _ => new("", Strings.Get("Status_ReconnectingTitle"), Strings.Get("Status_ReconnectingMessage"), Spinner: true),
    };

    private static string PauseMessage(PauseReason reason) => reason switch
    {
        PauseReason.InUseByAnotherApp => Strings.Get("Pause_InUse"),
        PauseReason.Sleep => Strings.Format("Pause_Sleep", Device),
        PauseReason.PrivacyToggle => Strings.Get("Pause_Privacy"),
        _ => Strings.Get("Pause_Default"),
    };
}

/// <summary>DEBUG <c>-qaPhase</c> names → the screen to show (every status screen, for screenshots and Narrator checks).</summary>
public static class QaPhases
{
    public static readonly string[] All =
        ["welcome", "scanning", "permission", "connecting", "waiting", "noSignal", "black", "reconnecting", "disconnected", "inUse", "sleep", "privacyOff", "failed"];

    public static (ViewerPhase Phase, SignalContent Signal)? Parse(string name) => name switch
    {
        "welcome" => (new ViewerPhase.Welcome(), new SignalContent.Picture()),
        "scanning" => (new ViewerPhase.Scanning(), new SignalContent.Picture()),
        "permission" => (new ViewerPhase.PermissionDenied(), new SignalContent.Picture()),
        "connecting" => (new ViewerPhase.Connecting("UGREEN-25854"), new SignalContent.Picture()),
        "waiting" => (new ViewerPhase.Waiting("UGREEN-25854"), new SignalContent.Picture()),
        "noSignal" => (new ViewerPhase.Live(), new SignalContent.Solid(0, 0, 255)),
        "black" => (new ViewerPhase.Live(), new SignalContent.Black()),
        "reconnecting" => (new ViewerPhase.Recovering(RecoveryReason.NoFrames), new SignalContent.Picture()),
        "disconnected" => (new ViewerPhase.Recovering(RecoveryReason.Disconnected), new SignalContent.Picture()),
        "inUse" => (new ViewerPhase.Paused(PauseReason.InUseByAnotherApp), new SignalContent.Picture()),
        "sleep" => (new ViewerPhase.Paused(PauseReason.Sleep), new SignalContent.Picture()),
        "privacyOff" => (new ViewerPhase.Paused(PauseReason.PrivacyToggle), new SignalContent.Picture()),
        "failed" => (new ViewerPhase.Failed("MF_E_HW_MFT_FAILED_START_STREAMING (0xC00D3704)"), new SignalContent.Picture()),
        _ => null,
    };
}

public sealed record StatusAction(string Title, string Id)
{
    public static readonly StatusAction Help = new(Strings.Get("Action_Help"), "help");
    public static readonly StatusAction Continue = new(Strings.Get("Action_Continue"), "welcome");
    public static readonly StatusAction OpenPrivacySettings = new(Strings.Get("Action_OpenSettings"), "privacy");
    public static readonly StatusAction CheckAgain = new(Strings.Get("Action_CheckAgain"), "retry");
    public static readonly StatusAction Reconnect = new(Strings.Get("Action_Reconnect"), "retry");
}

/// <summary>
/// The main window's state for x:Bind: phase and status card, input name, and the view toggles that the
/// control bar, context menu and shortcuts share. Window effects (presenter, renderer) live in MainWindow.
/// </summary>
public sealed partial class ViewerModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusVisibility), nameof(IsLive))]
    public partial ViewerPhase Phase { get; set; } = new ViewerPhase.Scanning();

    /// <summary>What the live picture shows; "no signal" covers the held last frame with the status card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status), nameof(StatusVisibility))]
    public partial SignalContent Signal { get; set; } = new SignalContent.Picture();

    [ObservableProperty]
    public partial string InputName { get; set; } = Strings.Get("Input_None");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteGlyph), nameof(MuteName))]
    public partial bool Muted { get; set; }

    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial ScaleMode Scale { get; set; } = ScaleMode.Fit;

    [ObservableProperty]
    public partial bool SharpPixels { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatsVisibility))]
    public partial bool ShowDiagnostics { get; set; }

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool CleanMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenGlyph))]
    public partial bool IsFullScreen { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; } = "";

    public StatusCopy? Status => Phase is ViewerPhase.Live && Signal.IsNoSignal ? StatusCopy.NoSignal(Signal) : StatusCopy.For(Phase);
    public Visibility StatusVisibility => Status is null ? Visibility.Collapsed : Visibility.Visible;
    public bool IsLive => Phase is ViewerPhase.Live;
    /// <summary>DEBUG -qaShowStats: shows diagnostics for this run only, never saved to prefs (CLAUDE.md QA rule).</summary>
    public bool QaDiagnostics { get; init; }

    public bool DiagnosticsShown => ShowDiagnostics || QaDiagnostics;

    public Visibility StatsVisibility => DiagnosticsShown ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>What the mute button does now (Narrator name and tooltip).</summary>
    public string MuteName => Muted ? Strings.Get("Control_Unmute") : Strings.Get("Control_Mute");

    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";
    public string FullScreenGlyph => IsFullScreen ? "\uE73F" : "\uE740";

    public static string ScaleTitle(ScaleMode mode) => mode switch
    {
        ScaleMode.Fit => Strings.Get("Scale_Fit"),
        ScaleMode.Fill => Strings.Get("Scale_Fill"),
        ScaleMode.Stretch => Strings.Get("Scale_Stretch"),
        _ => Strings.Get("Scale_Integer"),
    };
}
