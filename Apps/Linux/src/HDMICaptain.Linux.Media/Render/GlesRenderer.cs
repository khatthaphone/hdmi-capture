using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HDMICaptain.Core;

namespace HDMICaptain.Linux.Media.Render;

/// <summary>Pixel layouts the shared renderer takes (values match native/render-gles frame.h).</summary>
public enum RenderPixelFormat { Nv12 = 0, Yuy2 = 1, I420 = 2, I422 = 3 }

/// <summary>A frame's YCbCr matrix. Auto: BT.709 from 720 lines up. MJPEG (JFIF) frames are BT.601.</summary>
public enum FrameMatrix { Auto = 0, Bt601 = 1, Bt709 = 2 }

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

    public void Publish(long timestampNs, bool fullRange, FrameMatrix matrix = FrameMatrix.Auto) =>
        hc_renderer_publish(_handle, timestampNs, fullRange ? 1 : 0, (int)matrix);

    public void StartPattern(TestPatternKind pattern, int width = 1920, int height = 1080, int fps = 60) =>
        hc_renderer_start_pattern(_handle, (int)pattern, width, height, fps);

    public void StopPattern() => hc_renderer_stop_pattern(_handle);

    public void SetOptions(ScaleMode scale, FilterMode filter, ColorRange range, ColorMatrix matrix = ColorMatrix.Auto) =>
        hc_renderer_set_options(_handle, (int)scale, (int)filter, (int)range, (int)matrix);

    public void SetOptions(RenderOptions options) => SetOptions(options.Scale, options.Filter, options.ColorRange, options.Matrix);

    /// <summary>Picture look in pass 2 (adjustments, then the LUT blended by <paramref name="lutIntensity"/>); never in pass 1.</summary>
    public void SetLook(ImageAdjustments adjustments, CubeLUT? lut = null, double lutIntensity = 1)
    {
        fixed (float* table = lut?.Entries)
        fixed (float* min = lut?.DomainMin)
        fixed (float* max = lut?.DomainMax)
            hc_renderer_set_look(_handle, (float)adjustments.Brightness, (float)adjustments.Contrast, (float)adjustments.Saturation,
                (float)adjustments.Gamma, (float)adjustments.Sharpness, (float)lutIntensity, lut?.Size ?? 0, table, min, max);
    }

    public RenderStats Stats { get { RenderStats s; hc_renderer_stats(_handle, &s); return s; } }

    public const int ScopeColumns = 320, ScopeRows = 180;

    /// <summary>While on, every 5th frame leaves a 320×180 grid of Y,Cb,Cr bytes for the scopes (costs ~0.2 ms).</summary>
    public void SetScopesEnabled(bool on) => hc_renderer_set_scopes(_handle, on ? 1 : 0);

    /// <summary>Copies the latest scope grid into <paramref name="grid"/> (≥ 320×180×3); returns its sequence, 0 = none yet.</summary>
    public long ReadScopes(Span<byte> grid)
    {
        fixed (byte* p = grid) return hc_renderer_scopes(_handle, p, grid.Length);
    }

    public RenderSignal Signal { get { RenderSignal s; hc_renderer_signal(_handle, &s); return s; } }

    public bool GlInit() => hc_renderer_gl_init(_handle) != 0;

    public void GlRelease() => hc_renderer_gl_release(_handle);

    /// <summary>Converts the newest frame and draws it into the bound framebuffer (pixels). False: no picture yet.</summary>
    public bool GlRender(int width, int height) => hc_renderer_render(_handle, width, height) != 0;

    /// <summary>Draws into the region (x, y from the bottom-left, w, h) of the bound framebuffer: one grid tile.</summary>
    public bool GlRender(int x, int y, int width, int height) => hc_renderer_render_region(_handle, x, y, width, height) != 0;

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
    [LibraryImport(Lib)] private static partial void hc_renderer_publish(nint r, long timestampNs, int fullRange, int matrix);
    [LibraryImport(Lib)] private static partial long hc_renderer_monotonic_ns();
    [LibraryImport(Lib)] private static partial void hc_renderer_start_pattern(nint r, int pattern, int width, int height, int fps);
    [LibraryImport(Lib)] private static partial void hc_renderer_stop_pattern(nint r);
    [LibraryImport(Lib)] private static partial void hc_renderer_set_options(nint r, int scale, int filter, int range, int matrix);
    [LibraryImport(Lib)] private static partial void hc_renderer_set_look(nint r, float brightness, float contrast, float saturation, float gamma, float sharpness,
        float lutIntensity, int lutSize, float* lut, float* lutMin, float* lutMax);
    [LibraryImport(Lib)] private static partial void hc_renderer_stats(nint r, RenderStats* stats);
    [LibraryImport(Lib)] private static partial void hc_renderer_signal(nint r, RenderSignal* signal);
    [LibraryImport(Lib)] private static partial void hc_renderer_set_scopes(nint r, int enabled);
    [LibraryImport(Lib)] private static partial long hc_renderer_scopes(nint r, byte* output, int size);
    [LibraryImport(Lib)] private static partial int hc_renderer_gl_init(nint r);
    [LibraryImport(Lib)] private static partial void hc_renderer_gl_release(nint r);
    [LibraryImport(Lib)] private static partial int hc_renderer_render(nint r, int width, int height);
    [LibraryImport(Lib)] private static partial int hc_renderer_render_region(nint r, int x, int y, int width, int height);
    [LibraryImport(Lib)] private static partial int hc_renderer_read_pass1(nint r, byte* rgba, int capacity, int* width, int* height);
}
