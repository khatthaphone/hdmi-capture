using HDMICaptain.App.Views;
using HDMICaptain.Core;
using HDMICaptain.Media.Capture;
using Microsoft.UI.Xaml;

namespace HDMICaptain.App;

public partial class App : Application
{
    private static readonly List<MainWindow> OpenWindows = [];

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Write($"unhandled: {e.Exception}");
    }

    public static LaunchOptions Options { get; private set; } = LaunchOptions.Default;

    /// <summary>One prefs store for every window, so two viewers never race on prefs.json.</summary>
    public static PrefsStore Prefs { get; } = new();

    /// <summary>Opens a viewer for one card (Multi-View, task 098), or brings its window forward.</summary>
    public static void OpenCardWindow(string deviceKey)
    {
        // One reader per card per process: a card that already has a viewer just comes forward.
        if (OpenWindows.FirstOrDefault(w => w.CardKey == deviceKey || w.ShowingCard == deviceKey) is { } open)
        {
            open.Activate();
            return;
        }
        GridWindow.Open?.Release(deviceKey);
        Show(new MainWindow(Options, deviceKey));
        ClaimsChanged();
    }

    private static void Show(MainWindow window)
    {
        OpenWindows.Add(window);
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) FocusAudio(window);
        };
        window.Closed += (_, _) =>
        {
            OpenWindows.Remove(window);
            ClaimsChanged();
            // Closing the main window quits, like the Mac's last-window rule for the primary viewer.
            if (window.IsPrimary)
            {
                foreach (var other in OpenWindows.ToList()) other.Close();
                GridWindow.CloseIfOpen();
            }
        };
        window.Activate();
    }

    /// <summary>
    /// A card held by a card window other than <paramref name="asker"/>, or by the grid's own session
    /// (CaptureControllerOptions.IsClaimedElsewhere).
    /// </summary>
    public static bool IsClaimedByOther(MainWindow asker, string deviceKey) =>
        OpenWindows.Any(w => !ReferenceEquals(w, asker) && w.CardKey == deviceKey) || GridWindow.Open?.Owns(deviceKey) == true;

    /// <summary>The connected cards, as the main window's catalog sees them.</summary>
    public static IReadOnlyList<CaptureDevice> Devices => OpenWindows.FirstOrDefault(w => w.IsPrimary)?.Devices ?? [];

    /// <summary>The viewer window capturing <paramref name="deviceKey"/> right now, if any (the grid taps its frames).</summary>
    public static MainWindow? ViewerShowing(string deviceKey) => OpenWindows.FirstOrDefault(w => w.ShowingCard == deviceKey);

    /// <summary>A viewer is about to select <paramref name="deviceKey"/>: the grid hands its session over first.</summary>
    public static void ReleaseFromGrid(string deviceKey) => GridWindow.Open?.Release(deviceKey);

    /// <summary>The grid took or gave back a card.</summary>
    public static void NotifyClaimsChanged() => ClaimsChanged();

    /// <summary>The card window showing <paramref name="deviceKey"/>, if any.</summary>
    public static MainWindow? CardWindowFor(string deviceKey) => OpenWindows.FirstOrDefault(w => w.CardKey == deviceKey);

    private static void ClaimsChanged()
    {
        foreach (var window in OpenWindows) window.OnClaimsChanged();
    }

    /// <summary>"Play sound from the frontmost window": only the focused viewer (or the grid) is heard.</summary>
    public static void FocusAudio(Window focused)
    {
        var follow = Prefs.Prefs.AudioFollowsFocus;
        foreach (var window in OpenWindows) window.SetSilenced(follow && !ReferenceEquals(window, focused));
        if (GridWindow.Open is { } grid) grid.SetSilenced(follow && !ReferenceEquals(grid, focused));
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        const bool allowQa = true;
#else
        const bool allowQa = false;
#endif
        // Environment args include the exe path first.
        Options = LaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray(), allowQa);
        Show(new MainWindow(Options));
    }
}
