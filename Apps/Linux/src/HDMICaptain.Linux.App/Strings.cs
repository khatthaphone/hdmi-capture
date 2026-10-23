namespace HDMICaptain.Linux.App;

/// <summary>
/// User-facing text (docs/copy.md), English. Same words as the Windows .resw where the platforms agree; the
/// permission, sleep and help lines say what is true on Linux. Translations come later through gettext.
/// </summary>
public static class Strings
{
    public const string Device = "computer";

    public const string ActionCheckAgain = "Check Again";
    public const string ActionHelp = "Connection Help…";
    public const string ActionReconnect = "Reconnect";

    public const string ControlMute = "Mute";
    public const string ControlUnmute = "Unmute";
    public const string ControlFullScreen = "Full Screen";
    public const string ControlExitFullScreen = "Exit Full Screen";
    public const string ControlScreenshot = "Save Screenshot";
    public const string ControlSettings = "Settings";
    public const string ControlMenu = "More";
    public const string ControlVolume = "Volume";

    public const string InputNone = "No Input";

    public const string PauseDefault = "Capture is paused.";
    public const string PauseInUse = "Another app is using the capture card. The picture comes back when it lets go.";
    public const string PauseNoAccess = "HDMI Captain isn't allowed to open the capture card. Add your user to the video group (sudo usermod -aG video $USER), log out and back in, then choose Check Again.";
    public const string PauseSleep = "The computer is going to sleep. The picture comes back when it wakes.";

    public const string ScaleFit = "Fit";
    public const string ScaleFill = "Fill (crop)";
    public const string ScaleStretch = "Stretch";
    public const string ScaleInteger = "Integer (pixel-perfect)";

    public const string StatusConnectingMessage = "Starting the capture card.";
    public const string StatusConnectingTitle = "Connecting to {0}…";
    public const string StatusDisconnectedMessage = "Plug it back in to continue.";
    public const string StatusDisconnectedTitle = "Capture card disconnected";
    public const string StatusFailedMessage = "Try reconnecting. If that doesn't help, unplug the card and plug it back in.";
    public const string StatusFailedTitle = "The capture card stopped responding";
    public const string StatusNoSignalBlackMessage = "The card receives a black picture. If the source is a PlayStation or Apple TV, turn off HDCP in its settings.";
    public const string StatusNoSignalMessage = "Check that the source is on and the HDMI cable is plugged in.";
    public const string StatusNoSignalTitle = "No HDMI signal";
    public const string StatusPausedTitle = "Paused";
    public const string StatusNoAccessTitle = "Can't open the capture card";
    public const string StatusReconnectingMessage = "The capture card stopped sending video. Reconnecting automatically.";
    public const string StatusReconnectingTitle = "Reconnecting…";
    public const string StatusScanningMessage = "Plug an HDMI capture card into this computer. The picture appears automatically.";
    public const string StatusScanningTitle = "Connect a capture card";
    public const string StatusWaitingMessage = "Plug it back in to continue where you left off.";
    public const string StatusWaitingTitle = "{0} is disconnected";
    public const string StatusNoGl = "Can't show video";

    public const string AudioBadge = "No sound from the capture card";

    public const string ToastFrameCopied = "Frame copied";
    public const string ToastNoPictureToCapture = "No picture to capture yet";
    public const string ToastNoPictureToCopy = "No picture to copy yet";
    public const string ToastScreenshotSaved = "Screenshot saved to Pictures › HDMI Captain";
    public const string ToastScreenshotFailed = "Couldn't save the screenshot: {0}";

    public const string HelpTitle = "Connection Help";
    public const string HelpNotFoundTitle = "Capture card not found";
    public static readonly string[] HelpNotFound =
    [
        "Use a UVC capture card (most USB HDMI capture cards are); it appears as /dev/video* without a driver.",
        "Try another USB port or cable; some cables only charge.",
        "Your user needs access to the camera devices: most desktops grant it, otherwise add yourself to the video group.",
    ];
    public const string HelpNoSignalTitle = "No HDMI signal";
    public static readonly string[] HelpNoSignal =
    [
        "Turn the source on and make sure it isn't asleep.",
        "Unplug the HDMI cable at both ends and plug it back in.",
        "Pick the source's HDMI output (not its built-in screen) and a standard resolution such as 1080p.",
    ];
    public const string HelpBlackTitle = "Black picture from a PlayStation or Apple TV";
    public static readonly string[] HelpBlack =
    [
        "These sources protect video with HDCP, and capture cards can't show protected video.",
        "PS5: Settings › System › HDMI › turn off Enable HDCP. PS4: Settings › System › turn off Enable HDCP.",
        "Apple TV and streaming apps always use HDCP for movies and TV; games and menus usually work.",
    ];
    public const string HelpLagTitle = "Picture lags or drops out";
    public static readonly string[] HelpLag =
    [
        "Plug the card straight into the computer, not through a hub, when you can.",
        "Two cards on one USB 2.0 bus share bandwidth; use separate ports or lower the format in Settings.",
        "If the card keeps disconnecting, USB power saving may be switching it off: power tools like TLP or powertop can re-enable it.",
    ];
    public const string HelpSoundTitle = "No sound";
    public static readonly string[] HelpSound =
    [
        "Check that the source isn't muted and that sound is on in HDMI Captain (speaker button on the bar).",
        "Choose the output in your desktop's sound settings; HDMI Captain plays to the default output.",
        "If another app is recording from the card's microphone, close it.",
    ];

    public static string Format(string template, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, template, args);
}
