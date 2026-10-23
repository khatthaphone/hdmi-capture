using HDMICaptain.Linux.Media;
using HDMICaptain.Linux.Media.Capture;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Sleep/resume from logind (org.freedesktop.login1.Manager.PrepareForSleep on the system bus) and keep-awake through
/// GTK's inhibit (the Inhibit portal inside Flatpak). Without a system bus (some containers) sleep events never
/// arrive and the controller's own recovery handles the card coming back.
/// </summary>
public sealed class LinuxPower : IPowerPort, IDisposable
{
    private readonly Adw.Application _app;
    private readonly Func<Gtk.Window?> _window;
    private readonly SynchronizationContext _ui;
    private Gio.DBusConnection? _system;
    private uint _subscription;

    public LinuxPower(Adw.Application app, Func<Gtk.Window?> window)
    {
        _app = app;
        _window = window;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("LinuxPower needs the GTK synchronization context");
        try
        {
            _system = Gio.Functions.BusGetSync(Gio.BusType.System, null);
            _subscription = _system.SignalSubscribe("org.freedesktop.login1", "org.freedesktop.login1.Manager", "PrepareForSleep",
                "/org/freedesktop/login1", null, Gio.DBusSignalFlags.None, OnSignal);
        }
        catch (Exception e)
        {
            LinuxLog.Write($"power: no logind ({e.Message}); sleep/resume comes through recovery only");
            _system = null;
        }
    }

    public event Action? Suspending;
    public event Action? Resumed;

    private void OnSignal(Gio.DBusConnection connection, string? sender, string objectPath, string interfaceName, string signalName, GLib.Variant parameters)
    {
        var sleeping = parameters.GetChildValue(0).GetBoolean();
        LinuxLog.Write($"power: PrepareForSleep({sleeping})");
        if (sleeping) Suspending?.Invoke();
        else Resumed?.Invoke();
    }

    /// <summary>Idle inhibit while live (screen saver and auto-suspend on idle). Any thread: hops to the UI thread.</summary>
    public IDisposable RequestDisplayOn()
    {
        var cookie = new Cookie(this);
        _ui.Post(_ => cookie.Acquire(), null);
        return cookie;
    }

    public void Dispose()
    {
        if (_system != null && _subscription != 0) _system.SignalUnsubscribe(_subscription);
        _subscription = 0;
        _system = null;
    }

    private sealed class Cookie(LinuxPower owner) : IDisposable
    {
        private uint _id;
        private bool _released;

        public void Acquire()
        {
            if (_released || _id != 0) return;
            _id = owner._app.Inhibit(owner._window(), Gtk.ApplicationInhibitFlags.Idle, "Showing the capture card");
            LinuxLog.Write($"power: idle inhibit {(_id != 0 ? "on" : "refused")}");
        }

        public void Dispose() => owner._ui.Post(_ =>
        {
            _released = true;
            if (_id != 0) owner._app.Uninhibit(_id);
            _id = 0;
        }, null);
    }
}
