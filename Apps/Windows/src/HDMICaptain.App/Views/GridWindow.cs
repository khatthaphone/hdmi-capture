using HDMICaptain.App.Video;
using HDMICaptain.App.ViewModels;
using HDMICaptain.Core;
using HDMICaptain.Media.Audio;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HDMICaptain.App.Views;

/// <summary>
/// Multi-View grid (MultiViewGrid.swift, task 098): every connected card side by side in one swap chain.
/// Click a tile to hear it; double-click to open it in its own window. One reader per card per process, so a card
/// some viewer window already shows is tapped from that window, and only the others get a session of their own here.
/// With <c>-testPattern</c> the four test patterns stand in for cards.
/// </summary>
public sealed class GridWindow : Window
{
    private static GridWindow? _instance;

    private readonly GridRenderer _renderer;
    private readonly SwapChainPanel _panel = new();
    private readonly SwapChainPanelHost _host;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly TextBlock _empty;
    private readonly List<Tile> _tiles = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _audioKey;
    private string? _audioOpenKey;
    private WasapiAudioPipeline? _audio;
    private bool _silenced;

    private GridWindow()
    {
        Title = Strings.Get("Grid_Title");
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 760));

        _renderer = GridRenderer.Create(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _empty = new TextBlock
        {
            Text = Strings.Get("Grid_Empty"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White) { Opacity = 0.7 },
        };
        var root = new Grid
        {
            Background = new SolidColorBrush(Colors.Black),
            RequestedTheme = ElementTheme.Dark,
            Children = { _panel, _overlay, _empty },
        };
        Content = root;
        _host = new SwapChainPanelHost(_panel, _renderer);
        _panel.SizeChanged += (_, _) => Layout();
        _panel.CompositionScaleChanged += (_, _) => Layout();
        root.Tapped += (_, e) => { if (TileAt(e.GetPosition(_panel)) is { } tile) SetAudioTile(tile.Key); };
        root.DoubleTapped += (_, e) => { if (TileAt(e.GetPosition(_panel)) is { Pattern: null } tile) App.OpenCardWindow(tile.Key); };

        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) App.FocusAudio(this); };
        _timer.Tick += (_, _) => Reconcile();
        _timer.Start();
        Closed += (_, _) => Shutdown();
        Reconcile();
    }

    /// <summary>Opens the grid, or brings it forward.</summary>
    public static GridWindow Show()
    {
        _instance ??= new GridWindow();
        _instance.Activate();
        return _instance;
    }

    public static GridWindow? Open => _instance;

    public static void CloseIfOpen() => _instance?.Close();

    /// <summary>Whether the grid runs its own session for <paramref name="key"/> (so viewer windows' fallbacks skip it).</summary>
    public bool Owns(string key) => _tiles.Any(t => t.Key == key && t.Own != null);

    /// <summary>
    /// A viewer window is about to open <paramref name="key"/>: close the grid's session first (two readers in one
    /// process preempt each other), then tap that window once it shows the card.
    /// </summary>
    public void Release(string key)
    {
        if (_tiles.FirstOrDefault(t => t.Key == key) is not { Own: not null } tile) return;
        tile.CloseOwn();
        tile.HandOverUntil = DateTime.Now.AddSeconds(5);
        tile.Status = Strings.Get("Grid_Opening");
    }

    /// <summary>"Play sound from the frontmost window": the grid is heard only while it has focus.</summary>
    public void SetSilenced(bool silenced)
    {
        if (_silenced == silenced) return;
        _silenced = silenced;
        if (_audio is { } audio) audio.Settings = audio.Settings with { Muted = silenced };
    }

    // MARK: - Tiles

    private sealed class Tile(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; set; } = name;
        public TestPatternKind? Pattern { get; init; }
        public CaptureDevice? Device { get; set; }
        public volatile int Index;
        public ICaptureSource? PatternSource;
        public CaptureController? Own;
        public MainWindow? Tapped;
        public FrameHandler? Handler;
        public DateTime HandOverUntil;
        public string Status = Strings.Get("Grid_Connecting");
        public int Frames;
        public double Fps;
        public volatile int Width;
        public volatile int Height;
        public DevicePreset? Preset;
        public int LookIndex = -1;
        public (string? Path, CubeLUT? Lut) LutCache;

        public void CloseOwn()
        {
            var own = Own;
            Own = null;
            if (own == null) return;
            if (Handler != null) own.FrameArrived -= Handler;
            own.Dispose();
        }

        public void Untap()
        {
            if (Tapped != null && Handler != null) Tapped.FrameTap -= Handler;
            Tapped = null;
        }

        public void Close()
        {
            Untap();
            CloseOwn();
            if (PatternSource != null)
            {
                if (Handler != null) PatternSource.FrameArrived -= Handler;
                PatternSource.Dispose();
                PatternSource = null;
            }
        }
    }

    /// <summary>Every second: one tile per card (or test pattern), each fed from a viewer window or its own session.</summary>
    private void Reconcile()
    {
        var wanted = WantedTiles();
        foreach (var gone in _tiles.Where(t => wanted.All(w => w.Key != t.Key)).ToList())
        {
            gone.Close();
            _tiles.Remove(gone);
            if (_audioKey == gone.Key) _audioKey = null;
        }
        foreach (var w in wanted.Where(w => _tiles.All(t => t.Key != w.Key)))
            _tiles.Add(w);
        _tiles.Sort((a, b) => wanted.FindIndex(w => w.Key == a.Key).CompareTo(wanted.FindIndex(w => w.Key == b.Key)));
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (_tiles[i].Index != i) _renderer.ClearTile(i);
            _tiles[i].Index = i;
        }
        _renderer.TileCount = _tiles.Count;
        foreach (var tile in _tiles) ApplyLook(tile);

        var claimsChanged = false;
        foreach (var tile in _tiles)
        {
            tile.Fps = Interlocked.Exchange(ref tile.Frames, 0);
            if (tile.Pattern is { } kind)
            {
                if (tile.PatternSource == null)
                {
                    tile.Handler ??= Feed(tile);
                    tile.PatternSource = new TestPatternSource(kind, PixelFormat.Nv12);
                    tile.PatternSource.FrameArrived += tile.Handler;
                    tile.PatternSource.Start();
                    tile.Status = "";
                }
                continue;
            }

            var viewer = App.ViewerShowing(tile.Key);
            if (viewer != null)
            {
                claimsChanged |= tile.Own != null;
                tile.CloseOwn();
                if (!ReferenceEquals(tile.Tapped, viewer))
                {
                    tile.Untap();
                    tile.Handler ??= Feed(tile);
                    tile.Tapped = viewer;
                    viewer.FrameTap += tile.Handler;
                }
                tile.Status = tile.Fps > 0 ? "" : Strings.Get("Grid_NoPicture");
                continue;
            }

            tile.Untap();
            if (tile.Own == null && DateTime.Now >= tile.HandOverUntil && tile.Device != null)
            {
                tile.Handler ??= Feed(tile);
                tile.Own = CaptureController.CreateDefault(tile.Key, new CaptureControllerOptions { PinnedToPreferred = true });
                tile.Own.Log += line => AppLog.Write($"[grid] {line}");
                tile.Own.FrameArrived += tile.Handler;
                var own = tile.Own;
                own.StateChanged += state => DispatcherQueue.TryEnqueue(() => OnTileState(tile, own, state));
                own.Start();
                claimsChanged = true;
            }
        }
        if (claimsChanged) App.NotifyClaimsChanged();

        if (_audioKey == null || _tiles.All(t => t.Key != _audioKey))
            _audioKey = _tiles.FirstOrDefault(t => t.Pattern == null)?.Key;
        UpdateAudio();
        _empty.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Layout();
    }

    /// <summary>
    /// The card's saved preset (the one its window edits), so a tile looks like that card's own window:
    /// scale, filter, range, matrix, adjustments and LUT. Re-read every second, applied when it changed.
    /// </summary>
    private void ApplyLook(Tile tile)
    {
        if (tile.Pattern != null) return;
        var store = App.Prefs.Presets;
        var preset = store.Has(tile.Key) ? store.Get(tile.Key) : store.Get(SettingsModel.DefaultKey);
        if (tile.LookIndex == tile.Index && preset == tile.Preset) return;
        tile.Preset = preset;
        tile.LookIndex = tile.Index;
        var a = preset.Adjustments;
        _renderer.SetLook(tile.Index, new TileLook(preset.RenderOptions,
            ((float)a.Brightness, (float)a.Contrast, (float)a.Saturation, (float)a.Gamma, (float)a.Sharpness), LutFor(tile, preset)));
    }

    // Parses each tile's .cube once per path; a file that fails just leaves the tile without a LUT.
    private static LutData? LutFor(Tile tile, DevicePreset preset)
    {
        if (preset.LutPath is not { } path) return null;
        if (tile.LutCache.Path != path)
        {
            try
            {
                tile.LutCache = (path, CubeLUT.Parse(File.ReadAllText(path), System.IO.Path.GetFileNameWithoutExtension(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CubeLUT.ParseException)
            {
                AppLog.Write($"[grid] LUT {path}: {e.Message}");
                tile.LutCache = (path, null);
            }
        }
        if (tile.LutCache.Lut is not { } lut) return null;
        return LutData.FromRgb(lut.Size, lut.Entries, new System.Numerics.Vector3(lut.DomainMin[0], lut.DomainMin[1], lut.DomainMin[2]),
            new System.Numerics.Vector3(lut.DomainMax[0], lut.DomainMax[1], lut.DomainMax[2]), (float)preset.LutIntensity);
    }

    private List<Tile> WantedTiles()
    {
        if (App.Options.TestPattern != null)
            return Enum.GetValues<TestPatternKind>().Select(k => new Tile($"pattern:{k}", TestPatternSource.Title(k)) { Pattern = k }).ToList();
        var devices = App.Devices;
        foreach (var tile in _tiles)
            if (devices.FirstOrDefault(d => d.Key == tile.Key) is { } device)
                (tile.Device, tile.Name) = (device, device.Name);
        return devices.Select(d => _tiles.FirstOrDefault(t => t.Key == d.Key) ?? new Tile(d.Key, d.Name) { Device = d }).ToList();
    }

    // Source thread: straight into the tile's cell. A tapped viewer may have switched cards since the last check.
    private FrameHandler Feed(Tile tile) => (in VideoFrame frame) =>
    {
        if (tile.Tapped is { } viewer && viewer.ShowingCard != tile.Key) return;
        _renderer.Render(tile.Index, in frame);
        tile.Width = frame.Width;
        tile.Height = frame.Height;
        Interlocked.Increment(ref tile.Frames);
    };

    private void OnTileState(Tile tile, CaptureController owner, CaptureState state)
    {
        if (!ReferenceEquals(tile.Own, owner)) return;
        tile.Status = state switch
        {
            CaptureState.Live => "",
            CaptureState.Connecting => Strings.Get("Grid_Connecting"),
            CaptureState.Recovering { Reason: RecoveryReason.Waiting or RecoveryReason.Disconnected } => Strings.Get("Grid_Disconnected"),
            CaptureState.Recovering => Strings.Get("Grid_Reconnecting"),
            CaptureState.Paused { Reason: PauseReason.PrivacyToggle } => Strings.Get("Grid_CameraOff"),
            CaptureState.Paused { Reason: PauseReason.InUseByAnotherApp } => Strings.Get("Grid_InUse"),
            CaptureState.Paused => Strings.Get("Grid_Paused"),
            CaptureState.Failed f => f.Message,
            _ => "",
        };
        if (state is not CaptureState.Live) _renderer.ClearTile(tile.Index);
        if (tile.Key == _audioKey && state is CaptureState.Live) UpdateAudio();
        Layout();
    }

    private Tile? TileAt(Windows.Foundation.Point point)
    {
        var scale = _panel.CompositionScaleX;
        var index = TileLayout.HitTest(PixelCells(), point.X * scale, point.Y * scale);
        return index >= 0 && index < _tiles.Count ? _tiles[index] : null;
    }

    private Core.Rect[] PixelCells()
    {
        var scale = _panel.CompositionScaleX;
        return TileLayout.Compute(_tiles.Count, (int)Math.Ceiling(_panel.ActualWidth * scale), (int)Math.Ceiling(_panel.ActualHeight * _panel.CompositionScaleY), GridRenderer.Gap);
    }

    /// <summary>Labels and the audio highlight over each cell (XAML, so they never reach the picture).</summary>
    private void Layout()
    {
        _overlay.Children.Clear();
        var scale = _panel.CompositionScaleX;
        if (scale <= 0) return;
        var cells = PixelCells();
        for (var i = 0; i < cells.Length && i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var cell = cells[i];
            var (x, y, w, h) = (cell.X / scale, cell.Y / scale, cell.Width / scale, cell.Height / scale);
            var hearing = tile.Key == _audioKey;
            if (hearing && _tiles.Count > 1)
            {
                var ring = new Rectangle { Width = w, Height = h, Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], StrokeThickness = 2 };
                Canvas.SetLeft(ring, x);
                Canvas.SetTop(ring, y);
                _overlay.Children.Add(ring);
            }

            var info = tile.Status.Length > 0 || tile.Width == 0 ? tile.Status : $"{tile.Width}×{tile.Height}  {tile.Fps:0} fps";
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (tile.Pattern == null)
                label.Children.Add(new FontIcon { Glyph = hearing ? "\uE767" : "\uE74F", FontSize = 13 });
            label.Children.Add(new TextBlock { Text = tile.Name, FontSize = 12, MaxWidth = Math.Max(40, w - 140), TextTrimming = TextTrimming.CharacterEllipsis });
            if (info.Length > 0) label.Children.Add(new TextBlock { Text = info, FontSize = 12, Opacity = 0.7 });
            var chip = new Border
            {
                Background = new SolidColorBrush(Colors.Black) { Opacity = 0.6 },
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Child = label,
            };
            AutomationProperties.SetName(chip, $"{tile.Name} {info}".Trim());
            Canvas.SetLeft(chip, x + 8);
            Canvas.SetTop(chip, y + h - 36);
            _overlay.Children.Add(chip);
        }
    }

    // MARK: - Audio

    private void SetAudioTile(string key)
    {
        if (_audioKey == key) return;
        _audioKey = key;
        UpdateAudio();
        Layout();
    }

    /// <summary>Plays the chosen tile's card through its own capture endpoint (same ContainerId as its video).</summary>
    private void UpdateAudio()
    {
        var device = _tiles.FirstOrDefault(t => t.Key == _audioKey)?.Device;
        if (device?.Key == _audioOpenKey) return;
        _audioOpenKey = device?.Key;
        var old = Interlocked.Exchange(ref _audio, null);
        if (old != null) Task.Run(old.Dispose);
        if (device?.Usb?.ContainerId is not { } container) return;
        var settings = new AudioSettings(Muted: _silenced);
        Task.Run(() =>
        {
            if (AudioEndpoints.CaptureFor(container) is not { } endpoint) return;
            var audio = new WasapiAudioPipeline(endpoint.Id) { Settings = settings };
            audio.Log += line => AppLog.Write($"[grid audio] {line}");
            audio.Start();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_audioOpenKey == device.Key && _instance == this) Interlocked.Exchange(ref _audio, audio)?.Dispose();
                else audio.Dispose();
            });
        });
    }

    private void Shutdown()
    {
        _timer.Stop();
        foreach (var tile in _tiles) tile.Close();
        _tiles.Clear();
        Interlocked.Exchange(ref _audio, null)?.Dispose();
        _audioOpenKey = null;
        _host.Dispose();
        _renderer.Dispose();
        _instance = null;
        App.NotifyClaimsChanged();
    }
}
