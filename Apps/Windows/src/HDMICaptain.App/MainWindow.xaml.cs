using System.Diagnostics;
using HDMICaptain.App.Video;
using HDMICaptain.Core;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace HDMICaptain.App;

public sealed partial class MainWindow : Window
{
    private readonly nint _hwnd;
    private readonly D3DRenderer _renderer;
    private readonly SwapChainTarget _target;
    private readonly SwapChainPanelHost? _panelHost;
    private readonly ChildVideoWindow? _childWindow;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ICaptureSource? _source;
    private int _frames;
    private long _lastStatsTicks = Stopwatch.GetTimestamp();

    public MainWindow(LaunchOptions options)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 760));

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _renderer = D3DRenderer.Create(_hwnd);
        var client = AppWindow.ClientSize;
        if (string.Equals(options.VideoSurface, "hwnd", StringComparison.OrdinalIgnoreCase))
        {
            _childWindow = new ChildVideoWindow(_hwnd, client.Width, client.Height);
            _target = _renderer.CreateHwndTarget(_childWindow.Handle, client.Width, client.Height);
        }
        else
        {
            _panelHost = new SwapChainPanelHost(VideoPanel, _renderer);
            _target = _panelHost.Target;
        }

        AppWindow.Changed += OnAppWindowChanged;
        Closed += (_, _) => Shutdown();

        StatsText.Visibility = options.QaShowStats ? Visibility.Visible : Visibility.Collapsed;
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        if (options.TestPattern is { } pattern)
            StartSource(new TestPatternSource(pattern, ParseFormat(options.TestPatternFormat)));
        else
            StatusText.Text = "Waiting for a capture device";
    }

    private void StartSource(ICaptureSource source)
    {
        _source = source;
        source.FrameArrived += OnFrame;
        source.Start();
        Placeholder.Visibility = Visibility.Collapsed;
    }

    // Source thread: render straight away, no UI-thread hop (CLAUDE.md conventions).
    private void OnFrame(in VideoFrame frame)
    {
        _renderer.Render(in frame);
        Interlocked.Increment(ref _frames);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange && _childWindow != null)
        {
            var client = sender.ClientSize;
            _childWindow.SetBounds(0, 0, client.Width, client.Height);
            _target.Resize(client.Width, client.Height);
            _renderer.Redraw();
        }
        if (args.DidPositionChange) _renderer.CheckAdapter(_hwnd);
        if (args.DidPresenterChange || args.DidSizeChange || args.DidVisibilityChange)
        {
            // Minimized or hidden: stop presenting only; the source keeps running (spec §13.5).
            var minimized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
            _target.IsVisible = sender.IsVisible && !minimized;
        }
    }

    private void UpdateStats()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastStatsTicks) / (double)Stopwatch.Frequency;
        _lastStatsTicks = now;
        var fps = Interlocked.Exchange(ref _frames, 0) / seconds;
        var (w, h) = _renderer.SourceSize;
        var surface = _childWindow != null ? "HWND" : "SwapChainPanel";
        StatsText.Text = $"{w}×{h}  {fps:F1} fps\n{_renderer.AdapterName}\n{surface}, tearing {(_target.Tearing ? "on" : "off")}";
    }

    private void Shutdown()
    {
        _statsTimer.Stop();
        _source?.Dispose();
        _panelHost?.Dispose();
        _renderer.Dispose();
        _childWindow?.Dispose();
    }

    private static PixelFormat ParseFormat(string? name) => name?.ToLowerInvariant() switch
    {
        "yuy2" => PixelFormat.Yuy2,
        "i420" => PixelFormat.I420,
        "i422" => PixelFormat.I422,
        "i444" => PixelFormat.I444,
        "bgra" => PixelFormat.Bgra,
        _ => PixelFormat.Nv12,
    };
}
