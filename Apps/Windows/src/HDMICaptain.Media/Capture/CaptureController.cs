using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Video;

namespace HDMICaptain.Media.Capture;

/// <summary>An <see cref="ICaptureSource"/> that reports why it stopped and what it runs; <see cref="MfSource"/> is the real one.</summary>
public interface ICaptureSession : ICaptureSource
{
    /// <summary>Capture stopped on its own (unplug, preemption, privacy). Raised once, on any thread.</summary>
    event Action<CaptureError>? Failed;

    /// <summary>Diagnostic lines. On any thread.</summary>
    event Action<string>? Log;

    NativeVideoType? CurrentType { get; }
    CaptureStats Stats { get; }
}

/// <summary>Creates a session for a card. <see cref="ICaptureSource.Start"/> throws <see cref="COMException"/> when the open fails.</summary>
public interface ICaptureSourceFactory
{
    ICaptureSession Create(CaptureDevice device, FormatPreference? preference);
}

/// <summary>Opens cards through Media Foundation.</summary>
public sealed class MfSourceFactory(MjpegPolicy mjpegPolicy = MjpegPolicy.Auto) : ICaptureSourceFactory
{
    public ICaptureSession Create(CaptureDevice device, FormatPreference? preference) =>
        new MfSource(device.SymbolicLink, preference, device.Usb?.Link, mjpegPolicy);
}

/// <summary>Timing of the controller's watchdog and retries.</summary>
public sealed record CaptureControllerOptions
{
    /// <summary>No frame this long while live means the card lost its input or the pipeline stalled.</summary>
    public TimeSpan FrameGapTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>An opened source that never delivers a first frame within this long is restarted.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the watchdog looks.</summary>
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Polling for "privacy off" and "in use by another app" to clear (neither has a reliable change event).</summary>
    public TimeSpan PauseRetryInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Capture raw MJPG and decode it with libjpeg-turbo instead of taking Windows' decoded NV12 twin
    /// (<see cref="MjpegPolicy.PreferOwnDecode"/>). Off by default until 092 measures which path has less latency.
    /// </summary>
    public bool PreferOwnMjpegDecode { get; init; }

    /// <summary>
    /// Multi-View (task 098): cards another viewer in this process holds. Two readers of one card inside one process
    /// preempt each other (MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED every few seconds), so fallbacks skip these.
    /// </summary>
    public Func<string, bool>? IsClaimedElsewhere { get; init; }

    /// <summary>A card window: show only the preferred card, never fall back to another one.</summary>
    public bool PinnedToPreferred { get; init; }

    public MjpegPolicy MjpegPolicy => PreferOwnMjpegDecode ? MjpegPolicy.PreferOwnDecode : MjpegPolicy.Auto;
}

/// <summary>How an HRESULT from MF is treated.</summary>
public enum CaptureErrorKind
{
    Other,
    /// <summary>E_ACCESSDENIED: camera privacy is off for this app.</summary>
    PrivacyBlocked,
    /// <summary>Another app preempted or locked the card.</summary>
    InUse,
    /// <summary>The device went away (invalidated, shut down, removed).</summary>
    Gone,
}

/// <summary>The error mapping of windows.md §2d.</summary>
public static class CaptureErrorMap
{
    public static CaptureErrorKind Classify(int hr) => hr switch
    {
        MfErrors.AccessDenied => CaptureErrorKind.PrivacyBlocked,
        MfErrors.DevicePreempted or MfErrors.HwMftFailedStartStreaming or MfErrors.VideoDeviceLocked or MfErrors.SharingViolation
            => CaptureErrorKind.InUse,
        MfErrors.DeviceInvalidated or MfErrors.Shutdown or MfErrors.DeviceRemoved or MfErrors.DeviceNotConnected
            => CaptureErrorKind.Gone,
        _ => CaptureErrorKind.Other,
    };
}

/// <summary>
/// The Windows capture engine (spec §13.2, §13.5; task 093). Everything that decides anything runs on the scheduler's
/// control thread: choosing the card, opening it, mapping errors, backoff, sleep/resume. Frames do not: they go from
/// the MF work-queue thread straight to <see cref="FrameArrived"/>. Minimize, occlusion and focus are not inputs here,
/// so they can never stop capture. Subscribe to <see cref="StateChanged"/> and marshal to the UI thread yourself.
/// </summary>
public sealed class CaptureController : IDisposable
{
    private readonly DeviceCatalog _catalog;
    private readonly ICaptureSourceFactory _factory;
    private readonly IScheduler _scheduler;
    private readonly IPowerPort? _power;
    private readonly CaptureControllerOptions _options;
    private readonly IDisposable? _owned;
    private readonly RecoveryMachine _machine = new();
    private readonly Queue<CaptureEvent> _pending = new();

    // Control thread only, unless marked otherwise.
    private bool _started;
    private bool _draining;
    private bool _quiet;
    private bool _sleeping;
    private bool _waiting;
    private bool _lost;
    private CaptureDevice? _current;
    private Session? _active;
    private IDisposable? _pauseTimer;
    private IDisposable? _displayRequest;
    private CaptureState _published = new CaptureState.Idle();
    private int _disposed;

    private volatile CaptureState _state = new CaptureState.Idle();
    private volatile CaptureDevice? _publicDevice;
    private volatile NativeVideoType? _publicType;
    private volatile CaptureStats? _publicStats;
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

    /// <summary>
    /// A controller wired to the real machine: its own control thread, MF enumeration, hot-plug watcher, power port and
    /// MF sources. Disposing the controller disposes all of them.
    /// </summary>
    public static CaptureController CreateDefault(string? preferredKey = null, CaptureControllerOptions? options = null)
    {
        var scheduler = new ThreadScheduler();
        var watcher = new WatcherChangeSource();
        var catalog = new DeviceCatalog(new MfDeviceList(), scheduler, watcher, preferredKey);
        var power = new WindowsPower();
        return new CaptureController(catalog, new MfSourceFactory(options?.MjpegPolicy ?? MjpegPolicy.Auto), scheduler, power, options,
            new Disposables(power, catalog, watcher, scheduler));
    }

    /// <summary>Every state change, on the control thread (not the UI thread). Only distinct states are raised.</summary>
    public event Action<CaptureState>? StateChanged;

    /// <summary>Frames exactly as the source delivers them: invoked on the MF thread while the buffer is locked.</summary>
    public event FrameHandler? FrameArrived;

    /// <summary>Diagnostic lines (controller decisions and the source's own), on any thread.</summary>
    public event Action<string>? Log;

    public DeviceCatalog Catalog => _catalog;

    /// <summary>The latest published state (any thread).</summary>
    public CaptureState State => _state;

    /// <summary>The card being captured or waited for (any thread).</summary>
    public CaptureDevice? CurrentDevice => _publicDevice;

    /// <summary>The native type the open source runs; null while no source is open (any thread).</summary>
    public NativeVideoType? CurrentType => _publicType;

    /// <summary>Frame and drop counts of the open source (any thread).</summary>
    public CaptureStats? Stats => _publicStats;

    /// <summary>Format choice applied on the next open.</summary>
    public FormatPreference? Preference { get; set; }

    /// <summary>Starts always-on capture: refreshes the catalog and opens the preferred (else first) card.</summary>
    public void Start() => _scheduler.Post(() =>
    {
        if (_started) return;
        _started = true;
        _catalog.RefreshNow();
        Run(() => EvaluateTarget());
    });

    /// <summary>Stops capture and closes the card. <see cref="Start"/> resumes.</summary>
    public void Stop() => _scheduler.Post(() =>
    {
        _started = false;
        _current = null;
        _lost = false;
        _waiting = false;
        Run(() => Dispatch(new CaptureEvent.Stop()));
    });

    /// <summary>The user picks a card; it becomes the remembered preferred one. An unplugged key is remembered for later.</summary>
    public void Select(string deviceKey) => _scheduler.Post(() =>
    {
        _catalog.PreferredKey = deviceKey;
        if (_started) Run(() => EvaluateTarget());
    });

    /// <summary>Starts over now with a fresh backoff: from Failed, from a long Paused, or to kick a stuck card.</summary>
    /// <summary>Another viewer took or released a card (Multi-View): re-run the card policy.</summary>
    public void ClaimsChanged() => _scheduler.Post(() => EvaluateTarget());

    public void Retry() => _scheduler.Post(() =>
    {
        if (!_started) return;
        _catalog.RefreshNow();
        Run(() => EvaluateTarget(restart: true));
    });

    private void OnDevicesChanged(IReadOnlyList<CaptureDevice> _) // scheduler thread (the catalog refreshes there)
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
        _catalog.RefreshNow(); // the card may have been pulled while asleep
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
            SetCurrent(_current);
            return;
        }

        _waiting = false;
        if (_current == null || restart || target.Key != _current.Key)
        {
            Log?.Invoke(_current == null || restart ? $"selecting {target.Name}" : $"switching from {_current.Name} to {target.Name}");
            _current = target;
            _lost = false;
            SetCurrent(target);
            Dispatch(new CaptureEvent.Start(target.Key, target.Name));
            if (_sleeping) Dispatch(new CaptureEvent.Interrupted(PauseReason.Sleep));
        }
        else if (_lost)
        {
            Log?.Invoke($"{target.Name} is back");
            _lost = false;
            _current = target;
            SetCurrent(target);
            Dispatch(new CaptureEvent.Attached(target.Key));
        }
        else
        {
            _current = target; // same card, maybe a new symbolic link
            SetCurrent(target);
        }
    }

    /// <summary>Feeds one event and everything it causes; publishes once at the end.</summary>
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

    /// <summary>Runs work that dispatches events, then publishes (Dispatch does so itself when it was the outermost call).</summary>
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
                _scheduler.Schedule(TimeSpan.FromSeconds(retry.DelaySeconds),
                    () => Run(() => Dispatch(new CaptureEvent.RetryDue(retry.Generation))));
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
            EvaluateTarget(); // gone: switch to another card or wait
            return;
        }
        _current = device;
        SetCurrent(device);
        if (!_quiet) Publish(); // show Connecting before the (possibly slow) open

        var session = new Session(this, device, _factory.Create(device, Preference));
        _active = session;
        _publicStats = session.Source.Stats;
        try
        {
            Interlocked.Exchange(ref _lastFrameTicks, _scheduler.Now.Ticks);
            session.Source.Start();
        }
        catch (COMException e)
        {
            Log?.Invoke($"open failed: {MfErrors.Name(e.HResult)} {e.Message}");
            CloseSource();
            OnOpenError(e.HResult, e.Message);
            return;
        }
        catch (Exception e)
        {
            Log?.Invoke($"open failed: {e.GetType().Name} {e.Message}");
            CloseSource();
            Dispatch(new CaptureEvent.StreamError($"Open failed: {e.Message}"));
            return;
        }
        _publicType = session.Source.CurrentType;
        ScheduleWatchdog(session);
    }

    private void OnOpenError(int hr, string message)
    {
        switch (CaptureErrorMap.Classify(hr))
        {
            case CaptureErrorKind.PrivacyBlocked:
                Dispatch(new CaptureEvent.Interrupted(PauseReason.PrivacyToggle));
                break;
            case CaptureErrorKind.InUse:
                Dispatch(new CaptureEvent.Interrupted(PauseReason.InUseByAnotherApp));
                break;
            case CaptureErrorKind.Gone:
                GoneOrRetry();
                break;
            default:
                if (hr == MfErrors.NoMoreTypes)
                    Dispatch(new CaptureEvent.Fatal(message)); // nothing the renderer can show: retrying won't change that
                else
                    Dispatch(new CaptureEvent.StreamError($"Open failed: {MfErrors.Name(hr)}"));
                break;
        }
    }

    /// <summary>The card vanished under the source: either it's really unplugged, or it hiccuped and is worth a retry.</summary>
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
        _publicType = null;
        if (session == null) return;
        session.Detach();
        try
        {
            session.Source.Stop();
        }
        catch (Exception e)
        {
            Log?.Invoke($"stop failed: {e.Message}");
        }
        session.Source.Dispose();
    }

    private void ScheduleWatchdog(Session session) =>
        _scheduler.Schedule(_options.WatchdogInterval, () => CheckWatchdog(session));

    /// <summary>
    /// HDMI signal loss and a stalled pipeline raise no MF error (windows.md §2d); a gap in frames is the signal.
    /// </summary>
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
        _publicType = session.Source.CurrentType;
        var type = _publicType;
        var info = new LiveInfo(session.Device.Name, type?.Width ?? 0, type?.Height ?? 0, type?.FrameRate ?? 0, type?.FourCC ?? "");
        Dispatch(new CaptureEvent.FirstFrame(info));
    });

    private void OnSourceFailed(Session session, CaptureError error) => Run(() =>
    {
        if (!ReferenceEquals(session, _active)) return;
        Log?.Invoke($"source failed: {error}");
        CloseSource();
        OnOpenError(error.HResult, error.ToString());
    });

    private void SetCurrent(CaptureDevice? device) => _publicDevice = device;

    private void Publish()
    {
        var effective = _waiting && _started ? new CaptureState.Recovering(RecoveryReason.Waiting) : _machine.State;
        if (_quiet && effective is CaptureState.Connecting && _published is CaptureState.Paused) return;
        if (effective == _published) return;
        _published = effective;
        _state = effective;
        Log?.Invoke($"state: {effective}");
        if (Diagnostics.MediaEvents.Log.IsEnabled()) Diagnostics.MediaEvents.Log.CaptureState(effective.ToString());
        StateChanged?.Invoke(effective);
    }

    /// <summary>Things that follow the state: pause polling and the keep-awake request.</summary>
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
        if (_published is not CaptureState.Paused { Reason: PauseReason.PrivacyToggle or PauseReason.InUseByAnotherApp } || _sleeping)
            return;
        _quiet = true; // a failed poll goes Paused -> Paused without flashing Connecting
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

    /// <summary>One open source and the hooks that tie its threads back to the controller.</summary>
    private sealed class Session
    {
        private readonly CaptureController _owner;
        private readonly FrameHandler _onFrame;
        private readonly Action<CaptureError> _onFailed;
        private readonly Action<string> _onLog;
        private int _first;
        private volatile bool _detached;

        public Session(CaptureController owner, CaptureDevice device, ICaptureSession source)
        {
            _owner = owner;
            Device = device;
            Source = source;
            _onFrame = OnFrame;
            _onFailed = e => { if (!_detached) _owner._scheduler.Post(() => _owner.OnSourceFailed(this, e)); };
            _onLog = line => _owner.Log?.Invoke(line);
            source.FrameArrived += _onFrame;
            source.Failed += _onFailed;
            source.Log += _onLog;
        }

        public CaptureDevice Device { get; }
        public ICaptureSession Source { get; }
        public bool FirstFrameSeen => Volatile.Read(ref _first) == 1;

        public void Detach()
        {
            _detached = true;
            Source.FrameArrived -= _onFrame;
            Source.Failed -= _onFailed;
            Source.Log -= _onLog;
        }

        // On the MF thread: stamp, flag the first frame, hand the frame on. No thread hop.
        private void OnFrame(in VideoFrame frame)
        {
            if (_detached) return;
            Interlocked.Exchange(ref _owner._lastFrameTicks, _owner._scheduler.Now.Ticks);
            if (Interlocked.Exchange(ref _first, 1) == 0) _owner._scheduler.Post(() => _owner.OnFirstFrame(this));
            _owner.FrameArrived?.Invoke(in frame);
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
