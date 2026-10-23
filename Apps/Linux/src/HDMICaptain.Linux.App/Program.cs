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
        app.OnActivate += (_, _) => new MainWindow(app, options).Present();
        // GApplication parses its own options; ours are already read.
        return app.RunWithSynchronizationContext([]);
    }
}
