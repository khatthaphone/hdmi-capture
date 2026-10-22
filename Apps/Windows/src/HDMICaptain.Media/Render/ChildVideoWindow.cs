using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace HDMICaptain.Media.Render;

/// <summary>
/// A plain child HWND over the parent's client area that hosts an HWND swap chain: the fallback video surface
/// if <c>SwapChainPanel</c> never reaches independent flip (spec §13.4, task 092 compares the two).
/// XAML can't draw over it (airspace), so overlays need their own popup when this surface is used.
/// </summary>
public sealed class ChildVideoWindow : IDisposable
{
    private const string ClassName = "HDMICaptainVideo";
    private static readonly WNDPROC Proc = WndProc; // rooted: Windows calls it for the process lifetime
    private static bool _registered;

    private static readonly HWND HwndTop = default; // HWND_TOP

    private HWND _hwnd;

    public unsafe ChildVideoWindow(nint parent, int width, int height)
    {
        var module = PInvoke.GetModuleHandle((string?)null);
        if (!_registered)
        {
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Proc,
                    hInstance = (HINSTANCE)module.DangerousGetHandle(),
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(wc) == 0) throw new InvalidOperationException("RegisterClassEx failed");
            }
            _registered = true;
        }
        _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP, ClassName, "Video",
            WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_CLIPSIBLINGS,
            0, 0, width, height, (HWND)parent, null, module, null);
        if (_hwnd.IsNull) throw new InvalidOperationException("CreateWindowEx failed");
        PInvoke.SetWindowPos(_hwnd, HwndTop, 0, 0, width, height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    public nint Handle => _hwnd;

    /// <summary>Covers the parent's client area (pixels) and stays above the XAML island.</summary>
    public void SetBounds(int x, int y, int width, int height) =>
        PInvoke.SetWindowPos(_hwnd, HwndTop, x, y, Math.Max(1, width), Math.Max(1, height), SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        const uint WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        // Let clicks and double-clicks fall through to the window underneath (full screen toggle, context menu).
        if (msg == WM_NCHITTEST) return (LRESULT)HTTRANSPARENT;
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd.IsNull) return;
        PInvoke.DestroyWindow(_hwnd);
        _hwnd = default;
    }
}
