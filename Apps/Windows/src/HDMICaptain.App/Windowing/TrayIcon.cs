using System.Runtime.InteropServices;

namespace HDMICaptain.App.Windowing;

/// <summary>
/// Notification-area icon (spec §13.7: stands in for the Mac menu-bar extra). It rides on the main window's HWND
/// through a subclass, so it keeps working while the window is hidden, and it re-adds itself when Explorer
/// restarts ("TaskbarCreated"). The menu is a plain Win32 popup: it has to work with no XAML window shown.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 41; // WM_APP + 41
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifShowTip = 0x80;
    private const uint WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint MfString = 0x0, MfSeparator = 0x800, MfChecked = 0x8, MfGrayed = 0x1;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2, TpmBottomAlign = 0x20;

    private readonly nint _hwnd;
    private readonly nint _icon;
    private readonly uint _taskbarCreated;
    private readonly NativeWindow.SubclassProc _proc; // rooted while subclassed
    private readonly Func<IReadOnlyList<MenuEntry>> _menu;
    private readonly Action _activate;

    /// <summary>One popup entry; <see cref="Action"/> null makes a separator.</summary>
    public sealed record MenuEntry(string Text, Action? Action, bool Checked = false, bool Enabled = true)
    {
        public static readonly MenuEntry Separator = new("", null);
    }

    /// <param name="menu">Built each time the menu opens, so checks and names are current.</param>
    /// <param name="activate">Left click: show and focus the window.</param>
    public TrayIcon(nint hwnd, string iconPath, string tip, Func<IReadOnlyList<MenuEntry>> menu, Action activate)
    {
        _hwnd = hwnd;
        _menu = menu;
        _activate = activate;
        _icon = LoadImage(0, iconPath, 1 /* IMAGE_ICON */, 0, 0, 0x10 /* LR_LOADFROMFILE */ | 0x40 /* LR_DEFAULTSIZE */);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        Tip = tip;
        _proc = WndProc;
        NativeWindow.SetWindowSubclass(hwnd, _proc, 2, 0);
        Add();
    }

    public string Tip { get; private set; }

    public unsafe void SetTip(string tip)
    {
        Tip = tip;
        var data = Data(NifTip | NifShowTip);
        Shell_NotifyIcon(NimModify, &data);
    }

    private unsafe void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        Shell_NotifyIcon(NimAdd, &data);
        data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4: NIN_SELECT and WM_CONTEXTMENU with cursor coordinates
        Shell_NotifyIcon(NimSetVersion, &data);
    }

    private unsafe NotifyIconData Data(uint flags)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)sizeof(NotifyIconData),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = Tip.AsSpan(0, Math.Min(Tip.Length, 127));
        tip.CopyTo(new Span<char>(data.szTip, 128));
        return data;
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (msg == CallbackMessage)
        {
            var evt = (uint)(lParam & 0xFFFF);
            if (evt is WmLButtonUp or NinSelect) _activate();
            else if (evt is WmRButtonUp or WmContextMenu) ShowMenu();
            return 0;
        }
        if (msg == _taskbarCreated) Add();
        return NativeWindow.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var entries = _menu();
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Action is null) AppendMenu(menu, MfSeparator, 0, null);
                else AppendMenu(menu, MfString | (e.Checked ? MfChecked : 0) | (e.Enabled ? 0 : MfGrayed), (nuint)(i + 1), e.Text);
            }
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd); // or the menu won't close when clicking elsewhere
            var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton | TpmBottomAlign, point.X, point.Y, _hwnd, 0);
            if (chosen > 0) entries[chosen - 1].Action?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public unsafe void Dispose()
    {
        var data = Data(0);
        Shell_NotifyIcon(NimDelete, &data);
        NativeWindow.RemoveWindowSubclass(_hwnd, _proc, 2);
        if (_icon != 0) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Shell_NotifyIcon(uint message, NotifyIconData* data);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
