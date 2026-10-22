using CommunityToolkit.Mvvm.ComponentModel;
using HDMICaptain.Core;
using Microsoft.UI.Xaml;

namespace HDMICaptain.App.ViewModels;

/// <summary>What the main window shows instead of video (port of the Mac's ViewerModel.Phase).</summary>
public abstract record ViewerPhase
{
    private ViewerPhase() { }

    public sealed record Scanning : ViewerPhase;
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
    public static StatusCopy NoSignal(SignalContent content) => new("\uE7F4", "No HDMI signal",
        content is SignalContent.Black
            ? "The card receives a black picture. If the source is a PlayStation or Apple TV, turn off HDCP in its settings."
            : "Check that the source is on and the HDMI cable is plugged in.",
        Secondary: StatusAction.Help);

    public static StatusCopy? For(ViewerPhase phase) => phase switch
    {
        ViewerPhase.Scanning => new("\uE8EA", "Connect a capture card",
            $"Plug an HDMI capture card into this {Device}. The picture appears automatically.",
            Secondary: StatusAction.Help),
        ViewerPhase.PermissionDenied => new("\uE8A6", "Camera access required",
            "Turn on camera access for desktop apps in Settings › Privacy & security › Camera, then come back.",
            Primary: StatusAction.OpenPrivacySettings, Secondary: StatusAction.CheckAgain),
        ViewerPhase.Connecting c => new("", $"Connecting to {c.Name}…", "Starting the capture card.", Spinner: true),
        ViewerPhase.Waiting w => new("\uE8CD", $"{w.Name} is disconnected", "Plug it back in to continue where you left off."),
        ViewerPhase.Recovering r => Recovering(r.Reason),
        ViewerPhase.Paused p => new("\uE769", "Paused", PauseMessage(p.Reason)),
        ViewerPhase.Failed f => new("\uE7BA", "The capture card stopped responding",
            "Try reconnecting. If that doesn't help, unplug the card and plug it back in.", f.Detail, Primary: StatusAction.Reconnect),
        _ => null,
    };

    private static StatusCopy Recovering(string reason) => reason switch
    {
        RecoveryReason.NoSignal => new("\uE7F4", "No HDMI signal", "Check that the source is on and the HDMI cable is plugged in.",
            Secondary: StatusAction.Help),
        RecoveryReason.Disconnected or RecoveryReason.Waiting => new("\uE8CD", "Capture card disconnected", "Plug it back in to continue."),
        _ => new("", "Reconnecting…", "The capture card stopped sending video. Reconnecting automatically.", Spinner: true),
    };

    private static string PauseMessage(PauseReason reason) => reason switch
    {
        PauseReason.InUseByAnotherApp => "Another app is using the capture card. The picture comes back when it lets go.",
        PauseReason.Sleep => $"The {Device} is going to sleep. The picture comes back when it wakes.",
        PauseReason.PrivacyToggle => "Camera access was turned off in Settings › Privacy & security › Camera.",
        _ => "Capture is paused.",
    };
}

public sealed record StatusAction(string Title, string Id)
{
    public static readonly StatusAction Help = new("Connection Help…", "help");
    public static readonly StatusAction OpenPrivacySettings = new("Open Settings", "privacy");
    public static readonly StatusAction CheckAgain = new("Check Again", "retry");
    public static readonly StatusAction Reconnect = new("Reconnect", "retry");
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
    public partial string InputName { get; set; } = "No Input";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteGlyph))]
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
    public Visibility StatsVisibility => ShowDiagnostics ? Visibility.Visible : Visibility.Collapsed;
    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";
    public string FullScreenGlyph => IsFullScreen ? "\uE73F" : "\uE740";

    public static string ScaleTitle(ScaleMode mode) => mode switch
    {
        ScaleMode.Fit => "Fit",
        ScaleMode.Fill => "Fill (crop)",
        ScaleMode.Stretch => "Stretch",
        _ => "Integer (pixel-perfect)",
    };
}
