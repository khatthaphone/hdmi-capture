using HDMICaptain.Core;

namespace HDMICaptain.Media.Capture;

/// <summary>A plugged-in capture card: the video source, its stable key and the audio endpoint of the same USB device.</summary>
public sealed record CaptureDevice(string Key, string Name, string SymbolicLink, UsbDetails? Usb, AudioDevice? Audio);

/// <summary>Enumerates present capture cards. Called on the control thread; may be slow.</summary>
public interface IDeviceList
{
    IReadOnlyList<CaptureDevice> Enumerate();
}

/// <summary>Tells the catalog that the set of cameras probably changed (hot-plug). May fire on any thread.</summary>
public interface IDeviceChangeSource
{
    event Action? Changed;
}

/// <summary>The real list: <see cref="MfDevices"/> video sources paired with audio endpoints by PnP container.</summary>
public sealed class MfDeviceList : IDeviceList
{
    public IReadOnlyList<CaptureDevice> Enumerate()
    {
        var video = MfDevices.Video();
        var audio = MfDevices.Audio();
        return DeviceCatalog.Build(video, audio);
    }
}

/// <summary>Adapts <see cref="DeviceWatcher"/> (CM_Register_Notification) to <see cref="IDeviceChangeSource"/>.</summary>
public sealed class WatcherChangeSource : IDeviceChangeSource, IDisposable
{
    private readonly DeviceWatcher _watcher = new();

    public WatcherChangeSource()
    {
        _watcher.Arrived += _ => Changed?.Invoke();
        _watcher.Removed += _ => Changed?.Invoke();
    }

    public event Action? Changed;

    public void Dispose() => _watcher.Dispose();
}

/// <summary>
/// The list of plugged capture cards, refreshed on hot-plug events (debounced: the interface shows up in the notification
/// before MFEnumDeviceSources lists it) and remembering which card the user prefers (spec §13.2). Refreshes run on the
/// scheduler thread; <see cref="Devices"/> and <see cref="PreferredKey"/> are safe to read from anywhere.
/// </summary>
public sealed class DeviceCatalog : IDisposable
{
    private readonly IDeviceList _list;
    private readonly IScheduler _scheduler;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private IDisposable? _pending;
    private IReadOnlyList<CaptureDevice> _devices = [];
    private string? _preferred;

    public DeviceCatalog(IDeviceList list, IScheduler scheduler, IDeviceChangeSource? changes = null,
        string? preferredKey = null, TimeSpan? debounce = null)
    {
        _list = list;
        _scheduler = scheduler;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _preferred = preferredKey;
        Changes = changes;
        if (changes != null) changes.Changed += OnChange;
    }

    private IDeviceChangeSource? Changes { get; }

    /// <summary>The snapshot after a refresh that changed something. On the scheduler thread.</summary>
    public event Action<IReadOnlyList<CaptureDevice>>? DevicesChanged;

    /// <summary>The remembered card changed (persist it). On whichever thread called <see cref="PreferredKey"/>'s setter.</summary>
    public event Action<string?>? PreferredChanged;

    public IReadOnlyList<CaptureDevice> Devices
    {
        get { lock (_gate) return _devices; }
    }

    /// <summary>Key of the card the user chose last; the controller switches back to it whenever it is plugged in.</summary>
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

    /// <summary>Re-enumerates soon (any thread).</summary>
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
        _pending?.Dispose(); // a burst of interface events becomes one refresh
        _pending = _scheduler.Schedule(_debounce, () => RefreshNow());
    });

    /// <summary>
    /// Pairs video and audio sources by container and gives each card its stable key. Cards that would share a key
    /// (two identical cards without serials) get "#2", "#3" in symbolic-link order so they stay distinguishable.
    /// </summary>
    public static IReadOnlyList<CaptureDevice> Build(IEnumerable<VideoDevice> video, IEnumerable<AudioDevice> audio)
    {
        var audioList = audio.ToList();
        var seen = new Dictionary<string, int>();
        var result = new List<CaptureDevice>();
        foreach (var v in video.OrderBy(v => v.SymbolicLink, StringComparer.OrdinalIgnoreCase))
        {
            var key = KeyOf(v);
            seen[key] = seen.GetValueOrDefault(key) + 1;
            if (seen[key] > 1) key += $"#{seen[key]}";
            var container = v.Usb?.ContainerId;
            var paired = container is { } c ? audioList.FirstOrDefault(a => a.ContainerId == c) : null;
            result.Add(new CaptureDevice(key, v.Name, v.SymbolicLink, v.Usb, paired));
        }
        return result;
    }

    /// <summary>VID:PID + serial (or product name) when USB details are known, else the friendly name.</summary>
    public static string KeyOf(VideoDevice v) =>
        v.Usb is { VendorId: { } vid, ProductId: { } pid }
            ? DeviceKey.Of(vid, pid, v.Usb.Serial, v.Name)
            : $"name:{v.Name}";

    public void Dispose()
    {
        if (Changes != null) Changes.Changed -= OnChange;
        _pending?.Dispose();
    }
}
