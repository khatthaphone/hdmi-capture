using System.Diagnostics.Tracing;

namespace HDMICaptain.Linux.Media;

/// <summary>
/// Stage timings for traces (task 121), like Windows' HDMICaptain-Media: FrameCaptured (V4L2 timestamp → dequeue),
/// FrameDecoded (copy/decode into the renderer slot) and FrameDrawn (arrival → draw). Free while no session listens:
/// <c>dotnet-trace collect -p &lt;pid&gt; --providers HDMICaptain-Linux-Media --duration 00:00:00:10</c>.
/// </summary>
[EventSource(Name = "HDMICaptain-Linux-Media")]
public sealed class MediaEvents : EventSource
{
    public static readonly MediaEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void FrameCaptured(double dequeueMs, int skipped) => WriteEvent(1, dequeueMs, skipped);

    [Event(2, Level = EventLevel.Informational)]
    public void FrameDecoded(string fourCC, double milliseconds) => WriteEvent(2, fourCC, milliseconds);

    [Event(3, Level = EventLevel.Informational)]
    public void FrameDrawn(double arrivalToDrawMs) => WriteEvent(3, arrivalToDrawMs);

    [Event(4, Level = EventLevel.Informational)]
    public void CaptureState(string state) => WriteEvent(4, state);
}
