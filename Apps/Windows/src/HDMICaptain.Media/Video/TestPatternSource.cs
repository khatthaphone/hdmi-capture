using System.Diagnostics;
using HDMICaptain.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace HDMICaptain.Media.Video;

/// <summary>
/// Delivers a generated pattern at a fixed rate on its own thread, through the same <see cref="ICaptureSource"/>
/// path as a card (spec §13.3). Paced by a high-resolution waitable timer, so 60 fps really is 60.
/// </summary>
public sealed class TestPatternSource(TestPatternKind kind, PixelFormat format = PixelFormat.Nv12,
    int width = 1920, int height = 1080, double fps = 60) : ICaptureSource
{
    private readonly PackedFrame _frame = TestPattern.Pack(kind, format, width, height);
    private Thread? _thread;
    private volatile bool _running;

    public event FrameHandler? FrameArrived;

    public TestPatternKind Kind => kind;
    public int Width => width;
    public int Height => height;

    /// <summary>Menu and tile title.</summary>
    public static string Title(TestPatternKind kind) => kind switch
    {
        TestPatternKind.SmpteBars => "SMPTE Bars",
        TestPatternKind.FullBars => "Full Bars",
        TestPatternKind.Grid => "Grid",
        _ => "Gray Ramp",
    };

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "TestPatternSource", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
    }

    public void Dispose() => Stop();

    private const uint TimerAllAccess = 0x1F0003;

    private unsafe void Run()
    {
        using var timer = PInvoke.CreateWaitableTimerEx(null, (string?)null,
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TimerAllAccess);
        var period = TimeSpan.FromSeconds(1 / fps);
        var clock = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        while (_running)
        {
            var frame = _frame.AsFrame(timestamp: clock.Elapsed.Ticks);
            FrameArrived?.Invoke(in frame);

            next += period;
            var wait = next - clock.Elapsed;
            if (wait <= TimeSpan.Zero)
            {
                next = clock.Elapsed; // fell behind (debugger, slow present): don't burst to catch up
                continue;
            }
            var due = -wait.Ticks; // relative, 100 ns units
            PInvoke.SetWaitableTimer(timer, in due, 0, null, null, false);
            PInvoke.WaitForSingleObject(timer, 1000);
        }
    }
}
