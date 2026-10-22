using System.ComponentModel;
using System.Diagnostics;
using HDMICaptain.App.Video;
using HDMICaptain.App.ViewModels;
using HDMICaptain.App.Views;
using HDMICaptain.App.Windowing;
using HDMICaptain.Core;
using HDMICaptain.Media.Audio;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace HDMICaptain.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private const double BarZone = 140; // DIPs above the bottom edge that wake the control bar

    private readonly nint _hwnd;
    private readonly LaunchOptions _options;
    private readonly D3DRenderer _renderer;
    private readonly SwapChainTarget _target;
    private readonly SwapChainPanelHost? _panelHost;
    private readonly ChildVideoWindow? _childWindow;
    private readonly AspectLock _aspectLock;
    private readonly PrefsStore _prefs = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = IdleDelay };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DeviceWatcher? _watcher;
    private ICaptureSource? _source;
    private WasapiAudioPipeline? _audio;
    private int _opening;
    private int _frames;
    private int _firstFrame;
    private long _lastStatsTicks = Stopwatch.GetTimestamp();
    private bool _barHovered;
    private int _openFlyouts;
    private bool _cursorHidden;

    public ViewerModel Model { get; } = new();

    public MainWindow(LaunchOptions options)
    {
        _options = options;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        RestoreFrame();
        SystemBackdrop = new MicaBackdrop();

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
        _aspectLock = new AspectLock(_hwnd);

        AppWindow.Changed += OnAppWindowChanged;
        Closed += (_, _) => Shutdown();

        var prefs = _prefs.Prefs;
        Model.ShowDiagnostics = prefs.ShowStats || options.QaShowStats;
        Model.AlwaysOnTop = prefs.FloatOnTop;
        Model.CleanMode = prefs.CleanMode;
        Model.PropertyChanged += OnModelChanged;
        ApplyAlwaysOnTop();
        ApplyCleanMode();
        ShowStatus();
        SetUpShortcuts();
        Root.ContextFlyout = BuildContextMenu();

        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();
        _idleTimer.Tick += (_, _) => OnIdle();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Opacity = 0; };
        ControlBar.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
        Toast.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
        if (options.QaPinControls) ShowControls();

        if (options.TestPattern is { } pattern)
        {
            Model.InputName = "Test Pattern";
            StartSource(new TestPatternSource(pattern, ParseFormat(options.TestPatternFormat)));
            Model.Phase = new ViewerPhase.Live();
        }
        else
        {
            _watcher = new DeviceWatcher();
            _watcher.Arrived += _ => DispatcherQueue.TryEnqueue(OpenCard);
            _watcher.Removed += OnCameraRemoved;
            // Covers a card that is busy, blocked by privacy settings or not plugged in yet: try again until it opens.
            _retryTimer.Tick += (_, _) => OpenCard();
            _retryTimer.Start();
            OpenCard();
        }
    }

    // MARK: - Capture

    private void StartSource(ICaptureSource source)
    {
        _source = source;
        source.FrameArrived += OnFrame;
        source.Start();
    }

    /// <summary>
    /// Opens the first external card (the built-in webcam only as a last resort) on a pool thread: MF objects
    /// live in the MTA, and opening can take a second. The controller from task 093 replaces this policy.
    /// </summary>
    private void OpenCard()
    {
        if (_source != null || Interlocked.Exchange(ref _opening, 1) == 1) return;
        var previous = Model.Phase;
        Task.Run(() =>
        {
            ViewerPhase phase;
            MfSource? opened = null;
            string? name = null;
            try
            {
                var device = MfDevices.Video().OrderBy(d => d.Usb?.VendorId is null).FirstOrDefault();
                if (device is null)
                {
                    phase = previous is ViewerPhase.Waiting ? previous : new ViewerPhase.Scanning();
                }
                else
                {
                    name = device.Name;
                    if (previous is not ViewerPhase.Paused) DispatcherQueue.TryEnqueue(() => SetConnecting(device.Name));
                    var source = new MfSource(device.SymbolicLink, link: device.Usb?.Link);
                    source.Log += line => AppLog.Write($"[{device.Name}] {line}");
                    source.Failed += error => DispatcherQueue.TryEnqueue(() => OnCaptureFailed(source, error));
                    source.FrameArrived += OnFrame;
                    try
                    {
                        Volatile.Write(ref _firstFrame, 0);
                        source.Start();
                        opened = source;
                        StartAudio(device);
                        phase = new ViewerPhase.Connecting(device.Name); // Live on the first frame
                    }
                    catch (System.Runtime.InteropServices.COMException e)
                    {
                        AppLog.Write($"[{device.Name}] open failed: {MfErrors.Name(e.HResult)}");
                        phase = e.HResult switch
                        {
                            MfErrors.AccessDenied => new ViewerPhase.PermissionDenied(),
                            MfErrors.DevicePreempted or MfErrors.SharingViolation or MfErrors.HwMftFailedStartStreaming
                                => new ViewerPhase.Paused(PauseReason.InUseByAnotherApp),
                            _ => new ViewerPhase.Failed(MfErrors.Name(e.HResult)),
                        };
                    }
                }
            }
            catch (Exception e)
            {
                AppLog.Write($"open failed: {e}");
                phase = new ViewerPhase.Failed(e.Message);
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                Volatile.Write(ref _opening, 0);
                if (opened != null)
                {
                    _source = opened;
                    Model.InputName = name!;
                    if (Volatile.Read(ref _firstFrame) == 0) Model.Phase = phase;
                }
                else
                {
                    Model.Phase = phase;
                }
            });
        });
    }

    /// <summary>The card's own capture endpoint (same ContainerId as its video). Pool thread.</summary>
    private void StartAudio(VideoDevice device)
    {
        StopAudio();
        if (device.Usb?.ContainerId is not { } container || AudioEndpoints.CaptureFor(container) is not { } endpoint)
        {
            AppLog.Write($"[{device.Name}] no audio endpoint in its container");
            return;
        }
        var audio = new WasapiAudioPipeline(endpoint.Id);
        audio.Settings = CurrentAudioSettings();
        audio.Log += line => AppLog.Write($"[audio] {line}");
        audio.AccessDenied += () => DispatcherQueue.TryEnqueue(() =>
            ShowToast("Sound is blocked. Allow desktop apps in Settings › Privacy & security › Microphone."));
        audio.Start();
        _audio = audio;
    }

    private void StopAudio()
    {
        var audio = Interlocked.Exchange(ref _audio, null);
        if (audio != null) Task.Run(audio.Dispose);
    }

    private AudioSettings CurrentAudioSettings() => new(Volume: (float)(Model.Volume / 100), Muted: Model.Muted);

    private void SetConnecting(string name)
    {
        if (_source == null) Model.Phase = new ViewerPhase.Connecting(name);
    }

    private void OnCameraRemoved(string link)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_source is MfSource source && DeviceWatcher.SameLink(source.SymbolicLink, link))
                OnCaptureFailed(source, new CaptureError(MfErrors.DeviceRemoved, "interface removal"));
        });
    }

    // UI thread. The reader already stopped delivering; release it off the UI thread and look again.
    private void OnCaptureFailed(MfSource source, CaptureError error)
    {
        if (!ReferenceEquals(_source, source)) return;
        AppLog.Write($"[capture] {error}");
        _source = null;
        Task.Run(source.Dispose);
        StopAudio();
        Model.Phase = error.HResult == MfErrors.DevicePreempted
            ? new ViewerPhase.Paused(PauseReason.InUseByAnotherApp)
            : new ViewerPhase.Waiting(Model.InputName);
        _renderer.Clear();
    }

    // Source thread: render straight away, no UI-thread hop (CLAUDE.md conventions).
    private void OnFrame(in VideoFrame frame)
    {
        _renderer.Render(in frame);
        Interlocked.Increment(ref _frames);
        if (Interlocked.Exchange(ref _firstFrame, 1) == 0)
        {
            var aspect = (double)frame.Width / frame.Height;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_source != null) Model.Phase = new ViewerPhase.Live();
                _aspectLock.Aspect = _prefs.Prefs.LockAspect ? aspect : null;
            });
        }
    }

    // MARK: - Status card

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewerModel.Phase):
                ShowStatus();
                break;
            case nameof(ViewerModel.Scale) or nameof(ViewerModel.SharpPixels):
                _renderer.Options = _renderer.Options with
                {
                    Scale = Model.Scale,
                    Filter = Model.SharpPixels ? FilterMode.Nearest : FilterMode.Linear,
                };
                _renderer.Redraw();
                break;
            case nameof(ViewerModel.Muted) or nameof(ViewerModel.Volume):
                if (_audio is { } audio) audio.Settings = audio.Settings with { Volume = (float)(Model.Volume / 100), Muted = Model.Muted };
                break;
            case nameof(ViewerModel.ShowDiagnostics):
                _prefs.Update(p => p.ShowStats = Model.ShowDiagnostics);
                UpdateStats();
                break;
            case nameof(ViewerModel.AlwaysOnTop):
                _prefs.Update(p => p.FloatOnTop = Model.AlwaysOnTop);
                ApplyAlwaysOnTop();
                break;
            case nameof(ViewerModel.CleanMode):
                _prefs.Update(p => p.CleanMode = Model.CleanMode);
                ApplyCleanMode();
                break;
        }
    }

    private void ShowStatus()
    {
        if (Model.Status is not { } copy) return;
        StatusGlyph.Glyph = copy.Glyph;
        StatusGlyph.Visibility = copy.Spinner ? Visibility.Collapsed : Visibility.Visible;
        StatusSpinner.IsActive = copy.Spinner;
        StatusTitle.Text = copy.Title;
        StatusMessage.Text = copy.Message;
        StatusDetail.Text = copy.Detail ?? "";
        StatusDetail.Visibility = copy.Detail is null ? Visibility.Collapsed : Visibility.Visible;
        SetAction(StatusPrimary, copy.Primary);
        SetAction(StatusSecondary, copy.Secondary);

        static void SetAction(Button button, StatusAction? action)
        {
            button.Content = action?.Title;
            button.Tag = action?.Id;
            button.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void OnStatusAction(object sender, RoutedEventArgs e)
    {
        switch (((Button)sender).Tag as string)
        {
            case "help":
                await ConnectionHelp.ShowAsync(Content.XamlRoot);
                break;
            case "privacy":
                await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-webcam"));
                break;
            case "retry":
                Reconnect();
                break;
        }
    }

    private void Reconnect()
    {
        if (_source is MfSource source)
        {
            _source = null;
            Task.Run(source.Dispose);
            StopAudio();
        }
        if (_source == null) OpenCard();
    }

    // MARK: - Control bar and cursor

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowCursor();
        var y = e.GetCurrentPoint(Root).Position.Y;
        if (_prefs.Prefs.ShowControlsOnHover && Root.ActualHeight - y <= BarZone) ShowControls();
        RestartIdle();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e) => RestartIdle();

    private void OnBarPointerEntered(object sender, PointerRoutedEventArgs e) => _barHovered = true;

    private void OnBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _barHovered = false;
        RestartIdle();
    }

    private void OnFlyoutOpened(object? sender, object e) => _openFlyouts++;

    private void OnFlyoutClosed(object? sender, object e)
    {
        _openFlyouts = Math.Max(0, _openFlyouts - 1);
        RestartIdle();
    }

    private void ShowControls()
    {
        ControlBar.Opacity = 1;
        ControlBar.IsHitTestVisible = true;
    }

    private void RestartIdle()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    // 3 s without movement: fade the bar (unless hovered, a menu is open or QA pins it) and hide the cursor over live video.
    private void OnIdle()
    {
        _idleTimer.Stop();
        if (_barHovered || _openFlyouts > 0) return;
        if (!_options.QaPinControls)
        {
            ControlBar.Opacity = 0;
            ControlBar.IsHitTestVisible = false;
        }
        if (Model.IsLive && !_cursorHidden)
        {
            NativeWindow.ShowCursor(false);
            _cursorHidden = true;
        }
    }

    private void ShowCursor()
    {
        if (!_cursorHidden) return;
        NativeWindow.ShowCursor(true);
        _cursorHidden = false;
    }

    // MARK: - Menus

    private void OnInputMenuOpening(object? sender, object e)
    {
        InputMenu.Items.Clear();
        FillInputItems(InputMenu.Items);
    }

    private void FillInputItems(IList<MenuFlyoutItemBase> items)
    {
        var current = (_source as MfSource)?.SymbolicLink;
        foreach (var device in MfDevices.Video())
        {
            items.Add(new RadioMenuFlyoutItem
            {
                Text = device.Name,
                GroupName = "input",
                IsChecked = current != null && DeviceWatcher.SameLink(current, device.SymbolicLink),
            });
        }
        if (items.Count == 0) items.Add(new MenuFlyoutItem { Text = "No capture card connected", IsEnabled = false });
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item("Reconnect", "", Reconnect));
    }

    private void OnOptionsMenuOpening(object? sender, object e)
    {
        OptionsMenu.Items.Clear();
        FillOptionsItems(OptionsMenu.Items);
    }

    private void FillOptionsItems(IList<MenuFlyoutItemBase> items)
    {
        var scale = new MenuFlyoutSubItem { Text = "Scale", Icon = new FontIcon { Glyph = "" } };
        foreach (var mode in Enum.GetValues<ScaleMode>())
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = ViewerModel.ScaleTitle(mode),
                GroupName = "scale",
                IsChecked = Model.Scale == mode,
                KeyboardAcceleratorTextOverride = $"Ctrl+{(int)mode + 1}",
            };
            item.Click += (_, _) => Model.Scale = mode;
            scale.Items.Add(item);
        }
        items.Add(scale);
        items.Add(Toggle("Sharp Pixels", Model.SharpPixels, v => Model.SharpPixels = v));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Toggle("Show Diagnostics", Model.ShowDiagnostics, v => Model.ShowDiagnostics = v));
        items.Add(Item("Connection Help…", "", async () => await ConnectionHelp.ShowAsync(Content.XamlRoot)));
        items.Add(Toggle("Keep Window on Top", Model.AlwaysOnTop, v => Model.AlwaysOnTop = v, "Ctrl+T"));
        items.Add(Toggle("Clean Mode", Model.CleanMode, v => Model.CleanMode = v));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item("Save Screenshot", "", SaveScreenshot, "Ctrl+Shift+S"));
        items.Add(Item("Copy Frame", "", CopyFrame));
    }

    /// <summary>Right-click menu: the Mac's context menu (input, view options, full screen).</summary>
    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var input = new MenuFlyoutSubItem { Text = "Video Input", Icon = new FontIcon { Glyph = "" } };
            FillInputItems(input.Items);
            menu.Items.Add(input);
            menu.Items.Add(new MenuFlyoutSeparator());
            FillOptionsItems(menu.Items);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(Model.IsFullScreen ? "Exit Full Screen" : "Full Screen", Model.FullScreenGlyph, ToggleFullScreen, "F11"));
        };
        menu.Opened += OnFlyoutOpened;
        menu.Closed += OnFlyoutClosed;
        return menu;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (shortcut != null) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += (_, _) => action();
        return item;
    }

    private static ToggleMenuFlyoutItem Toggle(string text, bool on, Action<bool> set, string? shortcut = null)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = on };
        if (shortcut != null) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += (_, _) => set(item.IsChecked);
        return item;
    }

    // MARK: - Bar buttons and shortcuts

    private void OnMuteClick(object sender, RoutedEventArgs e) => Model.Muted = !Model.Muted;

    private void OnScreenshotClick(object sender, RoutedEventArgs e) => SaveScreenshot();

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnRootDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsInsideBar(e.OriginalSource)) return;
        ToggleFullScreen();
    }

    // Clean mode has no title bar, so the picture itself drags the window (as on the Mac).
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!Model.CleanMode || Model.IsFullScreen || IsInsideBar(e.OriginalSource)) return;
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        NativeWindow.ReleaseCapture();
        NativeWindow.SendMessage(_hwnd, NativeWindow.WM_NCLBUTTONDOWN, NativeWindow.HTCAPTION, 0);
    }

    private bool IsInsideBar(object source)
    {
        for (var element = source as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, ControlBar)) return true;
            if (ReferenceEquals(element, Root)) return false;
        }
        return false;
    }

    private void SetUpShortcuts()
    {
        Add(VirtualKey.F11, VirtualKeyModifiers.None, ToggleFullScreen);
        Add(VirtualKey.Enter, VirtualKeyModifiers.Menu, ToggleFullScreen);
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, () => { if (Model.IsFullScreen) ToggleFullScreen(); });
        Add(VirtualKey.M, VirtualKeyModifiers.Control, () => Model.Muted = !Model.Muted);
        Add(VirtualKey.T, VirtualKeyModifiers.Control, () => Model.AlwaysOnTop = !Model.AlwaysOnTop);
        Add(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, SaveScreenshot);
        Add(VirtualKey.Number1, VirtualKeyModifiers.Control, () => Model.Scale = ScaleMode.Fit);
        Add(VirtualKey.Number2, VirtualKeyModifiers.Control, () => Model.Scale = ScaleMode.Fill);
        Add(VirtualKey.Number3, VirtualKeyModifiers.Control, () => Model.Scale = ScaleMode.Stretch);
        Add(VirtualKey.Number4, VirtualKeyModifiers.Control, () => Model.Scale = ScaleMode.Integer);

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                action();
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    // MARK: - Window behaviour

    private void ToggleFullScreen()
    {
        var full = AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(full ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Model.IsFullScreen = full;
        if (!full)
        {
            ApplyAlwaysOnTop();
            ApplyCleanMode();
        }
    }

    /// <summary>Last size and position, if that spot is still on a connected display; else 1280×760.</summary>
    private void RestoreFrame()
    {
        if (_prefs.LoadFrame() is { Width: > 200, Height: > 150 } frame)
        {
            var rect = new Windows.Graphics.RectInt32(frame.X, frame.Y, frame.Width, frame.Height);
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.None);
            if (area != null)
            {
                AppWindow.MoveAndResize(rect);
                return;
            }
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 760));
    }

    private void SaveFrame()
    {
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        var (position, size) = (AppWindow.Position, AppWindow.Size);
        _prefs.SaveFrame(new WindowFrame(position.X, position.Y, size.Width, size.Height));
    }

    private void ApplyAlwaysOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = Model.AlwaysOnTop;
    }

    private void ApplyCleanMode()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: !Model.CleanMode);
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
            if (args.DidPresenterChange) Model.IsFullScreen = sender.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        }
    }

    // MARK: - Screenshot and copy (clean pass-1 source only: overlays never appear)

    private async void SaveScreenshot()
    {
        if (await EncodeFrameAsync() is not { } png)
        {
            ShowToast("No picture to capture yet");
            return;
        }
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDMI Captain");
            Directory.CreateDirectory(folder);
            var now = DateTime.Now;
            var path = Path.Combine(folder, $"HDMI Captain {now:yyyy-MM-dd} at {now:HH.mm.ss}.png");
            await using (var file = File.Create(path))
                await png.AsStreamForRead().CopyToAsync(file);
            ShowToast("Screenshot saved");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Couldn't save the screenshot: {e.Message}");
        }
    }

    private async void CopyFrame()
    {
        if (await EncodeFrameAsync() is not { } png)
        {
            ShowToast("No picture to copy yet");
            return;
        }
        var package = new DataPackage();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(png));
        Clipboard.SetContent(package);
        ShowToast("Frame copied");
    }

    private async Task<InMemoryRandomAccessStream?> EncodeFrameAsync()
    {
        var (w, h) = _renderer.SourceSize;
        if (_renderer.ReadSourcePixels() is not { } rgba || w == 0) return null;
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, rgba);
        await encoder.FlushAsync();
        stream.Seek(0);
        return stream;
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // MARK: - Diagnostics

    private void UpdateStats()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastStatsTicks) / (double)Stopwatch.Frequency;
        _lastStatsTicks = now;
        var fps = Interlocked.Exchange(ref _frames, 0) / seconds;
        if (!Model.ShowDiagnostics) return;
        var (w, h) = _renderer.SourceSize;
        var surface = _childWindow != null ? "HWND" : "SwapChainPanel";
        var sound = _audio is { } a ? $"\naudio buffered {a.BufferedMs:F0} ms" : "";
        var capture = _source is MfSource { CurrentType: { } type } mf
            ? $"\n{type.FourCC} {type.Width}×{type.Height} @ {type.FrameRate:F2}{(type.DecodedByWindows ? " (Windows MJPEG decode)" : "")}, {mf.Stats.Dropped} dropped"
            : "";
        Model.StatsText = $"{Model.InputName}\n{w}×{h}  {fps:F1} fps{capture}\n{_renderer.AdapterName}\n{surface}, tearing {(_target.Tearing ? "on" : "off")}{sound}";
    }

    private void Shutdown()
    {
        SaveFrame();
        _statsTimer.Stop();
        _retryTimer.Stop();
        _idleTimer.Stop();
        ShowCursor();
        _watcher?.Dispose();
        _audio?.Dispose();
        _source?.Dispose();
        _aspectLock.Dispose();
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
