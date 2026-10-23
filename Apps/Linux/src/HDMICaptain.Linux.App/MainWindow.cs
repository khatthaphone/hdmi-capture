using HDMICaptain.Core;

namespace HDMICaptain.Linux.App;

/// <summary>The viewer window. For now a placeholder title page; the video surface arrives with task 107.</summary>
public sealed class MainWindow
{
    private readonly Adw.ApplicationWindow _window;

    public MainWindow(Adw.Application app, LaunchOptions options)
    {
        _window = Adw.ApplicationWindow.New(app);
        _window.SetTitle("HDMI Captain");
        _window.SetDefaultSize(960, 540);

        var page = Adw.StatusPage.New();
        page.SetTitle("HDMI Captain");
        page.SetDescription(options.TestPattern is { } p ? $"Test pattern: {p}" : "Connect a capture card");
        page.SetIconName("camera-video-symbolic");

        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(page);
        _window.SetContent(toolbar);
    }

    public void Present() => _window.Present();
}
