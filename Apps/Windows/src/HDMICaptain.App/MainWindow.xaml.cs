using System.ComponentModel;
using System.Diagnostics;
using HDMICaptain.App.Video;
using HDMICaptain.App.ViewModels;
using HDMICaptain.App.Views;
using HDMICaptain.App.Recording;
using HDMICaptain.App.Store;
using HDMICaptain.App.Windowing;
using HDMICaptain.Core;
using HDMICaptain.Media.Audio;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private readonly TrayIcon? _tray;
    private readonly PrefsStore _prefs = App.Prefs;
    private readonly SettingsModel _settings;
    private readonly ProGate _pro;
    private readonly RecordingController _recording;
    private readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = IdleDelay };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CaptureController? _capture;
    private TestPatternSource? _testPattern;
    private readonly SignalMonitor _signal = new();
    private readonly ScopeSampler _scopes = new();
    private WasapiAudioPipeline? _audio;
    private string? _audioDeviceKey;
    private AudioSettings _audioSettings = new();
    private bool _hadCard;
    private (int Width, int Height) _guideSource = (1920, 1080);
    private int _frames;
    private long _lastStatsTicks = Stopwatch.GetTimestamp();
    private long _lastAllocated = GC.GetTotalAllocatedBytes();
    private int _lastGen0 = GC.CollectionCount(0);
    private bool _firstLiveLogged;
    private int _perfTicks;
    private long _frameThreadAllocated;
    private DateTime _soakNext = DateTime.MaxValue;
    private string _allocText = "";
    private bool _barHovered;
    private int _openFlyouts;
    private bool _cursorHidden;

    public ViewerModel Model { get; }

    /// <summary>The card this window is pinned to (Multi-View), or null for the main viewer.</summary>
    public string? CardKey { get; }

    public bool IsPrimary => CardKey is null;

    /// <summary>The card this viewer is capturing right now (any thread).</summary>
    public string? ShowingCard => _testPattern != null ? null : _capture?.CurrentDevice?.Key;

    /// <summary>The cards this window's catalog sees (the grid lists the main window's).</summary>
    public IReadOnlyList<CaptureDevice> Devices => _capture?.Catalog.Devices ?? [];

    /// <summary>Every frame this window renders, on the source thread (the Multi-View grid taps it instead of opening the card twice).</summary>
    public event FrameHandler? FrameTap;

    /// <summary>A card window opened or closed: let the card policy avoid cards other viewers hold.</summary>
    public void OnClaimsChanged() => _capture?.ClaimsChanged();

    private bool _silenced;

    /// <summary>Another viewer has focus and "Play sound from the frontmost window" is on (App.FocusAudio).</summary>
    public void SetSilenced(bool silenced)
    {
        if (_silenced == silenced) return;
        _silenced = silenced;
        if (_audio is { } audio) audio.Settings = EffectiveAudio;
    }

    private AudioSettings EffectiveAudio => _silenced ? _audioSettings with { Muted = true } : _audioSettings;

    /// <param name="cardKey">Null for the main viewer; a card's DeviceKey for a Multi-View card window.</param>
    public MainWindow(LaunchOptions options, string? cardKey = null)
    {
        _options = options;
        CardKey = cardKey;
        Model = new ViewerModel { QaDiagnostics = options.QaShowStats };
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        if (IsPrimary) RestoreFrame();
        else AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 580));
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
        _recording = new RecordingController(_renderer);
        _recording.Failed += message => DispatcherQueue.TryEnqueue(() => OnRecordingFailed(message));
        _recordTimer.Tick += (_, _) => UpdateRecordButton();
        if (IsPrimary) _tray = new TrayIcon(_hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), "HDMI Captain", TrayMenu, ShowFromTray);

        AppWindow.Changed += OnAppWindowChanged;
        Closed += (_, _) => Shutdown();

        // Settings (task 095) own prefs and the current card's preset; ViewerModel mirrors the toggles both ways.
        _settings = new SettingsModel(_prefs);
        _settings.Attach(Model);
        _settings.Changed += OnSettingsChanged;
        _settings.LutFailed += ShowToast;
        _pro = new ProGate(CreateProStore(options));
        _ = _pro.RefreshAsync();
        _settings.ApplyTo(_renderer);
        _audioSettings = _settings.AudioSettings;
        Model.PropertyChanged += OnModelChanged;
        _signal.Changed += content =>
        {
            AppLog.Write($"[signal] {content}");
            DispatcherQueue.TryEnqueue(() => Model.Signal = content);
        };
        ApplyAlwaysOnTop();
        ApplyCleanMode();
        ShowStatus();
        SetUpShortcuts();
        Root.ContextFlyout = BuildContextMenu();
        Root.SizeChanged += (_, _) => DrawGuides();

        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();
        _idleTimer.Tick += (_, _) => OnIdle();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Opacity = 0; };
        ControlBar.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
        Toast.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) };
        if (options.QaPinControls) ShowControls();
        if (IsPrimary && options.QaAction is { } qaAction) RunQaActionSoon(qaAction);
        if (options.QaSoakMinutes is > 0) _soakNext = DateTime.Now.AddSeconds(5);

        if (options.TestPattern is { } pattern)
        {
            Model.InputName = "Test Pattern";
            _testPattern = new TestPatternSource(pattern, ParseFormat(options.TestPatternFormat));
            _testPattern.FrameArrived += OnFrame;
            _testPattern.Start();
            Model.Phase = new ViewerPhase.Live();
        }
        else
        {
            // The controller owns discovery, open, recovery, privacy/in-use retries, sleep and power (task 093).
            _capture = CaptureController.CreateDefault(cardKey ?? _prefs.State.PreferredDeviceKey, new CaptureControllerOptions
            {
                IsClaimedElsewhere = key => App.IsClaimedByOther(this, key),
                PinnedToPreferred = cardKey != null,
            });
            _settings.Attach(_capture);
            _capture.Log += line => AppLog.Write($"[capture] {line}");
            _capture.FrameArrived += OnCaptureFrame;
            _capture.StateChanged += state => DispatcherQueue.TryEnqueue(() => OnCaptureState(state));
            if (IsPrimary)
                _capture.Catalog.PreferredChanged += key => DispatcherQueue.TryEnqueue(() => _prefs.UpdateState(s => s with { PreferredDeviceKey = key }));
            if (_prefs.State.WelcomeShown || !IsPrimary) _capture.Start();
            else Model.Phase = new ViewerPhase.Welcome();
        }
    }

    // MARK: - Capture

    // UI thread: CaptureState → what the window shows, and audio that follows the live card.
    private void OnCaptureState(CaptureState state)
    {
        if (_testPattern != null) return; // the card is stopped while a test pattern shows
        var device = _capture?.CurrentDevice;
        if (device != null) Model.InputName = device.Name;
        if (state is CaptureState.Live or CaptureState.Connecting && device != null && device.Key != _settings.Device?.Key)
            _settings.OnDeviceChanged(device);
        Model.Phase = state switch
        {
            CaptureState.Live => new ViewerPhase.Live(),
            CaptureState.Connecting c => new ViewerPhase.Connecting(c.DeviceName),
            CaptureState.Recovering { Reason: RecoveryReason.Waiting or RecoveryReason.Disconnected } when _hadCard
                => new ViewerPhase.Waiting(Model.InputName),
            CaptureState.Recovering { Reason: RecoveryReason.Waiting } => new ViewerPhase.Scanning(),
            CaptureState.Recovering r => new ViewerPhase.Recovering(r.Reason),
            CaptureState.Paused { Reason: PauseReason.PrivacyToggle } => new ViewerPhase.PermissionDenied(),
            CaptureState.Paused p => new ViewerPhase.Paused(p.Reason),
            CaptureState.Failed f => new ViewerPhase.Failed(f.Message),
            _ => new ViewerPhase.Scanning(),
        };
        if (state is not CaptureState.Live)
        {
            _signal.Reset();
            Model.Signal = new SignalContent.Picture();
        }
        if (state is CaptureState.Live live)
        {
            _hadCard = true;
            // Steady capture allocates nothing per frame; keep blocking gen2 GCs away while live (task 101).
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            if (!_firstLiveLogged)
            {
                _firstLiveLogged = true;
                var sinceStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
                AppLog.Write($"[perf] live picture {sinceStart.TotalMilliseconds:F0} ms after process start");
            }
            _aspectLock.Aspect = _settings.LockAspect ? (double)live.Info.Width / live.Info.Height : null;
            _guideSource = (live.Info.Width, live.Info.Height);
            DrawGuides();
            if (device != null && _audioDeviceKey != device.Key) StartAudio(device);
            // Keep Recent Footage stays on across launches and card changes, as on the Mac (Pro only).
            if (_prefs.Prefs.RecentEnabled && !_recording.IsKeepingRecent && _pro.IsUnlocked(ProFeature.ReplayBuffer))
                _recording.StartKeepingRecent(live.Info.FrameRate);
        }
        else if (state is CaptureState.Recovering { Reason: RecoveryReason.Waiting or RecoveryReason.Disconnected } or CaptureState.Failed)
        {
            if (_recording.IsRecording) _ = StopRecordingAsync("Recording saved: the capture card was disconnected");
            StopAudio();
            _renderer.Clear();
        }
    }

    /// <summary>The card's own capture endpoint (same ContainerId as its video).</summary>
    private void StartAudio(CaptureDevice device)
    {
        StopAudio();
        _audioDeviceKey = device.Key;
        var settings = _audioSettings;
        Task.Run(() =>
        {
            if (device.Usb?.ContainerId is not { } container || AudioEndpoints.CaptureFor(container) is not { } endpoint)
            {
                AppLog.Write($"[{device.Name}] no audio endpoint in its container");
                return;
            }
            var audio = new WasapiAudioPipeline(endpoint.Id) { Settings = _silenced ? settings with { Muted = true } : settings };
            audio.Log += line => AppLog.Write($"[audio] {line}");
            audio.AccessDenied += () => DispatcherQueue.TryEnqueue(() =>
                ShowToast("Sound is blocked. Allow desktop apps in Settings › Privacy & security › Microphone."));
            audio.Start();
            Interlocked.Exchange(ref _audio, audio)?.Dispose();
            DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_audio, audio)) _recording.AttachAudio(audio); });
        });
    }

    private void StopAudio()
    {
        _audioDeviceKey = null;
        _recording.AttachAudio(null);
        var audio = Interlocked.Exchange(ref _audio, null);
        if (audio != null) Task.Run(audio.Dispose);
    }

    private void Reconnect() => _capture?.Retry();

    // MF thread: the last frames of a card being stopped for a test pattern are dropped.
    private void OnCaptureFrame(in VideoFrame frame)
    {
        if (_testPattern == null) OnFrame(in frame);
    }

    /// <summary>
    /// Input ▸ Test Pattern: shows a generated pattern instead of the card (which is closed, so other apps can use it),
    /// or goes back to the live input (null).
    /// </summary>
    private void ShowTestPattern(TestPatternKind? kind)
    {
        if (_testPattern is { } old)
        {
            old.FrameArrived -= OnFrame;
            old.Dispose();
            _testPattern = null;
        }
        if (kind is { } pattern)
        {
            if (_recording.IsRecording) _ = StopRecordingAsync("Recording saved to Videos › HDMI Captain");
            _capture?.Stop();
            StopAudio();
            _signal.Reset();
            Model.Signal = new SignalContent.Picture();
            Model.InputName = "Test Pattern";
            _testPattern = new TestPatternSource(pattern, ParseFormat(_options.TestPatternFormat));
            _testPattern.FrameArrived += OnFrame;
            _testPattern.Start();
            Model.Phase = new ViewerPhase.Live();
            _guideSource = (_testPattern.Width, _testPattern.Height);
            DrawGuides();
        }
        else if (_capture != null)
        {
            _renderer.Clear();
            Model.Phase = new ViewerPhase.Scanning();
            _capture.Start();
        }
    }

    // Source thread: render straight away, no UI-thread hop (CLAUDE.md conventions).
    private void OnFrame(in VideoFrame frame)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        _renderer.Render(in frame);
        FrameTap?.Invoke(in frame);
        _signal.Observe(in frame);
        _scopes.Observe(in frame);
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _frameThreadAllocated, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // MARK: - Status card

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewerModel.Phase):
                ShowStatus();
                break;
            case nameof(ViewerModel.Scale):
                DrawGuides();
                break;
            case nameof(ViewerModel.InputName):
                _tray?.SetTip($"HDMI Captain — {Model.InputName}");
                if (!IsPrimary) Title = $"HDMI Captain — {Model.InputName}";
                break;
            case nameof(ViewerModel.ShowDiagnostics):
                UpdateStats();
                break;
            case nameof(ViewerModel.AlwaysOnTop):
                ApplyAlwaysOnTop();
                break;
            case nameof(ViewerModel.CleanMode):
                ApplyCleanMode();
                break;
        }
    }

    // UI thread. Settings changed from the Settings window, a menu, a shortcut or a card's preset.
    private void OnSettingsChanged(SettingsChange change)
    {
        if ((change & (SettingsChange.Render | SettingsChange.Adjustments)) != 0)
        {
            _settings.ApplyTo(_renderer);
            DrawGuides();
        }
        if ((change & SettingsChange.Audio) != 0)
        {
            _audioSettings = _settings.AudioSettings;
            if (_audio is { } audio) audio.Settings = EffectiveAudio;
        }
        if ((change & SettingsChange.Prefs) != 0)
        {
            ApplyAlwaysOnTop();
            ApplyCleanMode();
            UpdateStats();
            if (!_settings.LockAspect) _aspectLock.Aspect = null;
            else if (_guideSource.Width > 0) _aspectLock.Aspect = (double)_guideSource.Width / _guideSource.Height;
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
            case "welcome":
                _prefs.UpdateState(state => state with { WelcomeShown = true });
                Model.Phase = new ViewerPhase.Scanning();
                _capture?.Start();
                break;
        }
    }

    // MARK: - Control bar and cursor

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowCursor();
        var y = e.GetCurrentPoint(Root).Position.Y;
        if (_settings.ShowControlsOnHover && Root.ActualHeight - y <= BarZone) ShowControls();
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
        var current = _capture?.CurrentDevice?.Key;
        foreach (var device in _capture?.Catalog.Devices ?? [])
        {
            var item = new RadioMenuFlyoutItem { Text = device.Name, GroupName = "input", IsChecked = device.Key == current };
            item.Click += (_, _) =>
            {
                if (App.CardWindowFor(device.Key) is { } window && !ReferenceEquals(window, this)) window.Activate();
                else
                {
                    App.ReleaseFromGrid(device.Key);
                    _capture?.Select(device.Key);
                }
            };
            items.Add(item);
        }
        if (items.Count == 0) items.Add(new MenuFlyoutItem { Text = "No capture card connected", IsEnabled = false });
        if (_capture is { Catalog.Devices.Count: > 0 } capture)
        {
            var windows = new MenuFlyoutSubItem { Text = "New Window for", Icon = new FontIcon { Glyph = "\uE78B" } };
            foreach (var device in capture.Catalog.Devices)
            {
                var item = new MenuFlyoutItem { Text = device.Name };
                item.Click += (_, _) => App.OpenCardWindow(device.Key);
                windows.Items.Add(item);
            }
            items.Add(new MenuFlyoutSeparator());
            items.Add(windows);
            items.Add(Item("All Cards in a Grid", "\uF0E2", () => GridWindow.Show(), "Ctrl+G"));
        }
        items.Add(new MenuFlyoutSeparator());
        items.Add(TestPatternMenu());
        items.Add(Item("Reconnect", "\uE72C", Reconnect));
    }

    private MenuFlyoutSubItem TestPatternMenu()
    {
        var menu = new MenuFlyoutSubItem { Text = "Test Pattern", Icon = new FontIcon { Glyph = "\uE91B" } };
        var off = new RadioMenuFlyoutItem { Text = "Off (Live Input)", GroupName = "pattern", IsChecked = _testPattern == null, IsEnabled = _capture != null };
        off.Click += (_, _) => ShowTestPattern(null);
        menu.Items.Add(off);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var kind in Enum.GetValues<TestPatternKind>())
        {
            var item = new RadioMenuFlyoutItem { Text = TestPatternSource.Title(kind), GroupName = "pattern", IsChecked = _testPattern?.Kind == kind };
            item.Click += (_, _) => ShowTestPattern(kind);
            menu.Items.Add(item);
        }
        return menu;
    }

    private void OnOptionsMenuOpening(object? sender, object e)
    {
        OptionsMenu.Items.Clear();
        FillOptionsItems(OptionsMenu.Items);
    }

    private void FillOptionsItems(IList<MenuFlyoutItemBase> items)
    {
        var scale = new MenuFlyoutSubItem { Text = "Scale", Icon = new FontIcon { Glyph = "\uE740" } };
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
        items.Add(PictureLookMenu());
        items.Add(GuidesMenu());
        items.Add(LutMenu());
        var size = new MenuFlyoutSubItem { Text = "Window Size", Icon = new FontIcon { Glyph = "\uE9A6" } };
        size.Items.Add(Item("Half Size (50 %)", "\uE73F", () => SizeToVideo(0.5)));
        size.Items.Add(Item("Actual Pixels (100 %)", "\uE9A6", () => SizeToVideo(1), "Ctrl+0"));
        size.Items.Add(Item("Double Size (200 %)", "\uE740", () => SizeToVideo(2)));
        items.Add(size);
        items.Add(new MenuFlyoutSeparator());
        items.Add(Toggle("Show Diagnostics", Model.ShowDiagnostics, v => Model.ShowDiagnostics = v));
        items.Add(Item("Settings…", "\uE713", OpenSettings, "Ctrl+,"));
        items.Add(Item(_pro.IsPro ? "HDMI Captain Pro" : "Upgrade to Pro…", "\uE734", () => _ = ShowUpgradeAsync(null)));
        items.Add(Item("Connection Help…", "\uE897", async () => await ConnectionHelp.ShowAsync(Content.XamlRoot)));
        items.Add(Toggle("Keep Window on Top", Model.AlwaysOnTop, v => Model.AlwaysOnTop = v, "Ctrl+T"));
        items.Add(Toggle("Clean Mode", Model.CleanMode, v => Model.CleanMode = v));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item("Show Scopes", "\uE9D2", () => ScopesWindow.Show(_scopes)));
        items.Add(Item("Save Screenshot", "\uE722", SaveScreenshot, "Ctrl+Shift+S"));
        items.Add(Item("Copy Frame", "\uE8C8", CopyFrame));
    }

    private MenuFlyoutSubItem PictureLookMenu()
    {
        var menu = new MenuFlyoutSubItem { Text = "Picture Look", Icon = new FontIcon { Glyph = "\uE790" } };
        var current = LookPreset.Of(_settings.Preset.Adjustments);
        foreach (var look in LookPreset.All)
        {
            var item = new RadioMenuFlyoutItem { Text = look.Title, GroupName = "look", IsChecked = current == look };
            item.Click += (_, _) => _settings.ApplyLook(look);
            menu.Items.Add(item);
        }
        return menu;
    }

    private MenuFlyoutSubItem LutMenu()
    {
        var menu = new MenuFlyoutSubItem { Text = "LUT", Icon = new FontIcon { Glyph = "\uE2B1" } };
        if (_settings.LutName is { } name)
        {
            menu.Items.Add(new ToggleMenuFlyoutItem { Text = name, IsChecked = true, IsEnabled = false });
            foreach (var strength in new[] { 1.0, 0.75, 0.5, 0.25 })
            {
                var item = new RadioMenuFlyoutItem
                {
                    Text = $"Strength {strength * 100:0} %",
                    GroupName = "lutStrength",
                    IsChecked = Math.Abs(_settings.LutIntensity - strength) < 0.01,
                };
                item.Click += (_, _) => _settings.LutIntensity = strength;
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(Item("Choose .cube File…", "\uE8E5", ChooseLut));
        if (_settings.LutName != null) menu.Items.Add(Item("Remove LUT", "\uE711", () => _settings.SetLut(null)));
        return menu;
    }

    private async void ChooseLut()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".cube");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd); // unpackaged/desktop: pickers need an owner HWND
        if (await picker.PickSingleFileAsync() is { } file) _settings.SetLut(file.Path);
    }

    private MenuFlyoutSubItem GuidesMenu()
    {
        var guides = _prefs.Prefs.Guides;
        var menu = new MenuFlyoutSubItem { Text = "Guides", Icon = new FontIcon { Glyph = "\uE80A" } };
        menu.Items.Add(Toggle("Rule of Thirds", guides.Thirds, v => UpdateGuides(g => g with { Thirds = v })));
        menu.Items.Add(Toggle("Center Cross", guides.Center, v => UpdateGuides(g => g with { Center = v })));
        menu.Items.Add(Toggle("Safe Areas (90 % / 80 %)", guides.SafeAreas, v => UpdateGuides(g => g with { SafeAreas = v })));
        menu.Items.Add(new MenuFlyoutSeparator());
        var none = new RadioMenuFlyoutItem { Text = "No Aspect Mask", GroupName = "mask", IsChecked = guides.Mask is null };
        none.Click += (_, _) => UpdateGuides(g => g with { Mask = null });
        menu.Items.Add(none);
        foreach (var mask in Enum.GetValues<GuideMask>())
        {
            var item = new RadioMenuFlyoutItem { Text = $"Mask {GuideSettings.Title(mask)}", GroupName = "mask", IsChecked = guides.Mask == mask };
            item.Click += (_, _) => UpdateGuides(g => g with { Mask = mask });
            menu.Items.Add(item);
        }
        return menu;
    }

    private void UpdateGuides(Func<GuideSettings, GuideSettings> change)
    {
        _prefs.Update(p => p.Guides = change(p.Guides));
        DrawGuides();
    }

    private void DrawGuides()
    {
        var (w, h) = _guideSource;
        var geometry = RenderGeometry.Compute(w, h, Root.ActualWidth, Root.ActualHeight, Model.Scale);
        GuideOverlay.Draw(GuideCanvas, _prefs.Prefs.Guides, geometry.Viewport);
    }

    /// <summary>Right-click menu: the Mac's context menu (input, view options, full screen).</summary>
    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var input = new MenuFlyoutSubItem { Text = "Video Input", Icon = new FontIcon { Glyph = "\uE714" } };
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
        Add(VirtualKey.G, VirtualKeyModifiers.Control, () => GridWindow.Show());
        Add(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, SaveScreenshot);
        Add(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SizeToVideo(1));
        Add(VirtualKey.R, VirtualKeyModifiers.Control, () => _ = ToggleRecordingAsync());
        Add(VirtualKey.R, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = SaveRecentAsync(TimeSpan.FromSeconds(30)));
        Add((VirtualKey)188, VirtualKeyModifiers.Control, OpenSettings); // VK_OEM_COMMA
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

    /// <summary>
    /// Sizes the client area to the video at <paramref name="factor"/> (1 = one source pixel per screen pixel),
    /// limited to the work area of the window's display.
    /// </summary>
    private void SizeToVideo(double factor)
    {
        var (w, h) = _renderer.SourceSize;
        if (w == 0)
        {
            ShowToast("No picture to size to yet");
            return;
        }
        if (Model.IsFullScreen) ToggleFullScreen();
        // The video fills the whole Win32 client area (content extends into the title bar), which AppWindow's
        // ClientSize leaves out, so measure the frame from GetClientRect.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        NativeWindow.GetClientRect(_hwnd, out var client);
        var frameW = AppWindow.Size.Width - client.Width;
        var frameH = AppWindow.Size.Height - client.Height;
        var width = (int)Math.Round(w * factor);
        var height = (int)Math.Round(h * factor);
        var fit = Math.Min(1.0, Math.Min((double)(work.Width - frameW) / width, (double)(work.Height - frameH) / height));
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * fit) + frameW, (int)(height * fit) + frameH));
        if (fit < 1) ShowToast("Limited to the screen size");
    }

    // MARK: - Tray

    private IReadOnlyList<TrayIcon.MenuEntry> TrayMenu()
    {
        var entries = new List<TrayIcon.MenuEntry>
        {
            AppWindow.IsVisible ? new("Hide Window", () => AppWindow.Hide()) : new("Show Window", ShowFromTray),
            TrayIcon.MenuEntry.Separator,
        };
        var current = _capture?.CurrentDevice?.Key;
        foreach (var device in _capture?.Catalog.Devices ?? [])
            entries.Add(new(device.Name, () => _capture?.Select(device.Key), Checked: device.Key == current));
        if (_capture is { Catalog.Devices.Count: > 0 }) entries.Add(TrayIcon.MenuEntry.Separator);
        entries.Add(new("Mute", () => Model.Muted = !Model.Muted, Checked: Model.Muted));
        entries.Add(new("Save Screenshot", SaveScreenshot, Enabled: Model.IsLive));
        entries.Add(new("Settings…", OpenSettings));
        entries.Add(TrayIcon.MenuEntry.Separator);
        entries.Add(new("Quit HDMI Captain", Close));
        return entries;
    }

    private void OpenSettings() => SettingsWindow.Show(_settings, _capture);

    // MARK: - Record and Save Recent (Pro)

    private async void OnRecordClick(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    private async Task ToggleRecordingAsync()
    {
        if (_recording.IsRecording)
        {
            await StopRecordingAsync("Recording saved to Videos › HDMI Captain");
            return;
        }
        if (_testPattern != null)
        {
            ShowToast("Switch to the live input to record");
            return;
        }
        if (!Model.IsLive)
        {
            ShowToast("Connect a capture card to record");
            return;
        }
        if (!await RequireProAsync(ProFeature.Recording)) return;
        try
        {
            _recording.StartRecording(_capture?.CurrentType?.FrameRate ?? 60);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException)
        {
            ShowToast($"Recording failed: {e.Message}");
            return;
        }
        _recordTimer.Start();
        UpdateRecordButton();
    }

    private async Task StopRecordingAsync(string savedMessage)
    {
        _recordTimer.Stop();
        var path = await _recording.StopRecordingAsync();
        UpdateRecordButton();
        if (path != null)
        {
            AppLog.Write($"[record] saved {path}");
            ShowToast(savedMessage);
        }
    }

    private void OnRecordingFailed(string message)
    {
        AppLog.Write($"[record] failed: {message}");
        _recordTimer.Stop();
        _ = _recording.StopRecordingAsync();
        UpdateRecordButton();
        ShowToast($"Recording failed: {message}");
    }

    private void UpdateRecordButton()
    {
        var recording = _recording.IsRecording;
        RecordDot.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        StopSquare.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        var s = (int)_recording.RecordingDuration.TotalSeconds;
        RecordText.Text = recording ? (s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}") : "Record";
        RecordButton.Background = recording ? new SolidColorBrush(Microsoft.UI.Colors.Red) : null;
        ToolTipService.SetToolTip(RecordButton, recording ? "Stop Recording (Ctrl+R)" : "Record (Ctrl+R)");
        AutomationProperties.SetName(RecordButton, recording ? "Stop recording" : "Record");
    }

    private void OnRecentMenuOpening(object? sender, object e)
    {
        RecentMenu.Items.Clear();
        var unlocked = _pro.IsUnlocked(ProFeature.ReplayBuffer);
        var keep = new ToggleMenuFlyoutItem
        {
            Text = $"Keep Recent Footage ({RecordingController.KeepRecent.TotalSeconds:0} s)",
            IsChecked = _recording.IsKeepingRecent,
            Icon = new FontIcon { Glyph = unlocked ? "\uE81C" : "\uE72E" },
        };
        keep.Click += async (_, _) => await SetKeepRecentAsync(keep.IsChecked);
        RecentMenu.Items.Add(keep);
        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var buffered = _recording.RecentBuffered;
        foreach (var (title, span) in RecordingController.RecentSpans)
        {
            // Allow slack: the buffer starts at a keyframe every 2 s.
            var item = new MenuFlyoutItem { Text = title, IsEnabled = _recording.IsKeepingRecent && buffered + TimeSpan.FromSeconds(2) >= span };
            if (span == TimeSpan.FromSeconds(30)) item.KeyboardAcceleratorTextOverride = "Ctrl+Shift+R";
            item.Click += async (_, _) => await SaveRecentAsync(span);
            RecentMenu.Items.Add(item);
        }
    }

    private async Task SetKeepRecentAsync(bool on)
    {
        if (on)
        {
            if (!await RequireProAsync(ProFeature.ReplayBuffer)) return;
            if (!Model.IsLive || _testPattern != null)
            {
                ShowToast("Connect a capture card to keep recent footage");
                return;
            }
            _recording.StartKeepingRecent(_capture?.CurrentType?.FrameRate ?? 60);
            _prefs.Update(p => p.RecentEnabled = true);
        }
        else
        {
            await _recording.StopKeepingRecentAsync();
            _prefs.Update(p => p.RecentEnabled = false);
        }
    }

    private async Task SaveRecentAsync(TimeSpan span)
    {
        if (!_recording.IsKeepingRecent)
        {
            ShowToast("Turn on Keep Recent Footage in the Save Recent menu first");
            return;
        }
        ShowToast("Saving…");
        try
        {
            var result = await _recording.SaveRecentAsync(span);
            AppLog.Write($"[recent] {result.Path}: {result.Duration.TotalSeconds:F1} s in {result.Elapsed.TotalMilliseconds:F0} ms");
            ShowToast($"Saved the last {Describe(span)} to Videos › HDMI Captain");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            ShowToast($"Couldn't save: {e.Message}");
        }

        static string Describe(TimeSpan span) => span.TotalSeconds < 60 ? $"{span.TotalSeconds:0} seconds" : "minute";
    }

    /// <summary>The Store add-on; DEBUG builds without package identity (or with -debugUnlockPro) use the fake store.</summary>
    private IProStore CreateProStore(LaunchOptions options)
    {
#if DEBUG
        if (options.DebugUnlockPro || !HasPackageIdentity()) return new FakeProStore(options.DebugUnlockPro);
#endif
        return new MicrosoftStoreProStore(_hwnd);
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Pro features call this first; true when unlocked (possibly after buying in the dialog).</summary>
    private async Task<bool> RequireProAsync(ProFeature feature) =>
        _pro.IsUnlocked(feature) || await UpgradeDialog.ShowAsync(Content.XamlRoot, _pro, feature);

    private Task<bool> ShowUpgradeAsync(ProFeature? feature) => UpgradeDialog.ShowAsync(Content.XamlRoot, _pro, feature);

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    // DEBUG -qaAction (CLAUDE.md): runs 1.5 s after the picture is live.
    private void RunQaActionSoon(string action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            if (!Model.IsLive) return;
            timer.Stop();
            AppLog.Write($"[qa] {action}");
            switch (action)
            {
                case "screenshot": SaveScreenshot(); break;
                case "copy": CopyFrame(); break;
                case "size50": SizeToVideo(0.5); break;
                case "actualPixels": SizeToVideo(1); break;
                case "fullScreen": ToggleFullScreen(); break;
                case "settings": OpenSettings(); break;
                case "record3s": _ = RecordForQaAsync(); break;
                case "recent15": _ = RecentForQaAsync(); break;
                case "openCards":
                    foreach (var device in _capture?.Catalog.Devices ?? []) App.OpenCardWindow(device.Key);
                    break;
                case "scopes": ScopesWindow.Show(_scopes); break;
                case "grid": GridWindow.Show(); break;
                case "patternCycle": _ = PatternCycleForQaAsync(); break;
                case "upgrade": _ = ShowUpgradeAsync(ProFeature.Recording); break;
            }
        };
        timer.Start();
    }

    private async Task RecordForQaAsync()
    {
        await ToggleRecordingAsync();
        await Task.Delay(3000);
        if (_recording.IsRecording) await ToggleRecordingAsync();
    }

    // Bars for 3 s, then back to the card: the card is released and reopened (CLAUDE.md -qaAction patternCycle).
    private async Task PatternCycleForQaAsync()
    {
        ShowTestPattern(TestPatternKind.SmpteBars);
        await Task.Delay(3000);
        ShowTestPattern(null);
    }

    // Keeps 25 s, then saves the last 15 s (CLAUDE.md -qaAction recent15).
    private async Task RecentForQaAsync()
    {
        await SetKeepRecentAsync(true);
        await Task.Delay(25_000);
        await SaveRecentAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Last size and position, if that spot is still on a connected display; else 1280×760.</summary>
    private void RestoreFrame()
    {
        if (_prefs.State.Frame is { Width: > 200, Height: > 150 } frame)
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
        if (!IsPrimary) return;
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        var (position, size) = (AppWindow.Position, AppWindow.Size);
        _prefs.UpdateState(state => state with { Frame = new WindowFrame(position.X, position.Y, size.Width, size.Height) });
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
        var frames = Interlocked.Exchange(ref _frames, 0);
        var fps = frames / seconds;
        var allocated = GC.GetTotalAllocatedBytes();
        var gen0 = GC.CollectionCount(0);
        var frameAlloc = Interlocked.Exchange(ref _frameThreadAllocated, 0);
        _allocText = $"alloc {(allocated - _lastAllocated) / Math.Max(1, frames)} B/frame process, {frameAlloc / Math.Max(1, frames)} B/frame render path, gen0 {gen0 - _lastGen0}/s";
        (_lastAllocated, _lastGen0) = (allocated, gen0);
        WriteSoak(fps);
        if (++_perfTicks % 30 == 0 && Model.IsLive) AppLog.Write($"[perf] {fps:F1} fps, {_allocText}, heap {GC.GetTotalMemory(false) / 1_048_576} MB");
        if (!Model.DiagnosticsShown) return;
        var (w, h) = _renderer.SourceSize;
        var surface = _childWindow != null ? "HWND" : "SwapChainPanel";
        var sound = _audio is { } a ? $"\naudio buffered {a.BufferedMs:F0} ms" : "";
        var capture = _capture is { CurrentType: { } type } c
            ? $"\n{type.FourCC} {type.Width}×{type.Height} @ {type.FrameRate:F2}{(type.DecodedByWindows ? " (Windows MJPEG decode)" : "")}, {c.Stats?.Dropped ?? 0} dropped"
            : "";
        Model.StatsText = $"{Model.InputName}\n{w}×{h}  {fps:F1} fps{capture}\n{_renderer.AdapterName}\n{surface}, tearing {(_target.Tearing ? "on" : "off")}{sound}\n{_allocText}";
    }

    /// <summary>DEBUG -qaSoakMinutes N: one CSV row a minute (fps, drops, memory, audio buffer) to Documents\qa-soak.csv.</summary>
    private void WriteSoak(double fps)
    {
        if (_options.QaSoakMinutes is not { } minutes || DateTime.Now < _soakNext) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "qa-soak.csv");
        if (!_soakStarted)
        {
            _soakStarted = true;
            _soakEnd = DateTime.Now.AddMinutes(minutes);
            File.WriteAllText(path, "time,fps,dropped,working_set_mb,gc_heap_mb,gen0,gen2,audio_buffered_ms,alloc\n");
        }
        _soakNext = DateTime.Now.AddMinutes(1);
        var row = string.Join(',', DateTime.Now.ToString("HH:mm:ss"), fps.ToString("F1"), _capture?.Stats?.Dropped ?? 0,
            Environment.WorkingSet / 1_048_576, GC.GetTotalMemory(false) / 1_048_576, GC.CollectionCount(0), GC.CollectionCount(2),
            _audio?.BufferedMs.ToString("F1") ?? "", $"\"{_allocText}\"");
        File.AppendAllText(path, row + "\n");
        if (DateTime.Now >= _soakEnd)
        {
            AppLog.Write($"[qa] soak finished: {path}");
            _soakNext = DateTime.MaxValue; // done
        }
    }

    private DateTime _soakEnd = DateTime.MaxValue;
    private bool _soakStarted;

    private void Shutdown()
    {
        SaveFrame();
        _settings.Flush();
        _recordTimer.Stop();
        _recording.Dispose();
        SettingsWindow.CloseIfOpen();
        ScopesWindow.CloseIfOpen();
        _statsTimer.Stop();
        _idleTimer.Stop();
        ShowCursor();
        _audio?.Dispose();
        _capture?.Dispose();
        _testPattern?.Dispose();
        _tray?.Dispose();
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
