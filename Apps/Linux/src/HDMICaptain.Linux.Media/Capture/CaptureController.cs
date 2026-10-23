using HDMICaptain.Core;

namespace HDMICaptain.Linux.Media.Capture;

/// <summary>One open card stream: <see cref="V4l2Session"/> is the real one, tests use fakes.</summary>
public interface ICaptureSession : IDisposable
{
    /// <summary>Opens and starts streaming: 0, or -errno (or <see cref="CaptureErrorMap.NoUsableFormat"/>).</summary>
    int Start();

    /// <summary>The stream ended on its own: -errno. Raised once, on the capture thread.</summary>
    event Action<int>? Failed;

    /// <summary>Each frame, on the capture thread, while the buffer is still the driver's.</summary>
    event V4l2Source.FrameHandler? FrameArrived;

    V4l2Stream? Stream { get; }
    V4l2Counters Counters { get; }
}

public interface ICaptureSourceFactory
{
    ICaptureSession Create(CaptureDevice device, FormatPreference? preference);
}

/// <summary>Picks the format with <see cref="FormatSelector"/> (USB link rule from sysfs) and streams it with <see cref="V4l2Source"/>.</summary>
public sealed unsafe class V4l2Session : ICaptureSession
{
    private readonly CaptureDevice _device;
    private readonly FormatPreference? _preference;
    private readonly V4l2Source _source;

    public V4l2Session(CaptureDevice device, FormatPreference? preference)
    {
        _device = device;
        _preference = preference;
        _source = new V4l2Source(device.Path, (data, length, stream, ts) => FrameArrived?.Invoke(data, length, stream, ts));
        _source.Stopped += e => Failed?.Invoke(e);
    }

    public event Action<int>? Failed;
    public event V4l2Source.FrameHandler? FrameArrived;

    public V4l2Stream? Stream => _source.Stream;
    public V4l2Counters Counters => _source.Counters;

    public int Start()
    {
        var error = 0;
        var node = _device.Node ?? V4l2Devices.Probe(_device.Path, out error);
        if (node == null) return error != 0 ? error : -Errno.ENODEV;
        var choice = FormatSelector.Select(node.Descriptors, _preference, node.Usb?.Link);
        if (choice == null) return CaptureErrorMap.NoUsableFormat;
        var format = node.Formats[choice.Index];
        return _source.Start(format, choice.FrameRate);
    }

    public void Dispose() => _source.Dispose();
}

public sealed class V4l2SessionFactory : ICaptureSourceFactory
{
    public ICaptureSession Create(CaptureDevice device, FormatPreference? preference) => new V4l2Session(device, preference);
}

public sealed record CaptureControllerOptions
{
    public TimeSpan FrameGapTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Polling for "in use" and "no permission" to clear (neither has a change event).</summary>
    public TimeSpan PauseRetryInterval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Multi-View: cards another viewer in this process holds (V4L2 allows one streamer per node).</summary>
    public Func<string, bool>? IsClaimedElsewhere { get; init; }
    /// <summary>A card window: show only the preferred card, never fall back to another one.</summary>
    public bool PinnedToPreferred { get; init; }
}

public enum CaptureErrorKind { Other, NoAccess, InUse, Gone }

/// <summary>The errno mapping of linux.md §2c.</summary>
public static class CaptureErrorMap
{
    /// <summary>The card lists nothing the renderer can show.</summary>
    public const int NoUsableFormat = -100_000;

    public static CaptureErrorKind Classify(int error) => -error switch
    {
        Errno.EACCES or Errno.EPERM => CaptureErrorKind.NoAccess,
        Errno.EBUSY => CaptureErrorKind.InUse,
        Errno.ENODEV or Errno.ENOENT or Errno.ENXIO or Errno.EIO or Errno.EPROTO => CaptureErrorKind.Gone,
        _ => CaptureErrorKind.Other,
    };
}

/// <summary>Sleep/resume and keep-awake, implemented by the app (logind, GTK inhibit).</summary>
public interface IPowerPort
{
    event Action? Suspending;
    event Action? Resumed;
    /// <summary>Keeps the screen on while live; dispose to release.</summary>
    IDisposable RequestDisplayOn();
}

/// <summary>
/// The Linux capture engine (spec §14.2, §14.5; task 113), a port of the Windows controller: everything that decides
/// anything runs on the scheduler's control thread (choosing the card, opening it, mapping errno, backoff,
/// sleep/resume). Frames do not: they go from the V4L2 capture thread straight to <see cref="FrameArrived"/>.
/// Focus, minimize and workspaces are not inputs, so they can never stop capture.
/// </summary>
public sealed unsafe class CaptureController : IDisposable
{
    private readonly DeviceCatalog _catalog;
    private readonly ICaptureSourceFactory _factory;
    private readonly IScheduler _scheduler;
    private readonly IPowerPort? _power;
    private readonly CaptureControllerOptions _options;
    private readonly IDisposable? _owned;
    private readonly RecoveryMachine _machine = new();
    private readonly Queue<CaptureEvent> _pending = new();

    // Control thread only.
    private bool _started, _draining, _quiet, _sleeping, _waiting, _lost;
    private CaptureDevice? _current;
    private Session? _active;
    private IDisposable? _pauseTimer;
    private IDisposable? _displayRequest;
    private CaptureState _published = new CaptureState.Idle();
    private int _disposed;

    private volatile CaptureState _state = new CaptureState.Idle();
    private volatile CaptureDevice? _publicDevice;
    private volatile ICaptureSession? _publicSession;
    private long _lastFrameTicks;

    public CaptureController(DeviceCatalog catalog, ICaptureSourceFactory factory, IScheduler scheduler,
        IPowerPort? power = null, CaptureControllerOptions? options = null, IDisposable? owned = null)
    {
        _catalog = catalog;
        _factory = factory;
        _scheduler = scheduler;
        _power = power;
        _options = options ?? new();
        _owned = owned;
        catalog.DevicesChanged += OnDevicesChanged;
        if (power != null)
        {
            power.Suspending += OnSuspending;
            power.Resumed += OnResumed;
        }
    }

    /// <summary>A controller on the real machine: its own control thread, V4L2 enumeration and /dev watcher.</summary>
    public static CaptureController CreateDefault(string? preferredKey = null, IPowerPort? power = null, CaptureControllerOptions? options = null)
    {
        var scheduler = new ThreadScheduler();
        var changes = new DevChangeSource();
        var catalog = new DeviceCatalog(new V4l2DeviceList(LinuxLog.Write), scheduler, changes, preferredKey);
        return new CaptureController(catalog, new V4l2SessionFactory(), scheduler, power, options,
            new Disposables(catalog, changes, scheduler));
    }

    /// <summary>Every distinct state, on the control thread (not the UI thread).</summary>
    public event Action<CaptureState>? StateChanged;

    /// <summary>Frames as the driver delivers them, on the capture thread; the buffer goes back when the handler returns.</summary>
    public event V4l2Source.FrameHandler? FrameArrived;

    public event Action<string>? Log;

    public DeviceCatalog Catalog => _catalog;
    public CaptureState State => _state;
    public CaptureDevice? CurrentDevice => _publicDevice;
    /// <summary>What the open stream runs (null while closed). Any thread.</summary>
    public V4l2Stream? CurrentStream => _publicSession?.Stream;
    public V4l2Counters? Counters => _publicSession?.Counters;

    /// <summary>Format choice applied on the next open.</summary>
    public FormatPreference? Preference { get; set; }

    public void Start() => _scheduler.Post(() =>
    {
        if (_started) return;
        _started = true;
        _catalog.RefreshNow();
        Run(() => EvaluateTarget());
    });

    public void Stop() => _scheduler.Post(() =>
    {
        _started = false;
        _current = null;
        _lost = false;
        _waiting = false;
        Run(() => Dispatch(new CaptureEvent.Stop()));
    });

    /// <summary>The user picks a card; it becomes the remembered preferred one.</summary>
    public void Select(string deviceKey) => _scheduler.Post(() =>
    {
        _catalog.PreferredKey = deviceKey;
        if (_started) Run(() => EvaluateTarget());
    });

    /// <summary>Another viewer took or released a card (Multi-View): re-run the card policy.</summary>
    public void ClaimsChanged() => _scheduler.Post(() => EvaluateTarget());

    /// <summary>Starts over now with a fresh backoff (Failed, a long pause, a format change).</summary>
    public void Retry() => _scheduler.Post(() =>
    {
        if (!_started) return;
        _catalog.RefreshNow();
        Run(() => EvaluateTarget(restart: true));
    });

    private void OnDevicesChanged(IReadOnlyList<CaptureDevice> _)
    {
        if (_started) Run(() => EvaluateTarget());
    }

    private void OnSuspending() => _scheduler.Post(() =>
    {
        _sleeping = true;
        Run(() => Dispatch(new CaptureEvent.Interrupted(PauseReason.Sleep)));
    });

    private void OnResumed() => _scheduler.Post(() =>
    {
        _sleeping = false;
        if (!_started) return;
        _catalog.RefreshNow(); // the card re-enumerates (new /dev/videoN) or was pulled while asleep
        Run(() =>
        {
            EvaluateTarget();
            Dispatch(new CaptureEvent.InterruptionEnded());
        });
    });

    /// <summary>Card policy: the preferred card when plugged, else keep the current one, else the first.</summary>
    private void EvaluateTarget(bool restart = false)
    {
        if (!_started) return;
        var devices = _catalog.Devices;
        var preferred = _catalog.PreferredKey is { } key ? _catalog.Find(key) : null;
        var claimed = _options.IsClaimedElsewhere ?? (_ => false);
        var target = _options.PinnedToPreferred
            ? preferred
            : (preferred is { } p && !claimed(p.Key) ? p : null)
              ?? (_current is { } c && !claimed(c.Key) ? _catalog.Find(c.Key) : null)
              ?? devices.FirstOrDefault(d => !claimed(d.Key));

        if (target == null)
        {
            if (_current != null && !_lost)
            {
                _lost = true;
                Log?.Invoke($"{_current.Name} unplugged, waiting for a card");
                Dispatch(new CaptureEvent.Detached(_current.Key));
            }
            _waiting = true;
            _publicDevice = _current;
            return;
        }

        _waiting = false;
        if (_current == null || restart || target.Key != _current.Key)
        {
            Log?.Invoke(_current == null || restart ? $"selecting {target.Name} ({target.Path})" : $"switching from {_current.Name} to {target.Name}");
            _current = target;
            _lost = false;
            _publicDevice = target;
            Dispatch(new CaptureEvent.Start(target.Key, target.Name));
            if (_sleeping) Dispatch(new CaptureEvent.Interrupted(PauseReason.Sleep));
        }
        else if (_lost)
        {
            Log?.Invoke($"{target.Name} is back ({target.Path})");
            _lost = false;
            _current = target;
            _publicDevice = target;
            Dispatch(new CaptureEvent.Attached(target.Key));
        }
        else
        {
            _current = target; // same card, maybe a new /dev node
            _publicDevice = target;
        }
    }

    private void Dispatch(CaptureEvent e)
    {
        _pending.Enqueue(e);
        if (_draining) return;
        _draining = true;
        try
        {
            while (_pending.TryDequeue(out var next)) Execute(_machine.Handle(next));
        }
        finally
        {
            _draining = false;
        }
        _quiet = false;
        Publish();
        Reconcile();
    }

    private void Run(Action work)
    {
        var outer = !_draining;
        _draining = true;
        try
        {
            work();
            while (_pending.TryDequeue(out var next)) Execute(_machine.Handle(next));
        }
        finally
        {
            if (outer) _draining = false;
        }
        if (!outer) return;
        _quiet = false;
        Publish();
        Reconcile();
    }

    private void Execute(CaptureAction action)
    {
        switch (action)
        {
            case CaptureAction.Open:
                Open();
                break;
            case CaptureAction.Close:
                CloseSource();
                break;
            case CaptureAction.RetryAfter retry:
                CloseSource();
                Log?.Invoke($"retrying in {retry.DelaySeconds:F2} s ({_machine.State})");
                _scheduler.Schedule(TimeSpan.FromSeconds(retry.DelaySeconds), () => Run(() => Dispatch(new CaptureEvent.RetryDue(retry.Generation))));
                break;
        }
    }

    private void Open()
    {
        CloseSource();
        if (_current == null) return;
        var device = _catalog.Find(_current.Key);
        if (device == null && _catalog.RefreshNow()) device = _catalog.Find(_current.Key);
        if (device == null)
        {
            EvaluateTarget();
            return;
        }
        _current = device;
        _publicDevice = device;
        if (!_quiet) Publish(); // show Connecting before the (possibly slow) open

        var session = new Session(this, device, _factory.Create(device, Preference));
        _active = session;
        Interlocked.Exchange(ref _lastFrameTicks, _scheduler.Now.Ticks);
        int r;
        try
        {
            r = session.Source.Start();
        }
        catch (Exception e)
        {
            Log?.Invoke($"open failed: {e.GetType().Name} {e.Message}");
            CloseSource();
            Dispatch(new CaptureEvent.StreamError($"Open failed: {e.Message}"));
            return;
        }
        if (r != 0)
        {
            Log?.Invoke($"open {device.Path} failed: {(r == CaptureErrorMap.NoUsableFormat ? "no usable format" : Errno.Name(r))}");
            CloseSource();
            OnError(r);
            return;
        }
        _publicSession = session.Source;
        Log?.Invoke($"streaming {session.Source.Stream}");
        ScheduleWatchdog(session);
    }

    private void OnError(int error)
    {
        if (error == CaptureErrorMap.NoUsableFormat)
        {
            Dispatch(new CaptureEvent.Fatal("The card offers no format HDMI Captain can show."));
            return;
        }
        switch (CaptureErrorMap.Classify(error))
        {
            case CaptureErrorKind.NoAccess:
                Dispatch(new CaptureEvent.Interrupted(PauseReason.PrivacyToggle));
                break;
            case CaptureErrorKind.InUse:
                Dispatch(new CaptureEvent.Interrupted(PauseReason.InUseByAnotherApp));
                break;
            case CaptureErrorKind.Gone:
                GoneOrRetry();
                break;
            default:
                Dispatch(new CaptureEvent.StreamError($"Open failed: {Errno.Name(error)}"));
                break;
        }
    }

    /// <summary>The node vanished under the stream: either really unplugged, or a hiccup worth a retry.</summary>
    private void GoneOrRetry()
    {
        _catalog.RefreshNow();
        if (_current != null && _catalog.Find(_current.Key) == null)
            EvaluateTarget();
        else
            Dispatch(new CaptureEvent.StreamError(RecoveryReason.Disconnected));
    }

    private void CloseSource()
    {
        var session = _active;
        _active = null;
        _publicSession = null;
        if (session == null) return;
        session.Detach();
        session.Source.Dispose();
    }

    private void ScheduleWatchdog(Session session) =>
        _scheduler.Schedule(_options.WatchdogInterval, () => CheckWatchdog(session));

    /// <summary>HDMI signal loss and stalls raise no V4L2 error on many cards; a gap in frames is the signal.</summary>
    private void CheckWatchdog(Session session)
    {
        if (!ReferenceEquals(session, _active)) return;
        var gap = _scheduler.Now - TimeSpan.FromTicks(Interlocked.Read(ref _lastFrameTicks));
        var limit = session.FirstFrameSeen ? _options.FrameGapTimeout : _options.FirstFrameTimeout;
        if (gap > limit)
        {
            Log?.Invoke($"no frames for {gap.TotalSeconds:F1} s ({(session.FirstFrameSeen ? "gap" : "no first frame")})");
            Run(() => Dispatch(new CaptureEvent.StreamError(RecoveryReason.NoFrames)));
            return;
        }
        ScheduleWatchdog(session);
    }

    private void OnFirstFrame(Session session) => Run(() =>
    {
        if (!ReferenceEquals(session, _active)) return;
        var s = session.Source.Stream;
        Dispatch(new CaptureEvent.FirstFrame(new LiveInfo(session.Device.Name, s?.Width ?? 0, s?.Height ?? 0, s?.FrameRate ?? 0, s?.FourCC ?? "")));
    });

    private void OnSourceFailed(Session session, int error) => Run(() =>
    {
        if (!ReferenceEquals(session, _active)) return;
        Log?.Invoke($"stream failed: {Errno.Name(error)}");
        CloseSource();
        OnError(error);
    });

    private void Publish()
    {
        var effective = _waiting && _started ? new CaptureState.Recovering(RecoveryReason.Waiting) : _machine.State;
        if (_quiet && effective is CaptureState.Connecting && _published is CaptureState.Paused) return;
        if (effective == _published) return;
        _published = effective;
        _state = effective;
        Log?.Invoke($"state: {effective}");
        StateChanged?.Invoke(effective);
    }

    private void Reconcile()
    {
        var polling = _published is CaptureState.Paused { Reason: PauseReason.PrivacyToggle or PauseReason.InUseByAnotherApp } && !_sleeping;
        if (polling && _pauseTimer == null)
        {
            _pauseTimer = _scheduler.Schedule(_options.PauseRetryInterval, PollPause);
        }
        else if (!polling)
        {
            _pauseTimer?.Dispose();
            _pauseTimer = null;
        }

        var live = _published is CaptureState.Live;
        if (live && _displayRequest == null && _power != null) _displayRequest = _power.RequestDisplayOn();
        else if (!live)
        {
            _displayRequest?.Dispose();
            _displayRequest = null;
        }
    }

    private void PollPause()
    {
        _pauseTimer = null;
        if (_published is not CaptureState.Paused { Reason: PauseReason.PrivacyToggle or PauseReason.InUseByAnotherApp } || _sleeping) return;
        _quiet = true; // a failed poll goes Paused → Paused without flashing Connecting
        Run(() => Dispatch(new CaptureEvent.InterruptionEnded()));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _catalog.DevicesChanged -= OnDevicesChanged;
        if (_power != null)
        {
            _power.Suspending -= OnSuspending;
            _power.Resumed -= OnResumed;
        }
        using var done = new ManualResetEventSlim();
        _scheduler.Post(() =>
        {
            _started = false;
            _pauseTimer?.Dispose();
            CloseSource();
            _displayRequest?.Dispose();
            _displayRequest = null;
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(5));
        _owned?.Dispose();
    }

    private sealed class Session
    {
        private readonly CaptureController _owner;
        private readonly V4l2Source.FrameHandler _onFrame;
        private readonly Action<int> _onFailed;
        private int _first;
        private volatile bool _detached;

        public Session(CaptureController owner, CaptureDevice device, ICaptureSession source)
        {
            _owner = owner;
            Device = device;
            Source = source;
            _onFrame = OnFrame;
            _onFailed = e => { if (!_detached) _owner._scheduler.Post(() => _owner.OnSourceFailed(this, e)); };
            source.FrameArrived += _onFrame;
            source.Failed += _onFailed;
        }

        public CaptureDevice Device { get; }
        public ICaptureSession Source { get; }
        public bool FirstFrameSeen => Volatile.Read(ref _first) == 1;

        public void Detach()
        {
            _detached = true;
            Source.FrameArrived -= _onFrame;
            Source.Failed -= _onFailed;
        }

        // Capture thread: stamp, flag the first frame, hand the frame on. No thread hop.
        private void OnFrame(byte* data, int length, V4l2Stream stream, long timestampNs)
        {
            if (_detached) return;
            Interlocked.Exchange(ref _owner._lastFrameTicks, _owner._scheduler.Now.Ticks);
            if (Interlocked.Exchange(ref _first, 1) == 0) _owner._scheduler.Post(() => _owner.OnFirstFrame(this));
            _owner.FrameArrived?.Invoke(data, length, stream, timestampNs);
        }
    }

    private sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items) item.Dispose();
        }
    }
}
