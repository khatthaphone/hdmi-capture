using HDMICaptain.Linux.Media;
using HDMICaptain.Linux.Media.Render;

namespace HDMICaptain.Linux.App;

/// <summary>
/// The video surface: a GLES 3 <see cref="Gtk.GLArea"/> drawn by the shared renderer. Each published frame
/// queues one render on the GTK main loop (coalesced), so a frame is drawn on the next frame-clock tick.
/// </summary>
public sealed class VideoView
{
    private readonly GlesRenderer _renderer;
    private readonly SynchronizationContext _ui;
    private int _queued;
    private int _width, _height;

    public Gtk.GLArea Widget { get; }

    /// <summary>The GL context failed (no GLES 3): the reason, for the status screen.</summary>
    public string? Error { get; private set; }

    public VideoView(GlesRenderer renderer)
    {
        _renderer = renderer;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("VideoView needs the GTK synchronization context");
        Widget = Gtk.GLArea.New();
        Widget.SetAllowedApis(Gdk.GLAPI.Gles);
        Widget.SetRequiredVersion(3, 0);
        Widget.SetHexpand(true);
        Widget.SetVexpand(true);
        Widget.OnRealize += (_, _) => Realize();
        Widget.OnUnrealize += (_, _) => Unrealize();
        Widget.OnResize += (_, args) => (_width, _height) = (args.Width, args.Height);
        Widget.OnRender += (_, _) => Render();
        _renderer.FramePublished += OnFramePublished;
    }

    private void Realize()
    {
        Widget.MakeCurrent();
        if (Widget.GetError() is { } error)
        {
            Error = error.Message;
            LinuxLog.Write($"video: no GL context: {Error}");
            return;
        }
        var ok = _renderer.GlInit();
        LinuxLog.Write($"video: GL {(Widget.GetApi() == Gdk.GLAPI.Gles ? "ES" : "desktop")} context, renderer {(ok ? "ready" : "failed")}");
        if (!ok) Error = "The graphics driver can't run the video shaders.";
    }

    private void Unrealize()
    {
        Widget.MakeCurrent();
        if (Widget.GetError() == null) _renderer.GlRelease();
    }

    private bool Render()
    {
        Interlocked.Exchange(ref _queued, 0);
        if (Error == null && _renderer.GlRender(_width, _height) && MediaEvents.Log.IsEnabled())
            MediaEvents.Log.FrameDrawn(_renderer.Stats.LastLatencyNs / 1e6);
        return true;
    }

    // Producer thread: hop to the main loop once per burst of frames.
    private void OnFramePublished()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ui.Post(static state => ((VideoView)state!).Widget.QueueRender(), this);
    }

    /// <summary>The clean pass-1 picture, read with this view's context current (screenshots).</summary>
    public (byte[] Rgba, int Width, int Height)? ReadPass1()
    {
        Widget.MakeCurrent();
        return Widget.GetError() == null ? _renderer.GlReadPass1() : null;
    }
}
