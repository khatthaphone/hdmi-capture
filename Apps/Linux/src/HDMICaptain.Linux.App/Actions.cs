using HDMICaptain.Core;
using HDMICaptain.Linux.Media;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Window actions (win.*), their keyboard shortcuts (spec §14.7, the Windows set with Ctrl) and the menus that use
/// them: the control bar's scale and main menus and the right-click menu share one model.
/// </summary>
public static class Actions
{
    public static void Install(MainWindow w, Adw.ApplicationWindow window, Adw.Application app)
    {
        void Add(string name, Action run, params string[] accels)
        {
            var action = Gio.SimpleAction.New(name, null);
            action.OnActivate += (_, _) => run();
            window.AddAction(action);
            if (accels.Length > 0) app.SetAccelsForAction($"win.{name}", accels);
        }

        Add("fullscreen", w.ToggleFullScreen, "F11", "<Alt>Return", "f");
        Add("leave-fullscreen", w.ExitFullScreen, "Escape");
        Add("mute", w.ToggleMute, "<Control>m");
        Add("scale-fit", () => w.SetScale(ScaleMode.Fit), "<Control>1");
        Add("scale-fill", () => w.SetScale(ScaleMode.Fill), "<Control>2");
        Add("scale-stretch", () => w.SetScale(ScaleMode.Stretch), "<Control>3");
        Add("scale-integer", () => w.SetScale(ScaleMode.Integer), "<Control>4");
        Add("sharp-pixels", w.ToggleSharpPixels);
        Add("screenshot", w.SaveScreenshot, "<Control><Shift>s");
        Add("copy-frame", w.CopyFrame, "<Control><Shift>c");
        Add("clean", w.ToggleClean, "<Control><Shift>h");
        Add("diagnostics", w.ToggleDiagnostics, "<Control>i");
        Add("size-50", () => w.SizeTo(0.5), "<Control>minus");
        Add("size-100", () => w.SizeTo(1), "<Control>0");
        Add("size-200", () => w.SizeTo(2), "<Control>plus", "<Control>equal");
        Add("reconnect", w.Reconnect, "<Control>r");
        Add("help", () => ConnectionHelp.Show(window), "F1");
        Add("shortcuts", () => ShortcutsWindow(window).Present(), "<Control>question");
    }

    public static Gio.Menu ScaleMenu()
    {
        var menu = Gio.Menu.New();
        menu.Append(Strings.ScaleFit, "win.scale-fit");
        menu.Append(Strings.ScaleFill, "win.scale-fill");
        menu.Append(Strings.ScaleStretch, "win.scale-stretch");
        menu.Append(Strings.ScaleInteger, "win.scale-integer");
        var sharp = Gio.Menu.New();
        sharp.Append("Sharp Pixels", "win.sharp-pixels");
        menu.AppendSection(null, sharp);
        return menu;
    }

    public static Gio.Menu MainMenu()
    {
        var menu = Gio.Menu.New();
        var view = Gio.Menu.New();
        view.Append("Full Screen", "win.fullscreen");
        view.Append("Clean Mode", "win.clean");
        view.Append("Show Diagnostics", "win.diagnostics");
        menu.AppendSection(null, view);
        var size = Gio.Menu.New();
        size.Append("Half Size (50 %)", "win.size-50");
        size.Append("Actual Pixels (100 %)", "win.size-100");
        size.Append("Double Size (200 %)", "win.size-200");
        menu.AppendSubmenu("Window Size", size);
        menu.AppendSubmenu("Scale", ScaleMenu());
        var output = Gio.Menu.New();
        output.Append("Save Screenshot", "win.screenshot");
        output.Append("Copy Frame", "win.copy-frame");
        menu.AppendSection(null, output);
        var help = Gio.Menu.New();
        help.Append("Reconnect", "win.reconnect");
        help.Append("Connection Help", "win.help");
        help.Append("Keyboard Shortcuts", "win.shortcuts");
        menu.AppendSection(null, help);
        return menu;
    }

    public static void ShowContextMenu(MainWindow w, Gtk.Widget parent, double x, double y)
    {
        var popover = Gtk.PopoverMenu.NewFromModel(MainMenu());
        popover.SetParent(parent);
        popover.SetHasArrow(false);
        popover.SetHalign(Gtk.Align.Start);
        var rect = new Gdk.Rectangle { X = (int)x, Y = (int)y, Width = 1, Height = 1 };
        popover.SetPointingTo(rect);
        popover.OnClosed += (_, _) => GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            popover.Unparent();
            return false;
        });
        popover.Popup();
        w.ShowControls();
    }

    private static Gtk.Window ShortcutsWindow(Gtk.Window parent)
    {
        var rows = new (string Keys, string What)[]
        {
            ("F11  ·  F  ·  Alt+Enter", "Full screen"), ("Esc", "Leave full screen"), ("Ctrl+M", "Mute"),
            ("Ctrl+1 / 2 / 3 / 4", "Fit / Fill / Stretch / Integer"), ("Ctrl+Shift+S", "Save screenshot"),
            ("Ctrl+Shift+C", "Copy frame"), ("Ctrl+Shift+H", "Clean mode"), ("Ctrl+I", "Diagnostics"),
            ("Ctrl+− / 0 / +", "Window size 50 / 100 / 200 %"), ("Ctrl+R", "Reconnect"), ("F1", "Connection help"),
        };
        var list = Gtk.ListBox.New();
        list.AddCssClass("boxed-list");
        list.SetSelectionMode(Gtk.SelectionMode.None);
        foreach (var (keys, what) in rows)
        {
            var row = Adw.ActionRow.New();
            row.SetTitle(what);
            var label = Gtk.Label.New(keys);
            label.AddCssClass("dim-label");
            row.AddSuffix(label);
            list.Append(row);
        }
        var clamp = Adw.Clamp.New();
        clamp.SetChild(list);
        clamp.SetMarginTop(12);
        clamp.SetMarginBottom(12);
        clamp.SetMarginStart(12);
        clamp.SetMarginEnd(12);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(Adw.HeaderBar.New());
        toolbar.SetContent(clamp);
        var window = Adw.Window.New();
        window.SetTitle("Keyboard Shortcuts");
        window.SetTransientFor(parent);
        window.SetModal(true);
        window.SetDefaultSize(460, 560);
        window.SetContent(toolbar);
        return window;
    }

    /// <summary>DEBUG <c>-qaAction</c>: one command 1.5 s after launch (screenshots and checks without a person).</summary>
    public static void RunQa(MainWindow w, string action)
    {
        LinuxLog.Write($"qa: {action}");
        switch (action)
        {
            case "screenshot": w.SaveScreenshot(); break;
            case "copy": w.CopyFrame(); break;
            case "size50": w.SizeTo(0.5); break;
            case "actualPixels": w.SizeTo(1); break;
            case "fullScreen": w.ToggleFullScreen(); break;
            case "clean": w.ToggleClean(); break;
            case "help": ConnectionHelp.Show(w.Window); break;
            default: LinuxLog.Write($"qa: unknown action {action}"); break;
        }
    }
}
