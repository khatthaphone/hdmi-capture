using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace HDMICaptain.Media.Capture;

/// <summary>
/// Serial work queue with a monotonic clock and delayed work. The capture controller and catalog run only inside it,
/// so tests substitute a virtual-time implementation and never sleep.
/// </summary>
public interface IScheduler
{
    /// <summary>Monotonic time since an arbitrary origin.</summary>
    TimeSpan Now { get; }

    /// <summary>Runs <paramref name="work"/> on the scheduler's thread, after everything queued before it.</summary>
    void Post(Action work);

    /// <summary>Runs <paramref name="work"/> after <paramref name="delay"/>; disposing the result cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action work);
}

/// <summary>
/// The control thread: one dedicated MTA thread (Media Foundation and COM want a free-threaded apartment), opted out of
/// EcoQoS so Windows doesn't throttle the always-on recovery loop when the window is hidden.
/// </summary>
public sealed class ThreadScheduler : IScheduler, IDisposable
{
    private readonly object _gate = new();
    private readonly PriorityQueue<Item, (long Due, long Sequence)> _queue = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private long _sequence;
    private bool _stopped;

    public ThreadScheduler(string name = "HDMICaptain.Control")
    {
        _thread = new Thread(Run) { Name = name, IsBackground = true };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Work threw: the loop keeps running, the owner decides how loud to be.</summary>
    public event Action<Exception>? Unhandled;

    public TimeSpan Now => _clock.Elapsed;

    public void Post(Action work) => Schedule(TimeSpan.Zero, work);

    public IDisposable Schedule(TimeSpan delay, Action work)
    {
        var item = new Item(work);
        lock (_gate)
        {
            if (_stopped) return item;
            _queue.Enqueue(item, (Now.Ticks + Math.Max(0, delay.Ticks), _sequence++));
            Monitor.Pulse(_gate);
        }
        return item;
    }

    private void Run()
    {
        OptOutOfEcoQos();
        while (true)
        {
            Item run;
            lock (_gate)
            {
                while (true)
                {
                    if (_stopped) return;
                    if (!_queue.TryPeek(out var next, out var key)) { Monitor.Wait(_gate); continue; }
                    if (next.Cancelled) { _queue.Dequeue(); continue; }
                    var wait = key.Due - Now.Ticks;
                    if (wait <= 0) { _queue.Dequeue(); run = next; break; }
                    Monitor.Wait(_gate, TimeSpan.FromTicks(wait));
                }
            }
            try
            {
                run.Work();
            }
            catch (Exception e)
            {
                Unhandled?.Invoke(e);
            }
        }
    }

    /// <summary>THREAD_POWER_THROTTLE_STATE (processthreadsapi.h); CsWin32 doesn't project it.</summary>
    private struct ThreadPowerThrottleState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private static unsafe void OptOutOfEcoQos()
    {
        var state = new ThreadPowerThrottleState
        {
            Version = 1, // THREAD_POWER_THROTTLING_CURRENT_VERSION
            ControlMask = 1, // THREAD_POWER_THROTTLING_EXECUTION_SPEED
            StateMask = 0, // controlled, and not throttled
        };
        PInvoke.SetThreadInformation(PInvoke.GetCurrentThread(), THREAD_INFORMATION_CLASS.ThreadPowerThrottling,
            &state, (uint)sizeof(ThreadPowerThrottleState));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            Monitor.PulseAll(_gate);
        }
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class Item(Action work) : IDisposable
    {
        public readonly Action Work = work;
        public volatile bool Cancelled;

        public void Dispose() => Cancelled = true;
    }
}
