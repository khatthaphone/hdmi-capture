namespace HDMICaptain.App.Windowing;

/// <summary>
/// Keeps the client area at the video's aspect while the user drags a window edge (WM_SIZING), like the Mac's
/// contentAspectRatio. The non-client part (resize borders) is measured each time, so DPI changes are fine.
/// </summary>
internal sealed class AspectLock : IDisposable
{
    // WMSZ_* edges; left, right and the bottom corners move the bottom edge.
    private const int WmszTop = 3, WmszTopLeft = 4, WmszTopRight = 5, WmszBottom = 6;

    private readonly nint _hwnd;
    private readonly NativeWindow.SubclassProc _proc; // rooted while subclassed

    public AspectLock(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        NativeWindow.SetWindowSubclass(hwnd, _proc, 1, 0);
    }

    /// <summary>Width ÷ height to hold, or null to resize freely.</summary>
    public double? Aspect { get; set; }

    private unsafe nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (msg == NativeWindow.WM_SIZING && Aspect is { } aspect && aspect > 0
            && NativeWindow.GetWindowRect(hwnd, out var window) && NativeWindow.GetClientRect(hwnd, out var client))
        {
            var rect = (NativeWindow.Rect*)lParam;
            var ncW = window.Width - client.Width;
            var ncH = window.Height - client.Height;
            var edge = (int)wParam;
            if (edge is WmszTop or WmszBottom)
            {
                var width = (int)Math.Round((rect->Height - ncH) * aspect) + ncW;
                rect->Right = rect->Left + width;
            }
            else
            {
                var height = (int)Math.Round((rect->Width - ncW) / aspect) + ncH;
                if (edge is WmszTopLeft or WmszTopRight) rect->Top = rect->Bottom - height;
                else rect->Bottom = rect->Top + height;
            }
            return 1;
        }
        return NativeWindow.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose() => NativeWindow.RemoveWindowSubclass(_hwnd, _proc, 1);
}
