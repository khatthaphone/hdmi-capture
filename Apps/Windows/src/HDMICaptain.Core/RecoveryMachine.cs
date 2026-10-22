namespace HDMICaptain.Core;

/// <summary>Inputs to the capture state machine, from the Media Foundation source and the app lifecycle.</summary>
public abstract record CaptureEvent
{
    private CaptureEvent() { }

    /// <summary>The user (or auto-select) wants this device live.</summary>
    public sealed record Start(string DeviceKey, string DeviceName) : CaptureEvent;

    public sealed record Stop : CaptureEvent;

    /// <summary>The source delivered its first frame.</summary>
    public sealed record FirstFrame(LiveInfo Info) : CaptureEvent;

    /// <summary>Open failed or the stream broke; worth retrying.</summary>
    public sealed record StreamError(string Reason) : CaptureEvent;

    /// <summary>No frames for the stall timeout while live: usually the HDMI source changing mode.</summary>
    public sealed record Stalled : CaptureEvent;

    public sealed record Detached(string DeviceKey) : CaptureEvent;

    public sealed record Attached(string DeviceKey) : CaptureEvent;

    public sealed record Interrupted(PauseReason Reason) : CaptureEvent;

    public sealed record InterruptionEnded : CaptureEvent;

    /// <summary>Retrying can't help (e.g. camera permission denied).</summary>
    public sealed record Fatal(string Message) : CaptureEvent;

    /// <summary>A scheduled restart came due; stale ones (older generation) are ignored.</summary>
    public sealed record RetryDue(int Generation) : CaptureEvent;
}

/// <summary>What the controller must do after an event.</summary>
public abstract record CaptureAction
{
    private CaptureAction() { }

    public sealed record None : CaptureAction;

    /// <summary>Open the source now.</summary>
    public sealed record Open : CaptureAction;

    /// <summary>Close the source (and don't reopen until told).</summary>
    public sealed record Close : CaptureAction;

    /// <summary>Close, then post <see cref="CaptureEvent.RetryDue"/> with <paramref name="Generation"/> after <paramref name="DelaySeconds"/>.</summary>
    public sealed record RetryAfter(double DelaySeconds, int Generation) : CaptureAction;
}

/// <summary>
/// Pure port of CaptureEngine's always-on rules (spec section 5.3), via the Kotlin RecoveryMachine:
/// every failure retries with backoff, unplug waits for the same card to come back, interruptions
/// pause without retrying. Not thread-safe: the controller feeds it from its single control thread.
/// Covered by the backoff fixture (recovery-backoff.json) and the event sequences in the unit tests.
/// </summary>
public sealed class RecoveryMachine
{
    private CaptureEvent.Start? _desired;
    private int _attempt;
    private int _generation;
    private PauseReason? _interrupted;

    public CaptureState State { get; private set; } = new CaptureState.Idle();

    public CaptureAction Handle(CaptureEvent e)
    {
        switch (e)
        {
            case CaptureEvent.Start start:
                _desired = start;
                _attempt = 0;
                _generation++;
                _interrupted = null;
                State = new CaptureState.Connecting(start.DeviceName);
                return new CaptureAction.Open();

            case CaptureEvent.Stop:
                _desired = null;
                _generation++;
                State = new CaptureState.Idle();
                return new CaptureAction.Close();

            case CaptureEvent.FirstFrame first:
                if (_desired is null || _interrupted is not null) return new CaptureAction.None();
                _attempt = 0;
                State = new CaptureState.Live(first.Info);
                return new CaptureAction.None();

            case CaptureEvent.StreamError error:
                return ScheduleRestart(error.Reason);

            case CaptureEvent.Stalled:
                return State is CaptureState.Live or CaptureState.Connecting
                    ? ScheduleRestart(RecoveryReason.NoSignal)
                    : new CaptureAction.None();

            case CaptureEvent.Detached detached:
                if (_desired?.DeviceKey != detached.DeviceKey) return new CaptureAction.None();
                _generation++;
                State = new CaptureState.Recovering(RecoveryReason.Disconnected);
                return new CaptureAction.Close();

            case CaptureEvent.Attached attached:
                if (_desired is not { } want || want.DeviceKey != attached.DeviceKey || State is CaptureState.Live || _interrupted is not null)
                    return new CaptureAction.None();
                _attempt = 0;
                _generation++;
                State = new CaptureState.Connecting(want.DeviceName);
                return new CaptureAction.Open();

            case CaptureEvent.Interrupted interrupted:
                if (_desired is null) return new CaptureAction.None();
                _interrupted = interrupted.Reason;
                _generation++;
                State = new CaptureState.Paused(interrupted.Reason);
                return new CaptureAction.Close();

            case CaptureEvent.InterruptionEnded:
                if (_interrupted is null || _desired is not { } resume) return new CaptureAction.None();
                _interrupted = null;
                _attempt = 0;
                _generation++;
                State = new CaptureState.Connecting(resume.DeviceName);
                return new CaptureAction.Open();

            case CaptureEvent.Fatal fatal:
                _generation++;
                State = new CaptureState.Failed(fatal.Message);
                return new CaptureAction.Close();

            case CaptureEvent.RetryDue due:
                if (due.Generation != _generation || _desired is not { } retry || _interrupted is not null) return new CaptureAction.None();
                State = new CaptureState.Connecting(retry.DeviceName);
                return new CaptureAction.Open();

            default:
                throw new ArgumentOutOfRangeException(nameof(e));
        }
    }

    private CaptureAction ScheduleRestart(string reason)
    {
        if (_desired is null || _interrupted is not null) return new CaptureAction.None();
        _attempt++;
        _generation++;
        State = new CaptureState.Recovering(reason);
        return new CaptureAction.RetryAfter(RecoveryBackoff.RestartDelaySeconds(_attempt), _generation);
    }
}
