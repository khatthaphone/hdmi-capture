using HDMICaptain.Core;
using Microsoft.UI.Xaml;

namespace HDMICaptain.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Write($"unhandled: {e.Exception}");
    }

    public static LaunchOptions Options { get; private set; } = LaunchOptions.Default;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        const bool allowQa = true;
#else
        const bool allowQa = false;
#endif
        // Environment args include the exe path first.
        Options = LaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray(), allowQa);
        _window = new MainWindow(Options);
        _window.Activate();
    }
}
