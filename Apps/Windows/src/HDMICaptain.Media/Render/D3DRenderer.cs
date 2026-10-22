using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Luid = Vortice.Luid;

using HDMICaptain.Media.Diagnostics;

namespace HDMICaptain.Media.Render;

/// <summary>See <see cref="D3DRenderer.SourceRendered"/>.</summary>
public delegate void SourceRenderedHandler(ID3D11Texture2D texture, long timestamp);

/// <summary>
/// The MetalRenderer equivalent on D3D11 (spec §13.4). Frames are rendered on the caller's thread (the source's),
/// in two passes like Android: pass 1 converts the planes to RGBA at source size (<see cref="SourceTexture"/>,
/// the clean picture for screenshots and recordings); pass 2 scales it into each swap chain and presents.
/// The device lives on the adapter that drives the window's monitor and is rebuilt on device loss or adapter change.
/// </summary>
public sealed class D3DRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly List<SwapChainTarget> _targets = [];
    private readonly bool _warp;
    private readonly SourceSurface _surface = new();
    private RenderDevice _gpu;
    private (int Size, float[] Rgba, Vector3 Min, Vector3 Max, float Intensity)? _lut;

    private D3DRenderer(bool warp, Luid? adapter)
    {
        _warp = warp;
        _gpu = new RenderDevice(warp, adapter);
    }

    /// <summary>A renderer on the GPU driving <paramref name="hwnd"/>'s monitor (or the default adapter if 0).</summary>
    public static D3DRenderer Create(nint hwnd) => new(false, RenderDevice.AdapterForWindow(hwnd));

    /// <summary>A software (WARP) renderer for tests and machines without a usable GPU.</summary>
    public static D3DRenderer CreateWarp() => new(true, null);

    public ID3D11Device Device => _gpu.Device;
    public Luid AdapterLuid => _gpu.AdapterLuid;
    public string AdapterName => _gpu.AdapterName;

    /// <summary>Scale mode, filter, range and matrix; read at the next render.</summary>
    public RenderOptions Options { get; set; } = new();

    /// <summary>
    /// Brightness, contrast, saturation, gamma (identity = 0, 1, 1, 1) and sharpness, as MetalRenderer's ImageAdjustments.
    /// Gamma is the user value (0.5…2); the shader gets 1/gamma.
    /// </summary>
    public (float Brightness, float Contrast, float Saturation, float Gamma, float Sharpness) Adjustments { get; set; } = (0, 1, 1, 1, 0);

    /// <summary>Size of the last converted frame (0 × 0 before the first).</summary>
    public (int Width, int Height) SourceSize => _surface.Size;

    /// <summary>Pass-1 output (RGBA8 at source size). Overlays never touch it (CLAUDE.md output rule).</summary>
    public ID3D11Texture2D? SourceTexture => _surface.Texture;

    /// <summary>Raised (on the render thread) after a lost device was rebuilt.</summary>
    public event Action? DeviceRecreated;

    /// <summary>
    /// Raised on the render thread, inside the render lock, after each frame was converted and presented:
    /// <see cref="SourceTexture"/> and the QPC time (100 ns, <see cref="Recording.MediaClock"/>) at which the frame
    /// reached the renderer. The texture is reused by the next frame, so handlers copy it (on this device's
    /// immediate context) before returning. Recorders hook in here; overlays never reach this texture.
    /// </summary>
    public event SourceRenderedHandler? SourceRendered;

    public SwapChainTarget CreateHwndTarget(nint hwnd, int width, int height)
    {
        var target = new SwapChainTarget(hwnd, null, width, height);
        lock (_gate)
        {
            _gpu.CreateSwapChain(target);
            _targets.Add(target);
        }
        return target;
    }

    /// <summary>
    /// A composition swap chain for a XAML <c>SwapChainPanel</c>; <paramref name="attach"/> sets it on the panel
    /// (marshal to the UI thread there). It is called again whenever the device is recreated.
    /// </summary>
    public SwapChainTarget CreateCompositionTarget(int width, int height, Action<IDXGISwapChain1> attach)
    {
        var target = new SwapChainTarget(0, attach, width, height);
        lock (_gate)
        {
            _gpu.CreateSwapChain(target);
            _targets.Add(target);
        }
        return target;
    }

    public void RemoveTarget(SwapChainTarget target)
    {
        lock (_gate)
        {
            _targets.Remove(target);
            target.Dispose();
        }
    }

    /// <summary>Converts <paramref name="frame"/> and presents it on every visible target.</summary>
    public void Render(in VideoFrame frame)
    {
        var arrived = Recording.MediaClock.Now();
        lock (_gate)
        {
            var trace = MediaEvents.Log.IsEnabled();
            var start = trace ? Stopwatch.GetTimestamp() : 0;
            _gpu.Convert(_surface, frame, Options, Adjustments);
            var converted = trace ? Stopwatch.GetTimestamp() : 0;
            PresentAll();
            if (trace)
            {
                MediaEvents.Log.FrameConverted(frame.Timestamp, Stopwatch.GetElapsedTime(start, converted).TotalMilliseconds);
                MediaEvents.Log.FramePresented(frame.Timestamp, Stopwatch.GetElapsedTime(converted).TotalMilliseconds);
            }
            if (_surface.Texture is { } source) SourceRendered?.Invoke(source, arrived);
        }
    }

    /// <summary>Re-presents the last converted frame (after a resize while the source is paused).</summary>
    public void Redraw()
    {
        lock (_gate)
        {
            if (_surface.View != null) PresentAll();
        }
    }

    /// <summary>Forgets the last frame and presents black (source lost), so a stale picture never lingers.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _surface.ReleaseTexture();
            PresentAll();
        }
    }

    /// <summary>Uploads a 3D LUT (size³ RGB triples, red fastest, as .cube files list them) applied at <paramref name="intensity"/>.</summary>
    public void SetLut(int size, ReadOnlySpan<float> rgb, Vector3 domainMin, Vector3 domainMax, float intensity)
    {
        lock (_gate)
        {
            var rgba = new float[size * size * size * 4];
            for (var i = 0; i < size * size * size; i++)
            {
                rgba[i * 4] = rgb[i * 3];
                rgba[i * 4 + 1] = rgb[i * 3 + 1];
                rgba[i * 4 + 2] = rgb[i * 3 + 2];
                rgba[i * 4 + 3] = 1;
            }
            _lut = (size, rgba, domainMin, domainMax, intensity);
            _gpu.SetLut(size, rgba, domainMin, domainMax, intensity);
        }
    }

    public void ClearLut()
    {
        lock (_gate)
        {
            _lut = null;
            _gpu.ClearLut();
        }
    }

    /// <summary>Copies <see cref="SourceTexture"/> to the CPU as tightly packed RGBA8 (screenshots, tests).</summary>
    public byte[]? ReadSourcePixels()
    {
        lock (_gate)
        {
            if (_surface.Texture is not { } source) return null;
            var (w, h) = SourceSize;
            var desc = source.Description with
            {
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            };
            var context = _gpu.Context;
            using var staging = _gpu.Device.CreateTexture2D(desc);
            context.CopyResource(staging, source);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[w * h * 4];
                for (var row = 0; row < h; row++)
                    Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, pixels, row * w * 4, w * 4);
                return pixels;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }

    /// <summary>Recreates the device when <paramref name="hwnd"/> now sits on a monitor driven by another adapter.</summary>
    public void CheckAdapter(nint hwnd)
    {
        if (_warp) return;
        var luid = RenderDevice.AdapterForWindow(hwnd);
        lock (_gate)
        {
            if (luid is { } l && !l.Equals(AdapterLuid)) RecreateDevice(l);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var t in _targets) t.Dispose();
            _targets.Clear();
            _surface.Release();
            _gpu.Dispose();
        }
    }

    private void RecreateDevice(Luid? adapter)
    {
        Trace.WriteLine($"D3DRenderer: recreating device (removed reason {_gpu.Device.DeviceRemovedReason})");
        foreach (var t in _targets) t.ReleaseDeviceObjects();
        _surface.Release();
        _gpu.Dispose();
        _gpu = new RenderDevice(_warp, adapter);
        if (_lut is { } lut) _gpu.SetLut(lut.Size, lut.Rgba, lut.Min, lut.Max, lut.Intensity);
        foreach (var t in _targets) _gpu.CreateSwapChain(t);
        DeviceRecreated?.Invoke();
    }

    private void PresentAll()
    {
        var options = Options;
        foreach (var target in _targets)
        {
            if (!target.IsVisible || target.SwapChain == null) continue;
            _gpu.PrepareTarget(target);
            _gpu.BeginTarget(target);
            _gpu.DrawScaled(_surface, new Rect(0, 0, target.Width, target.Height), options.Scale, options.Filter);
            if (!_gpu.Present(target))
            {
                RecreateDevice(_warp ? null : AdapterLuid);
                return;
            }
        }
    }
}
