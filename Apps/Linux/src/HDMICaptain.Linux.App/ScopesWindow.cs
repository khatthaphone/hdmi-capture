using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Linux.Media.Render;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Scopes (task 117): histogram, waveform and vectorscope from the renderer's 320×180 Y/Cb/Cr grid (sampled from the
/// frame on the GL thread every 5th frame while this window is open), analysed by the shared <see cref="ScopeData"/>.
/// Ten updates a second; capture never waits for it.
/// </summary>
public sealed class ScopesWindow
{
    private static ScopesWindow? _instance;
    private readonly GlesRenderer _renderer;
    private readonly Adw.Window _window;
    private readonly Gtk.Picture _histogram = Picture(), _waveform = Picture(), _vector = Picture();
    private readonly byte[] _grid = new byte[GlesRenderer.ScopeColumns * GlesRenderer.ScopeRows * 3];
    private long _sequence;
    private uint _timer;

    public static void Show(Gtk.Window parent, GlesRenderer renderer)
    {
        _instance ??= new ScopesWindow(parent, renderer);
        _instance._window.Present();
    }

    private ScopesWindow(Gtk.Window parent, GlesRenderer renderer)
    {
        _renderer = renderer;
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        box.SetMarginTop(8);
        box.SetMarginBottom(8);
        box.SetMarginStart(8);
        box.SetMarginEnd(8);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        row.SetHomogeneous(true);
        row.Append(Labelled("Waveform (luma)", _waveform));
        row.Append(Labelled("Vectorscope", _vector));
        box.Append(row);
        box.Append(Labelled("Histogram", _histogram));
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(box);
        _window = Adw.Window.New();
        _window.SetTitle("Scopes");
        _window.SetTransientFor(parent);
        _window.SetDefaultSize(720, 520);
        _window.SetContent(toolbar);
        _renderer.SetScopesEnabled(true);
        _timer = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 100, () =>
        {
            Update();
            return true;
        });
        _window.OnCloseRequest += (_, _) =>
        {
            if (_timer != 0) GLib.Source.Remove(_timer);
            _timer = 0;
            _renderer.SetScopesEnabled(false);
            _instance = null;
            return false;
        };
    }

    private void Update()
    {
        var sequence = _renderer.ReadScopes(_grid);
        if (sequence == 0 || sequence == _sequence) return;
        _sequence = sequence;
        var data = ScopeData.Analyze(_grid, GlesRenderer.ScopeColumns, GlesRenderer.ScopeRows);
        _waveform.SetPaintable(Texture(data.WaveformPixels(), ScopeData.WaveformColumns, ScopeData.Levels));
        _vector.SetPaintable(Texture(data.VectorscopePixels(), ScopeData.VectorSize, ScopeData.VectorSize));
        _histogram.SetPaintable(Texture(data.HistogramPixels(), ScopeData.Levels, 100));
    }

    // ScopeData pixels are 0xAARRGGBB words: B, G, R, A bytes in memory on little-endian.
    private static Gdk.Texture Texture(uint[] pixels, int width, int height)
    {
        using var bytes = GLib.Bytes.New(MemoryMarshal.AsBytes(pixels.AsSpan()));
        return Gdk.MemoryTexture.New(width, height, Gdk.MemoryFormat.B8g8r8a8, bytes, (nuint)(width * 4));
    }

    private static Gtk.Picture Picture()
    {
        var picture = Gtk.Picture.New();
        picture.SetContentFit(Gtk.ContentFit.Fill);
        picture.SetCanShrink(true);
        picture.SetHexpand(true);
        picture.SetVexpand(true);
        picture.SetSizeRequest(200, 140);
        return picture;
    }

    private static Gtk.Widget Labelled(string title, Gtk.Widget child)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        var label = Gtk.Label.New(title);
        label.AddCssClass("caption-heading");
        label.SetXalign(0);
        box.Append(label);
        var frame = Gtk.Frame.New(null);
        frame.SetChild(child);
        box.Append(frame);
        return box;
    }
}
