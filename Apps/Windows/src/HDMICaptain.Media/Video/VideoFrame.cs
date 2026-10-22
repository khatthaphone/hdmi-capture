using HDMICaptain.Core;

namespace HDMICaptain.Media.Video;

/// <summary>CPU pixel layouts the renderer uploads. Planar 4:2:0/4:2:2/4:4:4 is what the JPEG decoder produces.</summary>
public enum PixelFormat { Nv12, Yuy2, I420, I422, I444, Bgra }

/// <summary>
/// One frame's planes, borrowed from the source. Only valid during the <see cref="FrameHandler"/> call
/// (MF sample buffers are locked just for that long), so consumers copy or upload before returning.
/// </summary>
public readonly struct VideoFrame
{
    public required PixelFormat Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required nint Plane0 { get; init; }
    public required int Stride0 { get; init; }
    public nint Plane1 { get; init; }
    public int Stride1 { get; init; }
    public nint Plane2 { get; init; }
    public int Stride2 { get; init; }
    /// <summary>Null when the source doesn't say; <see cref="ColorRange.Auto"/> then means limited.</summary>
    public bool? FullRange { get; init; }
    public ColorMatrix Matrix { get; init; }
    /// <summary>Capture time in 100 ns units (MF sample time or QPC-based).</summary>
    public long Timestamp { get; init; }

    /// <summary>Chroma plane size in texels (YUY2: one RGBA texel per pixel pair).</summary>
    public (int Width, int Height) ChromaSize => Format switch
    {
        PixelFormat.Nv12 or PixelFormat.I420 => ((Width + 1) / 2, (Height + 1) / 2),
        PixelFormat.I422 or PixelFormat.Yuy2 => ((Width + 1) / 2, Height),
        _ => (Width, Height),
    };
}

public delegate void FrameHandler(in VideoFrame frame);

/// <summary>A compressed frame (one MJPEG image), borrowed for the duration of the call.</summary>
public delegate void CompressedFrameHandler(ReadOnlySpan<byte> data, long timestamp);

/// <summary>A frame producer: a capture card (MF) or a generated test pattern. Frames arrive on the source's own thread.</summary>
public interface ICaptureSource : IDisposable
{
    event FrameHandler? FrameArrived;
    void Start();
    void Stop();
}
