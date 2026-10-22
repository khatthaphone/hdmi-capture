using HDMICaptain.Core;
using Microsoft.UI.Xaml;

namespace HDMICaptain.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(LaunchOptions options)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 760));
        StatusText.Text = options.TestPattern is { } p ? $"Test pattern: {p}" : "Waiting for a capture device";
    }
}
