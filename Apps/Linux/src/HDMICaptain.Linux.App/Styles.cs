namespace HDMICaptain.Linux.App;

/// <summary>App CSS: a black stage around the video and readable OSD overlays (libadwaita's osd class does most of it).</summary>
public static class Styles
{
    private const string Css = """
        .hc-viewer .hc-status { background-color: alpha(black, 0.72); color: white; }
        .hc-bar { border-radius: 999px; padding: 4px 10px; background-color: alpha(black, 0.66); color: white; }
        .hc-bar button, .hc-bar menubutton > button { min-width: 34px; min-height: 34px; border-radius: 999px; background: none; color: white; }
        .hc-bar button:hover, .hc-bar menubutton > button:hover { background-color: alpha(white, 0.14); }
        .hc-bar scale { margin: 0 6px; }
        .hc-badge { border-radius: 999px; padding: 6px 14px; }
        .hc-stats { border-radius: 8px; padding: 6px 10px; font-size: 0.85em; }
        """;

    private static bool _loaded;

    public static void Load()
    {
        if (_loaded || Gdk.Display.GetDefault() is not { } display) return;
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(Css);
        Gtk.StyleContext.AddProviderForDisplay(display, provider, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);
        _loaded = true;
    }
}
