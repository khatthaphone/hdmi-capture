using System.Diagnostics.Tracing;

namespace HDMICaptain.Media.Diagnostics;

/// <summary>
/// ETW events for the frame path (task 101), viewable in WPA or PerfView, or with
/// <c>dotnet-trace collect -n HDMICaptain --providers HDMICaptain-Media</c>. Each call site checks
/// <see cref="EventSource.IsEnabled()"/> first, so a session that isn't tracing pays one branch per frame.
/// </summary>
[EventSource(Name = "HDMICaptain-Media")]
public sealed class MediaEvents : EventSource
{
    public static readonly MediaEvents Log = new();

    private MediaEvents() { }

    /// <summary>A sample arrived from Media Foundation (100 ns capture timestamp).</summary>
    [Event(1, Level = EventLevel.Verbose)]
    public void FrameCaptured(long timestamp) => WriteEvent(1, timestamp);

    /// <summary>An MJPEG frame was decoded by libjpeg-turbo.</summary>
    [Event(2, Level = EventLevel.Verbose)]
    public void FrameDecoded(long timestamp, double milliseconds) => WriteEvent(2, timestamp, milliseconds);

    /// <summary>Upload and conversion (pass 1) finished.</summary>
    [Event(3, Level = EventLevel.Verbose)]
    public void FrameConverted(long timestamp, double milliseconds) => WriteEvent(3, timestamp, milliseconds);

    /// <summary>Scale and Present (pass 2) finished on every visible target.</summary>
    [Event(4, Level = EventLevel.Verbose)]
    public void FramePresented(long timestamp, double milliseconds) => WriteEvent(4, timestamp, milliseconds);

    /// <summary>Capture state changed (controller).</summary>
    [Event(10, Level = EventLevel.Informational)]
    public void CaptureState(string state) => WriteEvent(10, state);
}
