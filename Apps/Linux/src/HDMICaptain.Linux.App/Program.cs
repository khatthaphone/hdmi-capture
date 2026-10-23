using HDMICaptain.Core;
using HDMICaptain.Linux.Media;

namespace HDMICaptain.Linux.App;

public static class Program
{
    public const string AppId = "com.example.HDMICaptain";

    public static int Main(string[] args)
    {
#if DEBUG
        const bool allowQa = true;
#else
        const bool allowQa = false;
#endif
        var options = LaunchOptions.Parse(args, allowQa);
        LinuxLog.Write($"launch {string.Join(' ', args)}");
        // No GC pauses in the frame path while live: the frame path allocates nothing in steady state (task 121).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        Adw.Module.Initialize();
        // NonUnique: every launch is its own process, like the other platforms (QA runs start next to the user's copy).
        var app = Adw.Application.New(AppId, Gio.ApplicationFlags.NonUnique);
        app.OnStartup += (_, _) =>
        {
            Adw.StyleManager.GetDefault().SetColorScheme(Adw.ColorScheme.ForceDark);
            if (Gdk.Display.GetDefault() is { } display)
                Gtk.IconTheme.GetForDisplay(display).AddSearchPath(Path.Combine(AppContext.BaseDirectory, "icons"));
            Gtk.Window.SetDefaultIconName(AppId);
        };
        var store = new PrefsStore();
        app.OnActivate += (_, _) =>
        {
            if (Viewers.Primary is { } open) open.Present();
            else Viewers.OpenPrimary(app, options, store);
        };
        // GApplication parses its own options; ours are already read.
        return app.RunWithSynchronizationContext([]);
    }
}
