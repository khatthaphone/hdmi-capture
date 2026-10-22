using HDMICaptain.Core;
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
            if (window.IsPrimary) foreach (var other in OpenWindows.ToList()) other.Close();
        };
        window.Activate();
    }

    /// <summary>A card held by a card window other than <paramref name="asker"/> (CaptureControllerOptions.IsClaimedElsewhere).</summary>
    public static bool IsClaimedByOther(MainWindow asker, string deviceKey) =>
        OpenWindows.Any(w => !ReferenceEquals(w, asker) && w.CardKey == deviceKey);

    /// <summary>The card window showing <paramref name="deviceKey"/>, if any.</summary>
    public static MainWindow? CardWindowFor(string deviceKey) => OpenWindows.FirstOrDefault(w => w.CardKey == deviceKey);

    private static void ClaimsChanged()
    {
        foreach (var window in OpenWindows) window.OnClaimsChanged();
    }

    /// <summary>"Play sound from the frontmost window": only the focused viewer is heard.</summary>
    private static void FocusAudio(MainWindow focused)
    {
        var follow = Prefs.Prefs.AudioFollowsFocus;
        foreach (var window in OpenWindows) window.SetSilenced(follow && !ReferenceEquals(window, focused));
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
