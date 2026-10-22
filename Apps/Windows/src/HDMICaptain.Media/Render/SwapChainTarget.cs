using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HDMICaptain.Media.Render;

/// <summary>
/// One flip-model swap chain the renderer presents into: either composition (WinUI <c>SwapChainPanel</c>)
/// or a plain HWND. Both use FLIP_DISCARD, 2 buffers, a frame-latency waitable object with max latency 1,
/// and ALLOW_TEARING where supported (spec §13.4). The renderer owns all calls on it under its lock.
/// </summary>
public sealed class SwapChainTarget : IDisposable
{
    internal SwapChainTarget(nint hwnd, Action<IDXGISwapChain1>? attach, int width, int height)
    {
        Hwnd = hwnd;
        Attach = attach;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    /// <summary>The HWND for an HWND target; 0 for composition.</summary>
    public nint Hwnd { get; }

    /// <summary>Binds a new composition swap chain to its panel (called again after device recreation).</summary>
    internal Action<IDXGISwapChain1>? Attach { get; }

    public bool IsComposition => Hwnd == 0;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Hidden or minimized: skip presents, keep capturing (spec §13.5).</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Whether this swap chain was created with ALLOW_TEARING (Present uses the flag when windowed).</summary>
    public bool Tearing { get; internal set; }

    /// <summary>XAML composition scale; the swap chain gets the inverse transform so buffers map 1:1 to pixels.</summary>
    public float CompositionScale { get; private set; } = 1;

    internal IDXGISwapChain2? SwapChain { get; set; }
    internal ID3D11RenderTargetView? View { get; set; }
    internal nint Waitable { get; set; }

    private (int W, int H, float Scale)? _pendingResize;

    /// <summary>Requests a new buffer size (pixels); applied by the renderer before the next present.</summary>
    public void Resize(int width, int height, float compositionScale = 1)
    {
        lock (this) _pendingResize = (Math.Max(1, width), Math.Max(1, height), compositionScale);
    }

    internal bool TakePendingResize(out int width, out int height, out float scale)
    {
        lock (this)
        {
            if (_pendingResize is not { } r)
            {
                (width, height, scale) = (Width, Height, CompositionScale);
                return false;
            }
            _pendingResize = null;
            (width, height, scale) = r;
            Width = width;
            Height = height;
            CompositionScale = scale;
            return true;
        }
    }

    internal void ReleaseDeviceObjects()
    {
        View?.Dispose();
        View = null;
        SwapChain?.Dispose();
        SwapChain = null;
        // GetFrameLatencyWaitableObject hands us a handle we must close.
        if (Waitable != 0) Windows.Win32.PInvoke.CloseHandle((Windows.Win32.Foundation.HANDLE)Waitable);
        Waitable = 0;
    }

    public void Dispose() => ReleaseDeviceObjects();
}
