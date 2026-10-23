using HDMICaptain.Linux.Media.Audio;

namespace HDMICaptain.Linux.Media.Capture;

/// <summary>A plugged-in capture card: its V4L2 node, stable key and the ALSA card on the same USB device.</summary>
public sealed record CaptureDevice(string Key, string Name, string Path, UsbIdentity? Usb, AlsaCard? Audio, V4l2Device? Node)
{
    // The node's format list is a reference type; compare cards by what identifies them.
    public bool Equals(CaptureDevice? other) =>
        other is not null && Key == other.Key && Name == other.Name && Path == other.Path && Audio?.HwName == other.Audio?.HwName;

    public override int GetHashCode() => HashCode.Combine(Key, Path);
}

/// <summary>Enumerates present capture cards. Called on the control thread; may be slow.</summary>
public interface IDeviceList
{
    IReadOnlyList<CaptureDevice> Enumerate();
}

/// <summary>Tells the catalog that the set of cards probably changed (hot-plug). May fire on any thread.</summary>
public interface IDeviceChangeSource
{
    event Action? Changed;
}

/// <summary>The real list: V4L2 capture nodes paired with ALSA cards by USB device.</summary>
public sealed class V4l2DeviceList(Action<string>? log = null) : IDeviceList
{
    public IReadOnlyList<CaptureDevice> Enumerate() => DeviceCatalog.Build(V4l2Devices.List(log), AlsaCards.List());
}

/// <summary>
/// Hot-plug through inotify on /dev (FileSystemWatcher): video and sound nodes appearing or going away. Works inside
/// Flatpak too, where the libudev netlink monitor does not (linux.md §2b).
/// </summary>
public sealed class DevChangeSource : IDeviceChangeSource, IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];

    public DevChangeSource()
    {
        Watch("/dev", "video*");
        Watch("/dev/snd", "pcmC*");
    }

    public event Action? Changed;

    private void Watch(string dir, string filter)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir, filter) { NotifyFilter = NotifyFilters.FileName };
            w.Created += (_, _) => Changed?.Invoke();
            w.Deleted += (_, _) => Changed?.Invoke();
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        catch (IOException)
        {
            // No inotify (limits reached): the controller still recovers through its own retries.
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}

/// <summary>
/// The list of plugged capture cards, refreshed on hot-plug events (debounced: the node appears before its formats can
/// be read) and remembering which card the user prefers (spec §14.2). Port of the Windows catalog.
/// </summary>
public sealed class DeviceCatalog : IDisposable
{
    private readonly IDeviceList _list;
    private readonly IScheduler _scheduler;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly IDeviceChangeSource? _changes;
    private IDisposable? _pending;
    private IReadOnlyList<CaptureDevice> _devices = [];
    private string? _preferred;

    public DeviceCatalog(IDeviceList list, IScheduler scheduler, IDeviceChangeSource? changes = null,
        string? preferredKey = null, TimeSpan? debounce = null)
    {
        _list = list;
        _scheduler = scheduler;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _preferred = preferredKey;
        _changes = changes;
        if (changes != null) changes.Changed += OnChange;
    }

    /// <summary>The snapshot after a refresh that changed something. On the scheduler thread.</summary>
    public event Action<IReadOnlyList<CaptureDevice>>? DevicesChanged;

    public event Action<string?>? PreferredChanged;

    public IReadOnlyList<CaptureDevice> Devices
    {
        get { lock (_gate) return _devices; }
    }

    public string? PreferredKey
    {
        get { lock (_gate) return _preferred; }
        set
        {
            lock (_gate)
            {
                if (_preferred == value) return;
                _preferred = value;
            }
            PreferredChanged?.Invoke(value);
        }
    }

    public CaptureDevice? Find(string key) => Devices.FirstOrDefault(d => d.Key == key);

    public void Refresh() => _scheduler.Post(() => RefreshNow());

    /// <summary>Re-enumerates now. Scheduler thread only. Returns true when the list changed.</summary>
    public bool RefreshNow()
    {
        IReadOnlyList<CaptureDevice> fresh;
        try
        {
            fresh = _list.Enumerate();
        }
        catch (Exception)
        {
            return false; // keep the last good list; the next hot-plug or retry tries again
        }
        lock (_gate)
        {
            if (_devices.SequenceEqual(fresh)) return false;
            _devices = fresh;
        }
        DevicesChanged?.Invoke(fresh);
        return true;
    }

    private void OnChange() => _scheduler.Post(() =>
    {
        _pending?.Dispose(); // a burst of node events becomes one refresh
        _pending = _scheduler.Schedule(_debounce, () => RefreshNow());
    });

    /// <summary>
    /// One card per USB device (cards like the MS2130 expose two capture nodes; the first with formats wins), keyed by
    /// VID:PID + serial/name; identical cards without serials get "#2", "#3" in USB-path order.
    /// </summary>
    public static IReadOnlyList<CaptureDevice> Build(IEnumerable<V4l2Device> nodes, IReadOnlyList<AlsaCard> cards)
    {
        var seen = new Dictionary<string, int>();
        var usbSeen = new HashSet<string>();
        var result = new List<CaptureDevice>();
        foreach (var node in nodes.Where(n => n.Formats.Count > 0).OrderBy(n => n.Usb?.SysPath ?? n.Path, StringComparer.Ordinal))
        {
            if (node.Usb is { } usb && !usbSeen.Add(usb.SysPath)) continue;
            var key = node.Usb?.Key ?? $"v4l2:{node.BusInfo}";
            seen[key] = seen.GetValueOrDefault(key) + 1;
            if (seen[key] > 1) key += $"#{seen[key]}";
            var audio = node.Usb is { } u ? AlsaCards.PairedWith(u, cards) : null;
            result.Add(new CaptureDevice(key, node.Usb?.Product ?? node.Name, node.Path, node.Usb, audio, node));
        }
        return result;
    }

    public void Dispose()
    {
        if (_changes != null) _changes.Changed -= OnChange;
        _pending?.Dispose();
    }
}
