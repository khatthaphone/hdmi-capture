using System.Runtime.InteropServices;
using HDMICaptain.Media.Render;
using Microsoft.UI.Xaml.Controls;
using Vortice.DXGI;

namespace HDMICaptain.App.Video;

/// <summary>
/// Puts a renderer's composition swap chain into a XAML <see cref="SwapChainPanel"/> and keeps its size
/// and composition scale in sync. The default video surface (spec §13.4).
/// </summary>
public sealed class SwapChainPanelHost : IDisposable
{
    private readonly SwapChainPanel _panel;
    private readonly Action _redraw;
    private readonly Action<SwapChainTarget> _remove;
    private readonly SwapChainTarget _target;

    public SwapChainPanelHost(SwapChainPanel panel, D3DRenderer renderer)
        : this(panel, renderer.CreateCompositionTarget, renderer.Redraw, renderer.RemoveTarget)
    {
    }

    /// <summary>The Multi-View grid's single swap chain; the grid renderer disposes it.</summary>
    public SwapChainPanelHost(SwapChainPanel panel, GridRenderer grid)
        : this(panel, grid.CreateCompositionTarget, grid.Redraw, _ => { })
    {
    }

    private SwapChainPanelHost(SwapChainPanel panel, Func<int, int, Action<IDXGISwapChain1>, SwapChainTarget> create,
        Action redraw, Action<SwapChainTarget> remove)
    {
        _panel = panel;
        _redraw = redraw;
        _remove = remove;
        var (w, h) = PixelSize();
        _target = create(w, h, Attach);
        panel.SizeChanged += (_, _) => UpdateSize();
        panel.CompositionScaleChanged += (_, _) => UpdateSize();
    }

    public SwapChainTarget Target => _target;

    private (int W, int H) PixelSize() =>
        ((int)Math.Ceiling(_panel.ActualWidth * _panel.CompositionScaleX), (int)Math.Ceiling(_panel.ActualHeight * _panel.CompositionScaleY));

    private void UpdateSize()
    {
        var (w, h) = PixelSize();
        _target.Resize(w, h, _panel.CompositionScaleX);
        _redraw();
    }

    // Runs on the render thread at creation and after device loss; SetSwapChain must run on the UI thread.
    private void Attach(IDXGISwapChain1 swapChain)
    {
        var pointer = swapChain.NativePointer;
        Marshal.AddRef(pointer);
        void Set()
        {
            SetSwapChain(_panel, pointer);
            Marshal.Release(pointer);
        }
        if (_panel.DispatcherQueue.HasThreadAccess) Set();
        else _panel.DispatcherQueue.TryEnqueue(Set);
    }

    /// <summary>ISwapChainPanelNative::SetSwapChain (microsoft.ui.xaml.media.dxinterop.h), called through the vtable.</summary>
    private static unsafe void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        var iid = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            var vtable = *(nint**)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    public void Dispose() => _remove(_target);
}
