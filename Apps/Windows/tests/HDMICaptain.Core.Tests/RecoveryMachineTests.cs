namespace HDMICaptain.Core.Tests;

public class RecoveryMachineTests
{
    private static readonly CaptureEvent.Start Card = new("usb:1DE1:F105|name:UGREEN-25854", "UGREEN-25854");
    private static readonly LiveInfo Info = new("UGREEN-25854", 1920, 1080, 60.0, "NV12");

    public static TheoryData<int, double> Delays()
    {
        using var doc = Fixtures.Load("recovery-backoff.json");
        var data = new TheoryData<int, double>();
        foreach (var pair in doc.RootElement.GetProperty("delays").EnumerateArray())
            data.Add(pair[0].GetInt32(), pair[1].GetDouble());
        return data;
    }

    [Theory]
    [MemberData(nameof(Delays))]
    public void SharedBackoffFixture(int attempt, double delay) =>
        Assert.Equal(delay, RecoveryBackoff.RestartDelaySeconds(attempt), 9);

    [Fact]
    public void StartOpensAndFirstFrameGoesLive()
    {
        var m = new RecoveryMachine();
        Assert.Equal(new CaptureAction.Open(), m.Handle(Card));
        Assert.Equal(new CaptureState.Connecting("UGREEN-25854"), m.State);
        m.Handle(new CaptureEvent.FirstFrame(Info));
        Assert.Equal(new CaptureState.Live(Info), m.State);
    }

    [Fact]
    public void ErrorsBackOffAndResetWhenLive()
    {
        var m = new RecoveryMachine();
        m.Handle(Card);
        var delays = Enumerable.Range(1, 6)
            .Select(_ => ((CaptureAction.RetryAfter)m.Handle(new CaptureEvent.StreamError("I/O error"))).DelaySeconds)
            .ToArray();
        Assert.Equal([0.25, 0.5, 1.0, 2.0, 4.0, 4.0], delays);
        Assert.Equal(new CaptureState.Recovering("I/O error"), m.State);
        m.Handle(new CaptureEvent.FirstFrame(Info));
        Assert.Equal(0.25, ((CaptureAction.RetryAfter)m.Handle(new CaptureEvent.Stalled())).DelaySeconds, 9);
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.NoSignal), m.State);
    }

    [Fact]
    public void StaleRetryIsIgnored()
    {
        var m = new RecoveryMachine();
        m.Handle(Card);
        var first = (CaptureAction.RetryAfter)m.Handle(new CaptureEvent.StreamError("a"));
        var second = (CaptureAction.RetryAfter)m.Handle(new CaptureEvent.StreamError("b"));
        Assert.Equal(new CaptureAction.None(), m.Handle(new CaptureEvent.RetryDue(first.Generation)));
        Assert.Equal(new CaptureAction.Open(), m.Handle(new CaptureEvent.RetryDue(second.Generation)));
    }

    [Fact]
    public void UnplugWaitsForTheSameCard()
    {
        var m = new RecoveryMachine();
        m.Handle(Card);
        m.Handle(new CaptureEvent.FirstFrame(Info));
        Assert.Equal(new CaptureAction.None(), m.Handle(new CaptureEvent.Detached("usb:345F:2131|name:other")));
        Assert.Equal(new CaptureAction.Close(), m.Handle(new CaptureEvent.Detached(Card.DeviceKey)));
        Assert.Equal(new CaptureState.Recovering(RecoveryReason.Disconnected), m.State);
        Assert.Equal(new CaptureAction.None(), m.Handle(new CaptureEvent.Attached("usb:345F:2131|name:other")));
        Assert.Equal(new CaptureAction.Open(), m.Handle(new CaptureEvent.Attached(Card.DeviceKey)));
        Assert.Equal(new CaptureState.Connecting("UGREEN-25854"), m.State);
    }

    [Fact]
    public void InterruptionPausesWithoutRetrying()
    {
        var m = new RecoveryMachine();
        m.Handle(Card);
        m.Handle(new CaptureEvent.FirstFrame(Info));
        Assert.Equal(new CaptureAction.Close(), m.Handle(new CaptureEvent.Interrupted(PauseReason.InUseByAnotherApp)));
        Assert.Equal(new CaptureState.Paused(PauseReason.InUseByAnotherApp), m.State);
        Assert.Equal(new CaptureAction.None(), m.Handle(new CaptureEvent.StreamError("late error")));
        Assert.Equal(new CaptureAction.None(), m.Handle(new CaptureEvent.Attached(Card.DeviceKey)));
        Assert.Equal(new CaptureState.Paused(PauseReason.InUseByAnotherApp), m.State);
        Assert.Equal(new CaptureAction.Open(), m.Handle(new CaptureEvent.InterruptionEnded()));
        Assert.Equal(new CaptureState.Connecting("UGREEN-25854"), m.State);
    }

    [Fact]
    public void StopAndFatal()
    {
        var m = new RecoveryMachine();
        m.Handle(Card);
        Assert.Equal(new CaptureAction.Close(), m.Handle(new CaptureEvent.Fatal("Camera access denied")));
        Assert.Equal(new CaptureState.Failed("Camera access denied"), m.State);
        Assert.Equal(new CaptureAction.Open(), m.Handle(Card));
        Assert.Equal(new CaptureAction.Close(), m.Handle(new CaptureEvent.Stop()));
        Assert.Equal(new CaptureState.Idle(), m.State);
        Assert.IsType<CaptureAction.None>(m.Handle(new CaptureEvent.StreamError("after stop")));
    }
}
