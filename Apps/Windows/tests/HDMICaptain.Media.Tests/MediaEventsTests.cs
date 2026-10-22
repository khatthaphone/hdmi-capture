using System.Diagnostics.Tracing;
using HDMICaptain.Core;
using HDMICaptain.Media.Diagnostics;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;

namespace HDMICaptain.Media.Tests;

/// <summary>The renderer reports conversion and present timing through the HDMICaptain-Media EventSource.</summary>
public class MediaEventsTests
{
    [Fact]
    public void RenderEmitsConvertedAndPresented()
    {
        using var listener = new Listener();
        using var renderer = D3DRenderer.CreateWarp();
        var frame = TestPattern.Pack(TestPatternKind.Grid, PixelFormat.Nv12, 320, 180).AsFrame(timestamp: 1234);

        renderer.Render(in frame);

        Assert.Contains(listener.Events, e => e.EventName == nameof(MediaEvents.FrameConverted) && (long)e.Payload![0]! == 1234);
        Assert.Contains(listener.Events, e => e.EventName == nameof(MediaEvents.FramePresented));
    }

    private sealed class Listener : EventListener
    {
        public List<EventWrittenEventArgs> Events { get; } = [];

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "HDMICaptain-Media") EnableEvents(source, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            lock (Events) Events.Add(e);
        }
    }
}
