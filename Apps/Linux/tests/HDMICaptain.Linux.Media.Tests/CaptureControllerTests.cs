using HDMICaptain.Core;
using HDMICaptain.Linux.Media.Audio;
using HDMICaptain.Linux.Media.Capture;

namespace HDMICaptain.Linux.Media.Tests;

/// <summary>Virtual time: nothing runs until the test advances the clock, and everything runs on the test thread.</summary>
internal sealed class VirtualScheduler : IScheduler
{
    private readonly List<(TimeSpan Due, long Seq, Item Item)> _items = [];
    private readonly object _gate = new(); // Dispose posts from another thread while the test pumps
    private long _seq;

    public TimeSpan Now { get; private set; }

    public void Post(Action work) => Schedule(TimeSpan.Zero, work);

    public IDisposable Schedule(TimeSpan delay, Action work)
    {
        var item = new Item(work);
        lock (_gate) _items.Add((Now + delay, _seq++, item));
        return item;
    }

    public void Advance(TimeSpan span)
    {
        var end = Now + span;
        while (true)
        {
            (TimeSpan, long, Item)? next;
            lock (_gate)
            {
                next = _items.Where(i => !i.Item.Cancelled && i.Due <= end).OrderBy(i => i.Due).ThenBy(i => i.Seq).Cast<(TimeSpan, long, Item)?>().FirstOrDefault();
                if (next is { } found) _items.RemoveAll(i => i.Seq == found.Item2);
            }
            if (next is not { } n) break;
            if (n.Item1 > Now) Now = n.Item1;
            n.Item3.Work();
        }
        Now = end;
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    public void Settle() => Advance(TimeSpan.Zero);

    private sealed class Item(Action work) : IDisposable
    {
        public readonly Action Work = work;
        public bool Cancelled;

        public void Dispose() => Cancelled = true;
    }
}

internal sealed unsafe class FakeSession : ICaptureSession
{
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public CaptureDevice Device { get; init; } = null!;
    public int OpenError { get; init; }

    public event Action<int>? Failed;
    public event V4l2Source.FrameHandler? FrameArrived;

    public V4l2Stream? Stream { get; private set; }
    public V4l2Counters Counters { get; } = new();

    public int Start()
    {
        if (OpenError != 0) return OpenError;
        Started = true;
        Stream = new V4l2Stream("MJPG", 1920, 1080, 0, 60, 4);
        return 0;
    }

    public void Dispose()
    {
        Started = false;
        Disposed = true;
    }

    public void EmitFrame() => FrameArrived?.Invoke(null, 0, Stream!, 1);

    public void Fail(int errno) => Failed?.Invoke(errno);
}

internal sealed class FakeFactory : ICaptureSourceFactory
{
    public List<FakeSession> Sessions { get; } = [];
    public Dictionary<string, int> OpenErrors { get; } = [];
    public FakeSession Last => Sessions[^1];

    public ICaptureSession Create(CaptureDevice device, FormatPreference? preference)
    {
        var session = new FakeSession { Device = device, OpenError = OpenErrors.GetValueOrDefault(device.Key) };
        Sessions.Add(session);
        return session;
    }
}

internal sealed class FakeList : IDeviceList, IDeviceChangeSource
{
    public List<CaptureDevice> Devices { get; } = [];
    public event Action? Changed;
    public IReadOnlyList<CaptureDevice> Enumerate() => [.. Devices];
    public void Raise() => Changed?.Invoke();
}

internal sealed class FakePower : IPowerPort
{
    public int ActiveRequests { get; private set; }
    public event Action? Suspending;
    public event Action? Resumed;

    public IDisposable RequestDisplayOn()
    {
        ActiveRequests++;
        return new Release(this);
    }

    public void Sleep() => Suspending?.Invoke();
    public void Wake() => Resumed?.Invoke();

    private sealed class Release(FakePower owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner.ActiveRequests--;
        }
    }
}

public sealed class CaptureControllerTests
{
    private static readonly CaptureDevice A = new("usb:A", "Card A", "/dev/video0", null, null, null);
    private static readonly CaptureDevice B = new("usb:B", "Card B", "/dev/video2", null, null, null);

    private sealed class Harness : IDisposable
    {
        public readonly VirtualScheduler Time = new();
        public readonly FakeList List = new();
        public readonly FakeFactory Factory = new();
        public readonly FakePower Power = new();
        public readonly DeviceCatalog Catalog;
        public readonly CaptureController Controller;
        public readonly List<CaptureState> States = [];
        public int Frames;

        public unsafe Harness(string? preferred = null, params CaptureDevice[] plugged)
        {
            List.Devices.AddRange(plugged);
            Catalog = new DeviceCatalog(List, Time, List, preferred);
            Controller = new CaptureController(Catalog, Factory, Time, Power);
            Controller.StateChanged += States.Add;
            Controller.FrameArrived += (_, _, _, _) => Frames++;
        }

        public CaptureState State => Controller.State;

        public void StartAndGoLive()
        {
            Controller.Start();
            Time.Settle();
            GoLive();
        }

        public void GoLive()
        {
            Factory.Last.EmitFrame();
            Time.Settle();
            Assert.IsType<CaptureState.Live>(State);
        }

        public void Plug(CaptureDevice d)
        {
            List.Devices.Add(d);
            List.Raise();
            Time.Advance(0.6);
        }

        public void Unplug(CaptureDevice d)
        {
            List.Devices.Remove(d);
            List.Raise();
            Time.Advance(0.6);
        }

        // The controller's Dispose waits for its scheduler: run it aside and pump virtual time until it returns.
        public void Dispose()
        {
            var done = Task.Run(Controller.Dispose);
            while (!done.IsCompleted)
            {
                Time.Settle();
                Thread.Sleep(1);
            }
        }
    }

    [Fact]
    public void OpensTheCardAndGoesLiveOnTheFirstFrame()
    {
        using var h = new Harness(null, A);
        h.Controller.Start();
        h.Time.Settle();
        Assert.IsType<CaptureState.Connecting>(h.State);
        h.Factory.Last.EmitFrame();
        h.Time.Settle();
        var live = Assert.IsType<CaptureState.Live>(h.State);
        Assert.Equal(new LiveInfo("Card A", 1920, 1080, 60, "MJPG"), live.Info);
        Assert.Equal(1, h.Frames);
        Assert.NotNull(h.Controller.CurrentStream);
    }

    [Fact]
    public void NoCardMeansWaitingUntilOneArrives()
    {
        using var h = new Harness();
        h.Controller.Start();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Waiting), h.State);
        h.Plug(A);
        Assert.IsType<CaptureState.Connecting>(h.State);
        h.GoLive();
    }

    [Theory]
    [InlineData(Errno.EACCES)]
    [InlineData(Errno.EPERM)]
    public void NoPermissionPausesThenResumesWhenGranted(int errno)
    {
        using var h = new Harness(null, A);
        h.Factory.OpenErrors["usb:A"] = -errno;
        h.Controller.Start();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Paused(PauseReason.PrivacyToggle), h.State);
        h.Time.Advance(10); // keeps polling without flashing Connecting
        Assert.Equal([typeof(CaptureState.Connecting), typeof(CaptureState.Paused)], h.States.Select(s => s.GetType()));
        h.Factory.OpenErrors.Clear();
        h.Time.Advance(3.1);
        h.GoLive();
    }

    [Fact]
    public void BusyPausesAsInUseThenResumesWhenReleased()
    {
        using var h = new Harness(null, A);
        h.Factory.OpenErrors["usb:A"] = -Errno.EBUSY;
        h.Controller.Start();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Paused(PauseReason.InUseByAnotherApp), h.State);
        h.Factory.OpenErrors.Clear();
        h.Time.Advance(3.1);
        h.GoLive();
    }

    [Fact]
    public void NoFirstFrameRestartsWithBackoff()
    {
        using var h = new Harness(null, A);
        h.Controller.Start();
        h.Time.Advance(5.6);
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.NoFrames), h.State);
        Assert.True(h.Factory.Sessions[0].Disposed);
        h.Time.Advance(0.3);
        Assert.Equal(2, h.Factory.Sessions.Count);
        h.GoLive();
    }

    [Fact]
    public void FrameGapWhileLiveRecovers()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Time.Advance(2);
        h.Factory.Last.EmitFrame();
        h.Time.Advance(2.9);
        Assert.IsType<CaptureState.Live>(h.State);
        h.Time.Advance(0.7);
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.NoFrames), h.State);
        h.Time.Advance(0.3);
        h.GoLive();
    }

    [Fact]
    public void UnplugMidStreamWaitsThenReplugResumes()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Unplug(A);
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Waiting), h.State);
        Assert.True(h.Factory.Last.Disposed);
        h.Plug(A with { Path = "/dev/video4" }); // replugged cards come back on a new node
        Assert.IsType<CaptureState.Connecting>(h.State);
        Assert.Equal("/dev/video4", h.Factory.Last.Device.Path);
        h.GoLive();
    }

    [Fact]
    public void EnodevBeforeTheWatcherStillDetectsTheUnplug()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.List.Devices.Remove(A); // enumeration already misses it, inotify event not delivered yet
        h.Factory.Last.Fail(-Errno.ENODEV);
        h.Time.Settle();
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Waiting), h.State);
    }

    [Fact]
    public void EioWhileStillListedRetriesInPlace()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Factory.Last.Fail(-Errno.EIO);
        h.Time.Settle();
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Disconnected), h.State);
        h.Time.Advance(0.3);
        Assert.Equal(2, h.Factory.Sessions.Count);
        h.GoLive();
    }

    [Fact]
    public void PreferredCardReturningSwitchesBack()
    {
        using var h = new Harness("usb:A", A, B);
        h.StartAndGoLive();
        h.Unplug(A);
        h.GoLive();
        Assert.Equal(B, h.Controller.CurrentDevice);
        h.Plug(A);
        Assert.Equal(A, h.Controller.CurrentDevice);
        h.GoLive();
    }

    [Fact]
    public void SleepPausesAndResumeReopens()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Power.Sleep();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Paused(PauseReason.Sleep), h.State);
        Assert.True(h.Factory.Last.Disposed);
        h.Time.Advance(60);
        Assert.Single(h.Factory.Sessions);
        h.Power.Wake();
        h.Time.Settle();
        Assert.Equal(2, h.Factory.Sessions.Count);
        h.GoLive();
    }

    [Fact]
    public void DisplayStaysOnOnlyWhileLive()
    {
        using var h = new Harness(null, A);
        h.Controller.Start();
        h.Time.Settle();
        Assert.Equal(0, h.Power.ActiveRequests);
        h.GoLive();
        Assert.Equal(1, h.Power.ActiveRequests);
        h.Power.Sleep();
        h.Time.Settle();
        Assert.Equal(0, h.Power.ActiveRequests);
    }

    [Fact]
    public void NoUsableFormatFailsUntilRetried()
    {
        using var h = new Harness(null, A);
        h.Factory.OpenErrors["usb:A"] = CaptureErrorMap.NoUsableFormat;
        h.Controller.Start();
        h.Time.Advance(30);
        Assert.IsType<CaptureState.Failed>(h.State);
        Assert.Single(h.Factory.Sessions);
        h.Factory.OpenErrors.Clear();
        h.Controller.Retry();
        h.Time.Settle();
        h.GoLive();
    }

    [Theory]
    [InlineData(-Errno.EBUSY, CaptureErrorKind.InUse)]
    [InlineData(-Errno.EACCES, CaptureErrorKind.NoAccess)]
    [InlineData(-Errno.ENODEV, CaptureErrorKind.Gone)]
    [InlineData(-Errno.EPROTO, CaptureErrorKind.Gone)]
    [InlineData(-Errno.EINVAL, CaptureErrorKind.Other)]
    public void ClassifiesErrno(int error, CaptureErrorKind expected) => Assert.Equal(expected, CaptureErrorMap.Classify(error));

    [Fact]
    public void OneCardPerUsbDeviceKeyedAndPairedWithItsAlsaCard()
    {
        var usb = new UsbIdentity(0x345F, 0x2130, null, "MacroSilicon", "USB3 Video", 5000, "/sys/devices/x/2-1");
        V4l2Device Node(string path) => new(path, "USB3 Video", "uvcvideo", "usb-0000:00:14.0-1", usb,
            [new V4l2Format("YUYV", false, 1920, 1080, [(1, 60)])]);
        var alsa = new AlsaCard(1, "Video", "USB3 Video", usb);
        var devices = DeviceCatalog.Build([Node("/dev/video0"), Node("/dev/video1")], [alsa]);
        var d = Assert.Single(devices);
        Assert.Equal("usb:345F:2130|name:USB3 Video", d.Key);
        Assert.Equal("/dev/video0", d.Path);
        Assert.Equal("hw:1,0", d.Audio?.HwName);
    }

    [Fact]
    public void ACardWeMayNotOpenIsStillListed()
    {
        // No permission (not in the video group): the card shows up without formats, so opening it reports NoAccess
        // instead of the viewer saying no card is connected.
        var usb = new UsbIdentity(0x1DE1, 0xF105, null, null, "UGREEN-25854", 480, "/sys/devices/x/1-1");
        var denied = new V4l2Device("/dev/video0", "UGREEN-25854: UGREEN-25854", "", "", usb, [], Denied: true);
        var d = Assert.Single(DeviceCatalog.Build([denied], []));
        Assert.Equal("UGREEN-25854", d.Name);
        Assert.Null(d.Node);
        Assert.Empty(DeviceCatalog.Build([denied with { Denied = false }], []));
    }
}
