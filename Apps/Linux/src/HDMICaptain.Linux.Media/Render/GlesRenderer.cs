using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HDMICaptain.Core;

namespace HDMICaptain.Linux.Media.Render;

/// <summary>Pixel layouts the shared renderer takes (values match native/render-gles frame.h).</summary>
public enum RenderPixelFormat { Nv12 = 0, Yuy2 = 1, I420 = 2, I422 = 3 }

[StructLayout(LayoutKind.Sequential)]
public struct RenderStats
{
    public long FramesIn, FramesDrawn, LastLatencyNs;
    public int Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
public struct RenderSignal
{
    public double MeanY, MeanCb, MeanCr, RangeY, RangeChroma;
    public int FullRange;
    public long Count;
}

/// <summary>
/// The shared GLES 3 renderer (native/render-gles, the Android renderer's pipeline) for a host that owns
/// the GL context: frames go in from any thread; the Gl* calls need that context current (GtkGLArea).
/// </summary>
public sealed unsafe partial class GlesRenderer : IDisposable
{
    private const string Lib = "hcrender";
    private nint _handle;
    private GCHandle _self;
    private Action? _onFrame;

    public GlesRenderer() => _handle = hc_renderer_create();

    /// <summary>Raised on the producer's thread for every published frame (schedule a draw from it).</summary>
    public event Action? FramePublished
    {
        add
        {
            _onFrame += value;
            if (!_self.IsAllocated)
            {
                _self = GCHandle.Alloc(this);
                hc_renderer_set_frame_callback(_handle, &OnFrame, GCHandle.ToIntPtr(_self));
            }
        }
        remove => _onFrame -= value;
    }

    public static long MonotonicNs => hc_renderer_monotonic_ns();

    /// <summary>A frame buffer to fill, then <see cref="Publish"/>. Null for an unsupported size.</summary>
    public Span<byte> Acquire(RenderPixelFormat format, int width, int height)
    {
        var data = hc_renderer_acquire(_handle, (int)format, width, height);
        return data == null ? default : new Span<byte>(data, FrameBytes(format, width, height));
    }

    public void Publish(long timestampNs, bool fullRange) => hc_renderer_publish(_handle, timestampNs, fullRange ? 1 : 0);

    public void StartPattern(TestPatternKind pattern, int width = 1920, int height = 1080, int fps = 60) =>
        hc_renderer_start_pattern(_handle, (int)pattern, width, height, fps);

    public void StopPattern() => hc_renderer_stop_pattern(_handle);

    public void SetOptions(ScaleMode scale, FilterMode filter, ColorRange range) =>
        hc_renderer_set_options(_handle, (int)scale, (int)filter, (int)range);

    public RenderStats Stats { get { RenderStats s; hc_renderer_stats(_handle, &s); return s; } }

    public RenderSignal Signal { get { RenderSignal s; hc_renderer_signal(_handle, &s); return s; } }

    public bool GlInit() => hc_renderer_gl_init(_handle) != 0;

    public void GlRelease() => hc_renderer_gl_release(_handle);

    /// <summary>Converts the newest frame and draws it into the bound framebuffer (pixels). False: no picture yet.</summary>
    public bool GlRender(int width, int height) => hc_renderer_render(_handle, width, height) != 0;

    /// <summary>The clean pass-1 picture (RGBA, top row first), or null when there is none.</summary>
    public (byte[] Rgba, int Width, int Height)? GlReadPass1()
    {
        var stats = Stats;
        if (stats.Width <= 0 || stats.Height <= 0) return null;
        var buffer = new byte[stats.Width * stats.Height * 4];
        int w, h;
        fixed (byte* p = buffer)
            if (hc_renderer_read_pass1(_handle, p, buffer.Length, &w, &h) == 0) return null;
        return (buffer, w, h);
    }

    public static int FrameBytes(RenderPixelFormat format, int width, int height) => format switch
    {
        RenderPixelFormat.Nv12 or RenderPixelFormat.I420 => width * height * 3 / 2,
        _ => width * height * 2,
    };

    public void Dispose()
    {
        if (_handle == 0) return;
        hc_renderer_set_frame_callback(_handle, null, 0);
        hc_renderer_destroy(_handle);
        _handle = 0;
        if (_self.IsAllocated) _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrame(nint user)
    {
        if (GCHandle.FromIntPtr(user).Target is GlesRenderer r) r._onFrame?.Invoke();
    }

    [LibraryImport(Lib)] private static partial nint hc_renderer_create();
    [LibraryImport(Lib)] private static partial void hc_renderer_destroy(nint r);
    [LibraryImport(Lib)] private static partial void hc_renderer_set_frame_callback(nint r, delegate* unmanaged[Cdecl]<nint, void> callback, nint user);
    [LibraryImport(Lib)] private static partial byte* hc_renderer_acquire(nint r, int format, int width, int height);
    [LibraryImport(Lib)] private static partial void hc_renderer_publish(nint r, long timestampNs, int fullRange);
    [LibraryImport(Lib)] private static partial long hc_renderer_monotonic_ns();
    [LibraryImport(Lib)] private static partial void hc_renderer_start_pattern(nint r, int pattern, int width, int height, int fps);
    [LibraryImport(Lib)] private static partial void hc_renderer_stop_pattern(nint r);
    [LibraryImport(Lib)] private static partial void hc_renderer_set_options(nint r, int scale, int filter, int range);
    [LibraryImport(Lib)] private static partial void hc_renderer_stats(nint r, RenderStats* stats);
    [LibraryImport(Lib)] private static partial void hc_renderer_signal(nint r, RenderSignal* signal);
    [LibraryImport(Lib)] private static partial int hc_renderer_gl_init(nint r);
    [LibraryImport(Lib)] private static partial void hc_renderer_gl_release(nint r);
    [LibraryImport(Lib)] private static partial int hc_renderer_render(nint r, int width, int height);
    [LibraryImport(Lib)] private static partial int hc_renderer_read_pass1(nint r, byte* rgba, int capacity, int* width, int* height);
}
