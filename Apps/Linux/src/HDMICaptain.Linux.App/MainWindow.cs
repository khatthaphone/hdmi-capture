using HDMICaptain.Core;
using HDMICaptain.Linux.Media;
using HDMICaptain.Linux.Media.Render;

namespace HDMICaptain.Linux.App;

/// <summary>The viewer window: the video surface, with a status page on top until there is a picture.</summary>
public sealed class MainWindow
{
    private readonly Adw.ApplicationWindow _window;
    private readonly GlesRenderer _renderer = new();
    private readonly VideoView _video;
    private Timer? _statsLog;

    public MainWindow(Adw.Application app, LaunchOptions options)
    {
        _window = Adw.ApplicationWindow.New(app);
        _window.SetTitle("HDMI Captain");
        _window.SetDefaultSize(960, 540);

        _video = new VideoView(_renderer);
        var overlay = Gtk.Overlay.New();
        overlay.SetChild(_video.Widget);

        if (options.TestPattern is { } pattern)
        {
            _renderer.StartPattern(pattern);
        }
        else
        {
            var page = Adw.StatusPage.New();
            page.SetTitle("Connect a capture card");
            page.SetIconName("camera-video-symbolic");
            overlay.AddOverlay(page);
        }

        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(overlay);
        _window.SetContent(toolbar);
#if DEBUG
        // Frame rates in the log every 5 s (task 107: does WSLg keep up with 60 fps?).
        var last = _renderer.Stats;
        _statsLog = new Timer(_ =>
        {
            var now = _renderer.Stats;
            LinuxLog.Write($"stats: in {(now.FramesIn - last.FramesIn) / 5.0:F1} fps, drawn {(now.FramesDrawn - last.FramesDrawn) / 5.0:F1} fps, " +
                           $"{now.Width}x{now.Height}, arrival→draw {now.LastLatencyNs / 1e6:F1} ms");
            last = now;
        }, null, 5000, 5000);
#endif
        _window.OnCloseRequest += (_, _) =>
        {
            _statsLog?.Dispose();
            _renderer.StopPattern();
            return false;
        };
    }

    public void Present() => _window.Present();
}
