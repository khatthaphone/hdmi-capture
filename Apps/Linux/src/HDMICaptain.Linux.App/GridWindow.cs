using HDMICaptain.Core;
using HDMICaptain.Linux.Media;
using HDMICaptain.Linux.Media.Audio;
using HDMICaptain.Linux.Media.Capture;
using HDMICaptain.Linux.Media.Render;
using HDMICaptain.Linux.Media.Video;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Multi-View grid (task 118, spec §11): every connected card as a tile in one GL area (<see cref="TileLayout"/>).
/// Each tile has its own renderer in the grid's context. A card a viewer window is showing is tapped from that
/// window (V4L2 streams a card to one opener); any other card gets the tile's own pinned controller. With
/// -testPattern the four patterns fill the tiles. Sound comes from the selected tile (click), when the grid has focus.
/// </summary>
public sealed unsafe class GridWindow
{
    private const int Gap = 2;

    private readonly MainWindow _primary;
    private readonly LaunchOptions _options;
    private readonly Adw.Window _window;
    private readonly Gtk.GLArea _area;
    private readonly Gtk.Overlay _overlay = Gtk.Overlay.New();
    private readonly Adw.StatusPage _empty;
    private readonly SynchronizationContext _ui;
    private readonly List<Tile> _tiles = [];
    private int _queued;
    private uint _timer;
    private bool _glReady, _silenced;
    private string? _audioKey;
    private LinuxAudioPipeline? _audio;
    private string? _audioDevice;
    private (int W, int H) _pixels;

    public GridWindow(MainWindow primary, LaunchOptions options)
    {
        _primary = primary;
        _options = options;
        _ui = SynchronizationContext.Current!;
        _area = Gtk.GLArea.New();
        _area.SetAllowedApis(Gdk.GLAPI.Gles);
        _area.SetRequiredVersion(3, 0);
        _area.SetHexpand(true);
        _area.SetVexpand(true);
        _area.OnRealize += (_, _) =>
        {
            _area.MakeCurrent();
            _glReady = _area.GetError() == null;
        };
        _area.OnUnrealize += (_, _) =>
        {
            _area.MakeCurrent();
            foreach (var t in _tiles) t.ReleaseGl();
            _glReady = false;
        };
        _area.OnResize += (_, args) => _pixels = (args.Width, args.Height);
        _area.OnRender += (_, _) => Render();

        _empty = Adw.StatusPage.New();
        _empty.SetTitle("No capture cards");
        _empty.SetDescription("Plug in capture cards to see them side by side.");
        _empty.SetIconName("view-grid-symbolic");

        var overlay = _overlay;
        overlay.SetChild(_area);
        overlay.AddOverlay(_empty);
        var click = Gtk.GestureClick.New();
        click.OnPressed += (_, args) => Select(args.X, args.Y, args.NPress);
        overlay.AddController(click);

        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(overlay);
        _window = Adw.Window.New();
        _window.SetTitle("All Cards");
        _window.SetDefaultSize(1280, 760);
        _window.SetContent(toolbar);
        _window.OnNotify += (_, args) =>
        {
            if (args.Pspec.GetName() == "is-active" && _window.IsActive) Viewers.FocusAudio(this);
        };
        _window.OnCloseRequest += (_, _) =>
        {
            Shutdown();
            return false;
        };

        Sync();
        _timer = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 1000, () =>
        {
            Sync();
            return true;
        });
    }

    public void Present() => _window.Present();

    public void Close() => _window.Close();

    /// <summary>Cards the grid opened itself (its own controllers): viewers must not open them too.</summary>
    public IEnumerable<string> OwnedKeys => _tiles.Where(t => t.Own != null).Select(t => t.Key);

    public bool Owns(string key) => _tiles.Any(t => t.Key == key && t.Own != null);

    /// <summary>A viewer is about to open this card: give it up now (it is tapped from the viewer afterwards).</summary>
    public void Release(string key)
    {
        foreach (var t in _tiles.Where(t => t.Key == key)) t.CloseOwn();
        if (_audioKey == key) StopAudio();
    }

    public void SetSilenced(bool silenced)
    {
        _silenced = silenced;
        UpdateAudio();
    }

    /// <summary>Once a second: tiles follow the connected cards; each tile taps a viewer or owns its card.</summary>
    private void Sync()
    {
        var wanted = new List<(string Key, string Name, TestPatternKind? Pattern, CaptureDevice? Device)>();
        if (_options.TestPattern != null)
        {
            foreach (var kind in Enum.GetValues<TestPatternKind>()) wanted.Add(($"pattern:{kind}", kind.ToString(), kind, null));
        }
        else
        {
            foreach (var d in _primary.Capture?.Catalog.Devices ?? []) wanted.Add((d.Key, d.Name, null, d));
        }

        foreach (var gone in _tiles.Where(t => wanted.All(w => w.Key != t.Key)).ToList())
        {
            gone.Dispose(_area, _glReady);
            _overlay.RemoveOverlay(gone.Label);
            _tiles.Remove(gone);
        }
        foreach (var w in wanted.Where(w => _tiles.All(t => t.Key != w.Key)))
        {
            var tile = new Tile(w.Key, w.Name, w.Pattern, w.Device);
            tile.Renderer.FramePublished += QueueRender;
            _overlay.AddOverlay(tile.Label);
            _tiles.Add(tile);
            if (w.Pattern is { } p) tile.Renderer.StartPattern(p);
        }

        var claims = false;
        foreach (var tile in _tiles.Where(t => t.Pattern == null))
        {
            tile.Device = wanted.First(w => w.Key == tile.Key).Device;
            // The card's own preset, so a tile looks like that card's window.
            var preset = _primary.Settings.Store.Presets.Get(tile.Key);
            tile.Renderer.SetOptions(preset.RenderOptions);
            tile.Renderer.SetLook(preset.Adjustments);
            var viewer = Viewers.ViewerShowing(tile.Key);
            if (viewer != null)
            {
                claims |= tile.Own != null;
                tile.CloseOwn();
                tile.TapFrom(viewer);
            }
            else if (tile.Own == null)
            {
                tile.Untap();
                tile.Open(_ui, () => _ui.Post(_ => UpdateAudio(), null));
                claims = true;
            }
        }
        if (claims) Viewers.ClaimsChanged();

        foreach (var tile in _tiles) tile.UpdateLabel(tile.Key == _audioKey);
        _empty.SetVisible(_tiles.Count == 0);
        _audioKey ??= _tiles.FirstOrDefault(t => t.Pattern == null)?.Key;
        UpdateAudio();
        Layout();
    }

    private Rect[] Cells(int width, int height) => TileLayout.Compute(_tiles.Count, width, height, Gap * Math.Max(1, _window.GetScaleFactor()));

    private void Layout()
    {
        var (w, h) = (_area.GetWidth(), _area.GetHeight());
        var cells = TileLayout.Compute(_tiles.Count, w, h, Gap);
        for (var i = 0; i < _tiles.Count && i < cells.Length; i++)
        {
            _tiles[i].Label.SetMarginStart((int)cells[i].X + 8);
            _tiles[i].Label.SetMarginTop((int)cells[i].Y + 8);
        }
    }

    private void QueueRender()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ui.Post(_ => _area.QueueRender(), null);
    }

    private bool Render()
    {
        Interlocked.Exchange(ref _queued, 0);
        if (!_glReady) return true;
        var (w, h) = _pixels;
        var cells = Cells(w, h);
        Gl.ClearBlack(w, h);
        for (var i = 0; i < _tiles.Count && i < cells.Length; i++)
        {
            var tile = _tiles[i];
            if (!tile.GlReady) tile.InitGl();
            var c = cells[i];
            // TileLayout is top-left; GL regions start bottom-left.
            tile.Renderer.GlRender((int)c.X, (int)(h - c.Y - c.Height), (int)c.Width, (int)c.Height);
        }
        return true;
    }

    private void Select(double x, double y, int presses)
    {
        var index = TileLayout.HitTest(TileLayout.Compute(_tiles.Count, _area.GetWidth(), _area.GetHeight(), Gap), x, y);
        if (index < 0) return;
        var tile = _tiles[index];
        if (presses == 2 && tile.Pattern == null)
        {
            Viewers.OpenCard(tile.Key); // double-click: a window of its own
            return;
        }
        _audioKey = tile.Key;
        foreach (var t in _tiles) t.UpdateLabel(t.Key == _audioKey);
        UpdateAudio();
    }

    /// <summary>Sound for the selected tile when the grid owns that card and has focus; a viewer showing it plays its own.</summary>
    private void UpdateAudio()
    {
        var tile = _tiles.FirstOrDefault(t => t.Key == _audioKey);
        var hw = tile is { Own: not null, Device.Audio: { } card } && tile.Own.State is CaptureState.Live ? $"plughw:{card.Index},0" : null;
        if (_silenced) hw = null;
        if (hw == _audioDevice) return;
        StopAudio();
        _audioDevice = hw;
        if (hw == null) return;
        _audio = new LinuxAudioPipeline(
            () => AlsaCapture.TryOpen(hw, out var c) is var r && r == 0 ? c! : throw new AudioOpenException(r, $"{hw}: {AlsaCapture.Error(r)}"),
            () => PulsePlayback.TryOpen(out var p) == 0 ? p! : throw new AudioOpenException(-Errno.EIO, "no sound server"));
        _audio.Log += m => LinuxLog.Write($"[grid] audio: {m}");
        _audio.Start();
    }

    private void StopAudio()
    {
        _audio?.Dispose();
        _audio = null;
        _audioDevice = null;
    }

    private void Shutdown()
    {
        if (_timer != 0) GLib.Source.Remove(_timer);
        _timer = 0;
        StopAudio();
        foreach (var t in _tiles) t.Dispose(_area, _glReady);
        _tiles.Clear();
        Viewers.Grid = null;
        Viewers.ClaimsChanged();
    }

    private sealed class Tile(string key, string name, TestPatternKind? pattern, CaptureDevice? device)
    {
        public readonly string Key = key;
        public readonly string Name = name;
        public readonly TestPatternKind? Pattern = pattern;
        public CaptureDevice? Device = device;
        public readonly GlesRenderer Renderer = new();
        public readonly Gtk.Label Label = MakeLabel();
        public CaptureController? Own;
        public bool GlReady;
        private FrameWriter? _writer;
        private MainWindow? _tapped;
        private V4l2Source.FrameHandler? _tap;
        private long _lastFrames;

        private FrameWriter Writer => _writer ??= new FrameWriter(Renderer);

        private static Gtk.Label MakeLabel()
        {
            var label = Gtk.Label.New("");
            label.SetHalign(Gtk.Align.Start);
            label.SetValign(Gtk.Align.Start);
            label.SetCanTarget(false);
            label.AddCssClass("osd");
            label.AddCssClass("hc-badge");
            return label;
        }

        public void InitGl() => GlReady = Renderer.GlInit();

        public void ReleaseGl()
        {
            if (GlReady) Renderer.GlRelease();
            GlReady = false;
        }

        public void Open(SynchronizationContext ui, Action stateChanged)
        {
            Own = CaptureController.CreateDefault(Key, options: new CaptureControllerOptions { PinnedToPreferred = true });
            Own.Log += line => LinuxLog.Write($"[grid] {line}");
            Own.FrameArrived += (data, length, stream, ts) => Writer.Write(stream.FourCC, data, length, stream.Width, stream.Height, stream.BytesPerLine, ts);
            Own.StateChanged += _ => stateChanged();
            Own.Start();
        }

        public void CloseOwn()
        {
            Own?.Dispose();
            Own = null;
        }

        public void TapFrom(MainWindow viewer)
        {
            if (ReferenceEquals(_tapped, viewer)) return;
            Untap();
            _tap = (data, length, stream, ts) => Writer.Write(stream.FourCC, data, length, stream.Width, stream.Height, stream.BytesPerLine, ts);
            _tapped = viewer;
            viewer.FrameTap += _tap;
        }

        public void Untap()
        {
            if (_tapped != null && _tap != null) _tapped.FrameTap -= _tap;
            _tapped = null;
            _tap = null;
        }

        public void UpdateLabel(bool hasSound)
        {
            var frames = Renderer.Stats.FramesIn;
            var fps = frames - _lastFrames;
            _lastFrames = frames;
            var state = Pattern != null || fps > 0 ? $"{fps} fps" : Own?.State switch
            {
                CaptureState.Paused => "paused",
                CaptureState.Recovering => "reconnecting…",
                CaptureState.Connecting => "connecting…",
                _ => "no picture",
            };
            Label.SetLabel($"{(hasSound ? "🔊 " : "")}{Name} · {state}");
        }

        public void Dispose(Gtk.GLArea area, bool glReady)
        {
            Untap();
            CloseOwn();
            Renderer.StopPattern();
            if (glReady)
            {
                area.MakeCurrent();
                ReleaseGl();
            }
            _writer?.Dispose();
            Renderer.Dispose();
        }
    }
}

/// <summary>The two GL calls the grid makes itself (the gaps between tiles); everything else is the shared renderer.</summary>
internal static partial class Gl
{
    public static void ClearBlack(int width, int height)
    {
        glViewport(0, 0, width, height);
        glClearColor(0, 0, 0, 1);
        glClear(0x4000); // GL_COLOR_BUFFER_BIT
    }

    [System.Runtime.InteropServices.LibraryImport("libGLESv2.so.2")] private static partial void glViewport(int x, int y, int width, int height);
    [System.Runtime.InteropServices.LibraryImport("libGLESv2.so.2")] private static partial void glClearColor(float r, float g, float b, float a);
    [System.Runtime.InteropServices.LibraryImport("libGLESv2.so.2")] private static partial void glClear(uint mask);
}
