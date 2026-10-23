using System.Runtime.InteropServices;

namespace HDMICaptain.Linux.App;

/// <summary>
/// "Keep window on top" under X11 (spec §14.7): GTK 4 dropped gtk_window_set_keep_above, and Wayland has no protocol
/// for it, so on X11 this asks the window manager directly with a _NET_WM_STATE client message (EWMH).
/// </summary>
internal static unsafe partial class X11Window
{
    private const int ClientMessage = 33;
    private const long SubstructureRedirectMask = 1L << 20, SubstructureNotifyMask = 1L << 19;

    /// <summary>GTK runs on its X11 backend (not Wayland/XWayland detection: what GDK actually opened).</summary>
    public static bool IsAvailable =>
        Gdk.Display.GetDefault() is { } display && Marshal.PtrToStringUTF8(g_type_name_from_instance(display.Handle.DangerousGetHandle())) == "GdkX11Display";

    public static bool SetAbove(Gtk.Window window, bool above)
    {
        if (!IsAvailable || window.GetSurface() is not { } surface) return false;
        try
        {
            var gdkDisplay = Gdk.Display.GetDefault()!.Handle.DangerousGetHandle();
            var xdisplay = gdk_x11_display_get_xdisplay(gdkDisplay);
            var xid = gdk_x11_surface_get_xid(surface.Handle.DangerousGetHandle());
            var root = XDefaultRootWindow(xdisplay);
            var ev = new XClientMessageEvent
            {
                Type = ClientMessage,
                Display = xdisplay,
                Window = xid,
                MessageType = XInternAtom(xdisplay, "_NET_WM_STATE", 0),
                Format = 32,
            };
            ev.Data[0] = above ? 1 : 0; // _NET_WM_STATE_ADD / _REMOVE
            ev.Data[1] = (long)XInternAtom(xdisplay, "_NET_WM_STATE_ABOVE", 0);
            ev.Data[3] = 1; // source: application
            var raw = stackalloc byte[192]; // sizeof(XEvent)
            new Span<byte>(raw, 192).Clear();
            *(XClientMessageEvent*)raw = ev;
            XSendEvent(xdisplay, root, 0, SubstructureRedirectMask | SubstructureNotifyMask, raw);
            XFlush(xdisplay);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public nuint Window;
        public nuint MessageType;
        public int Format;
        public fixed long Data[5];
    }

    [LibraryImport("libgobject-2.0.so.0")] private static partial nint g_type_name_from_instance(nint instance);
    [LibraryImport("libgtk-4.so.1")] private static partial nint gdk_x11_display_get_xdisplay(nint display);
    [LibraryImport("libgtk-4.so.1")] private static partial nuint gdk_x11_surface_get_xid(nint surface);
    [LibraryImport("libX11.so.6")] private static partial nuint XDefaultRootWindow(nint display);
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)] private static partial nuint XInternAtom(nint display, string name, int onlyIfExists);
    [LibraryImport("libX11.so.6")] private static partial int XSendEvent(nint display, nuint window, int propagate, long mask, void* ev);
    [LibraryImport("libX11.so.6")] private static partial int XFlush(nint display);
}
