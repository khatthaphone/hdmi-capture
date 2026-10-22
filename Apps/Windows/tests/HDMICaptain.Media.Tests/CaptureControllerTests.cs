using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Video;

namespace HDMICaptain.Media.Tests;

/// <summary>Virtual time: nothing runs until the test advances the clock, and everything runs on the test thread.</summary>
internal sealed class VirtualScheduler : IScheduler
{
    private readonly List<(TimeSpan Due, long Seq, Item Item)> _items = [];
    private long _seq;

    public TimeSpan Now { get; private set; }

    public void Post(Action work) => Schedule(TimeSpan.Zero, work);

    public IDisposable Schedule(TimeSpan delay, Action work)
    {
        var item = new Item(work);
        _items.Add((Now + delay, _seq++, item));
        return item;
    }

    /// <summary>Runs everything due within <paramref name="span"/>, in order, including work it schedules.</summary>
    public void Advance(TimeSpan span)
    {
        var end = Now + span;
        while (true)
        {
            var next = _items.Where(i => !i.Item.Cancelled && i.Due <= end).OrderBy(i => i.Due).ThenBy(i => i.Seq).Cast<(TimeSpan, long, Item)?>().FirstOrDefault();
            if (next is not { } n) break;
            _items.RemoveAll(i => i.Seq == n.Item2);
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

internal sealed class FakeSession : ICaptureSession
{
    public static readonly NativeVideoType Type = new(0, Guid.Empty, "NV12", 1920, 1080, 60, 1, null, ColorMatrix.Auto, false);

    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public CaptureDevice Device { get; init; } = null!;

    public event FrameHandler? FrameArrived;
    public event Action<CaptureError>? Failed;
    public event Action<string>? Log;

    public NativeVideoType? CurrentType => Type;
    public CaptureStats Stats { get; } = new();

    /// <summary>When set, <see cref="Start"/> throws like MF does for a failed open.</summary>
    public int? OpenError { get; init; }

    public void Start()
    {
        if (OpenError is { } hr) throw new COMException("open failed", hr);
        Started = true;
    }

    public void Stop()
    {
        Started = false;
        Stopped = true;
    }

    public void Dispose() => Stop();

    public void EmitFrame()
    {
        var frame = new VideoFrame { Format = PixelFormat.Nv12, Width = 1920, Height = 1080, Plane0 = 0, Stride0 = 1920 };
        FrameArrived?.Invoke(in frame);
    }

    public void Fail(int hr) => Failed?.Invoke(new CaptureError(hr, "ReadSample"));

    public void Say(string line) => Log?.Invoke(line);
}

internal sealed class FakeFactory : ICaptureSourceFactory
{
    public List<FakeSession> Sessions { get; } = [];

    /// <summary>HRESULT the next open of a card fails with; absent means it opens.</summary>
    public Dictionary<string, int> OpenErrors { get; } = [];

    public FakeSession Last => Sessions[^1];

    public ICaptureSession Create(CaptureDevice device, FormatPreference? preference)
    {
        var session = new FakeSession { Device = device, OpenError = OpenErrors.TryGetValue(device.Key, out var hr) ? hr : null };
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
    public int DisplayRequests { get; private set; }
    public int ActiveRequests { get; private set; }

    public event Action? Suspending;
    public event Action? Resumed;

    public IDisposable RequestDisplayOn()
    {
        DisplayRequests++;
        ActiveRequests++;
        return new Release(this);
    }

    public void Sleep() => Suspending?.Invoke();

    public void Wake() => Resumed?.Invoke();

    public void Dispose() { }

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
    private static readonly CaptureDevice A = new("usb:A", "Card A", "link-a", null, null);
    private static readonly CaptureDevice B = new("usb:B", "Card B", "link-b", null, null);

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

        public Harness(string? preferred = null, params CaptureDevice[] plugged)
        {
            List.Devices.AddRange(plugged);
            Catalog = new DeviceCatalog(List, Time, List, preferred);
            Controller = new CaptureController(Catalog, Factory, Time, Power);
            Controller.StateChanged += States.Add;
            Controller.FrameArrived += (in VideoFrame _) => Frames++;
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
            Time.Advance(0.5);
        }

        public void Unplug(CaptureDevice d)
        {
            List.Devices.Remove(d);
            List.Raise();
            Time.Advance(0.5);
        }

        public void Dispose() => Controller.Dispose();
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
        Assert.Equal(new LiveInfo("Card A", 1920, 1080, 60, "NV12"), live.Info);
        Assert.Equal(1, h.Frames); // frames pass straight through
        Assert.Equal(A, h.Controller.CurrentDevice);
        Assert.NotNull(h.Controller.CurrentType);
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

    [Fact]
    public void PrivacyDeniedPausesThenResumesWhenAllowed()
    {
        using var h = new Harness(null, A);
        h.Factory.OpenErrors["usb:A"] = MfErrors.AccessDenied;
        h.Controller.Start();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Paused(PauseReason.PrivacyToggle), h.State);

        h.Time.Advance(10); // still denied: keeps polling without flashing Connecting
        Assert.Equal([typeof(CaptureState.Connecting), typeof(CaptureState.Paused)], h.States.Select(s => s.GetType()));
        Assert.True(h.Factory.Sessions.Count >= 3);

        h.Factory.OpenErrors.Clear();
        h.Time.Advance(3.1);
        Assert.IsType<CaptureState.Connecting>(h.State);
        h.GoLive();
    }

    [Theory]
    [InlineData(MfErrors.DevicePreempted)]
    [InlineData(MfErrors.SharingViolation)]
    [InlineData(MfErrors.HwMftFailedStartStreaming)]
    public void InUsePausesThenResumesWhenReleased(int hr)
    {
        using var h = new Harness(null, A);
        h.Factory.OpenErrors["usb:A"] = hr;
        h.Controller.Start();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Paused(PauseReason.InUseByAnotherApp), h.State);

        h.Factory.OpenErrors.Clear();
        h.Time.Advance(3.1);
        h.GoLive();
    }

    [Fact]
    public void PreemptionWhileLivePausesAndRecovers()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Factory.Last.Fail(MfErrors.DevicePreempted);
        h.Time.Settle();
        Assert.Equal(new CaptureState.Paused(PauseReason.InUseByAnotherApp), h.State);
        Assert.True(h.Factory.Sessions[0].Stopped);
        h.Time.Advance(3.1);
        h.GoLive();
    }

    [Fact]
    public void NoFirstFrameRestartsWithBackoff()
    {
        using var h = new Harness(null, A);
        h.Controller.Start();
        h.Time.Advance(5.6); // first-frame timeout is 5 s, checked every 0.5 s
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.NoFrames), h.State);
        Assert.Single(h.Factory.Sessions);
        Assert.True(h.Factory.Sessions[0].Stopped);

        h.Time.Advance(0.3); // 0.25 s backoff
        Assert.Equal(2, h.Factory.Sessions.Count);
        Assert.IsType<CaptureState.Connecting>(h.State);
        h.GoLive();
    }

    [Fact]
    public void FrameGapWhileLiveRecovers()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Time.Advance(2); // frames keep coming: fine
        h.Factory.Last.EmitFrame();
        h.Time.Advance(2.9);
        Assert.IsType<CaptureState.Live>(h.State);
        h.Time.Advance(0.7); // past 3 s without a frame (watchdog ticks every 0.5 s)
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.NoFrames), h.State);

        h.Time.Advance(0.3);
        Assert.Equal(2, h.Factory.Sessions.Count);
        h.GoLive();
    }

    [Fact]
    public void RepeatedStallsBackOffUpToFourSeconds()
    {
        using var h = new Harness(null, A);
        h.Controller.Start();
        for (var i = 0; i < 6; i++) h.Time.Advance(5.6 + RecoveryBackoff.RestartDelaySeconds(i + 1));
        Assert.Equal(7, h.Factory.Sessions.Count);
    }

    [Fact]
    public void UnplugMidStreamWaitsThenReplugResumes()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Unplug(A);
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Waiting), h.State);
        Assert.True(h.Factory.Last.Stopped);

        h.Plug(A);
        Assert.IsType<CaptureState.Connecting>(h.State);
        Assert.Equal(2, h.Factory.Sessions.Count);
        h.GoLive();
    }

    [Fact]
    public void SourceReportingInvalidatedBeforeTheWatcherStillDetects()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.List.Devices.Remove(A); // enumeration already misses it, hot-plug event not delivered yet
        h.Factory.Last.Fail(MfErrors.DeviceInvalidated);
        h.Time.Settle();
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Waiting), h.State);
    }

    [Fact]
    public void InvalidatedWhileStillListedRetriesInPlace()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Factory.Last.Fail(MfErrors.Shutdown);
        h.Time.Settle();
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Disconnected), h.State);
        h.Time.Advance(0.3);
        Assert.Equal(2, h.Factory.Sessions.Count);
        h.GoLive();
    }

    [Fact]
    public void UnplugSwitchesToTheOtherCard()
    {
        using var h = new Harness(null, A, B);
        h.StartAndGoLive();
        Assert.Equal(A, h.Controller.CurrentDevice);
        h.Unplug(A);
        Assert.Equal(B, h.Controller.CurrentDevice);
        Assert.IsType<CaptureState.Connecting>(h.State);
        Assert.Equal("usb:B", h.Factory.Last.Device.Key);
        h.GoLive();
    }

    [Fact]
    public void PreferredCardReturningSwitchesBack()
    {
        using var h = new Harness("usb:A", A, B);
        h.StartAndGoLive();
        Assert.Equal(A, h.Controller.CurrentDevice);
        h.Unplug(A);
        h.GoLive();
        Assert.Equal(B, h.Controller.CurrentDevice);

        h.Plug(A);
        Assert.Equal(A, h.Controller.CurrentDevice);
        Assert.Equal("usb:A", h.Factory.Last.Device.Key);
        Assert.True(h.Factory.Sessions[1].Stopped);
        h.GoLive();
    }

    [Fact]
    public void SecondCardPluggedInDoesNotStealTheCurrentOne()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Plug(B);
        Assert.IsType<CaptureState.Live>(h.State);
        Assert.Single(h.Factory.Sessions);
    }

    [Fact]
    public void SelectSwitchesAndRemembersThePreferredCard()
    {
        using var h = new Harness(null, A, B);
        var remembered = new List<string?>();
        h.Catalog.PreferredChanged += remembered.Add;
        h.StartAndGoLive();
        h.Controller.Select("usb:B");
        h.Time.Settle();
        Assert.Equal(B, h.Controller.CurrentDevice);
        Assert.Equal(["usb:B"], remembered);
        h.GoLive();
        h.Unplug(B); // falls back to A, and B coming back returns to B
        h.GoLive();
        h.Plug(B);
        Assert.Equal(B, h.Controller.CurrentDevice);
    }

    [Fact]
    public void SleepPausesAndResumeRebuildsTheReader()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Power.Sleep();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Paused(PauseReason.Sleep), h.State);
        Assert.True(h.Factory.Last.Stopped);
        h.Time.Advance(60); // no polling and no watchdog while asleep
        Assert.Single(h.Factory.Sessions);

        h.Power.Wake();
        h.Time.Settle();
        Assert.IsType<CaptureState.Connecting>(h.State);
        Assert.Equal(2, h.Factory.Sessions.Count);
        h.GoLive();
    }

    [Fact]
    public void CardUnpluggedDuringSleepIsWaitedForOnResume()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Power.Sleep();
        h.Time.Settle();
        h.List.Devices.Remove(A);
        h.Power.Wake();
        h.Time.Settle();
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Waiting), h.State);
        h.Plug(A);
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
        h.Factory.OpenErrors["usb:A"] = MfErrors.NoMoreTypes;
        h.Controller.Start();
        h.Time.Advance(30);
        Assert.IsType<CaptureState.Failed>(h.State);
        Assert.Single(h.Factory.Sessions);

        h.Factory.OpenErrors.Clear();
        h.Controller.Retry();
        h.Time.Settle();
        h.GoLive();
    }

    [Fact]
    public void StopClosesTheCardAndGoesIdle()
    {
        using var h = new Harness(null, A);
        h.StartAndGoLive();
        h.Controller.Stop();
        h.Time.Settle();
        Assert.IsType<CaptureState.Idle>(h.State);
        Assert.True(h.Factory.Last.Stopped);
        Assert.Equal(0, h.Power.ActiveRequests);
    }

    [Theory]
    [InlineData(MfErrors.AccessDenied, CaptureErrorKind.PrivacyBlocked)]
    [InlineData(MfErrors.DevicePreempted, CaptureErrorKind.InUse)]
    [InlineData(MfErrors.VideoDeviceLocked, CaptureErrorKind.InUse)]
    [InlineData(MfErrors.DeviceInvalidated, CaptureErrorKind.Gone)]
    [InlineData(MfErrors.DeviceRemoved, CaptureErrorKind.Gone)]
    [InlineData(0x12345678, CaptureErrorKind.Other)]
    public void ClassifiesHresults(int hr, CaptureErrorKind expected) => Assert.Equal(expected, CaptureErrorMap.Classify(hr));
}

public sealed class DeviceCatalogTests
{
    private static VideoDevice Video(string name, string link, int vid, int pid, string? serial, Guid? container) =>
        new(name, link, new UsbDetails("id", "usb", vid, pid, serial, container, null, null, null));

    [Fact]
    public void PairsAudioByContainerAndKeysByVidPidAndSerial()
    {
        var c1 = Guid.NewGuid();
        var c2 = Guid.NewGuid();
        var devices = DeviceCatalog.Build(
            [Video("UGREEN", "b", 0x1234, 0x5678, "S1", c1), Video("Other", "a", 0x1, 0x2, null, c2)],
            [new AudioDevice("Mic 2", "ep2", "", c2), new AudioDevice("Mic 1", "ep1", "", c1), new AudioDevice("Loose", "ep3")]);
        Assert.Equal(2, devices.Count);
        Assert.Equal("usb:0001:0002|name:Other", devices[0].Key);
        Assert.Equal("Mic 2", devices[0].Audio?.Name);
        Assert.Equal("usb:1234:5678|serial:S1", devices[1].Key);
        Assert.Equal("Mic 1", devices[1].Audio?.Name);
    }

    [Fact]
    public void IdenticalCardsGetDistinctKeys()
    {
        var devices = DeviceCatalog.Build(
            [Video("Card", "link-2", 1, 2, null, null), Video("Card", "link-1", 1, 2, null, null)], []);
        Assert.Equal(["usb:0001:0002|name:Card", "usb:0001:0002|name:Card#2"], devices.Select(d => d.Key));
        Assert.Equal("link-1", devices[0].SymbolicLink);
    }

    [Fact]
    public void WithoutUsbDetailsTheNameIsTheKey()
    {
        var devices = DeviceCatalog.Build([new VideoDevice("Integrated Camera", "x", null)], []);
        Assert.Equal("name:Integrated Camera", devices[0].Key);
        Assert.Null(devices[0].Audio);
    }

    [Fact]
    public void HotPlugBurstsBecomeOneDebouncedRefresh()
    {
        var time = new VirtualScheduler();
        var list = new FakeList();
        using var catalog = new DeviceCatalog(list, time, list);
        var changes = 0;
        catalog.DevicesChanged += _ => changes++;
        list.Devices.Add(new CaptureDevice("k", "n", "l", null, null));
        for (var i = 0; i < 5; i++) { list.Raise(); time.Advance(0.1); }
        Assert.Equal(0, changes); // still inside the debounce window
        time.Advance(0.5);
        Assert.Equal(1, changes);
        Assert.Single(catalog.Devices);
    }
}
