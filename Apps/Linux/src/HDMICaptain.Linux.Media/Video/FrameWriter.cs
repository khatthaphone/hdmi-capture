using System.Diagnostics;
using HDMICaptain.Linux.Media.Render;

namespace HDMICaptain.Linux.Media.Video;

/// <summary>
/// Puts one captured buffer into the renderer's next frame slot (task 110): NV12/YUYV are copied row by row
/// (V4L2 rows may be padded), MJPEG is decoded by libjpeg-turbo straight into the slot's planes (4:2:0 → I420,
/// 4:2:2 → I422; grayscale gets neutral chroma). Nothing is allocated per frame. One writer per capture thread.
/// </summary>
public sealed unsafe class FrameWriter : IDisposable
{
    private readonly GlesRenderer _renderer;
    private nint _jpeg;
    private readonly Stopwatch _clock = new();

    public FrameWriter(GlesRenderer renderer) => _renderer = renderer;

    /// <summary>A frame as it went to the renderer (decoded planes), on the capture thread: recorders copy it.</summary>
    public delegate void WrittenHandler(ReadOnlySpan<byte> frame, RenderPixelFormat format, int width, int height, long timestampNs,
        bool fullRange, FrameMatrix matrix);

    public WrittenHandler? FrameReady;

    /// <summary>The layout of the last frame written (what a recorder will receive).</summary>
    public (RenderPixelFormat Format, int Width, int Height)? LastLayout { get; private set; }

    private void Publish(Span<byte> slot, RenderPixelFormat format, int width, int height, long ts, bool fullRange, FrameMatrix matrix)
    {
        LastLayout = (format, width, height);
        FrameReady?.Invoke(slot, format, width, height, ts, fullRange, matrix);
        _renderer.Publish(ts, fullRange, matrix);
    }

    public long Written { get; private set; }
    /// <summary>Buffers that were not JPEGs, failed to decode or had an unusable layout (dropped).</summary>
    public long Rejected { get; private set; }
    public long JpegWarnings { get; private set; }
    public string? LastError { get; private set; }
    public double LastMilliseconds { get; private set; }

    /// <summary>
    /// fourCC: the selector name (NV12, YUY2, MJPG). bytesPerLine: V4L2's row pitch (0 = tight).
    /// Returns false when the buffer was dropped.
    /// </summary>
    public bool Write(string fourCC, byte* data, int length, int width, int height, int bytesPerLine, long timestampNs)
    {
        _clock.Restart();
        var ok = fourCC switch
        {
            "NV12" => CopyNv12(data, length, width, height, bytesPerLine, timestampNs),
            "YUY2" => CopyYuy2(data, length, width, height, bytesPerLine, timestampNs),
            "MJPG" => Decode(data, length, timestampNs),
            _ => Fail($"unsupported format {fourCC}"),
        };
        LastMilliseconds = _clock.Elapsed.TotalMilliseconds;
        if (MediaEvents.Log.IsEnabled()) MediaEvents.Log.FrameDecoded(fourCC, LastMilliseconds);
        if (ok) Written++;
        else Rejected++;
        return ok;
    }

    private bool CopyNv12(byte* data, int length, int width, int height, int pitch, long ts)
    {
        pitch = pitch > 0 ? pitch : width;
        if ((width & 1) != 0 || (height & 1) != 0 || length < pitch * height * 3 / 2) return Fail("short NV12 buffer");
        var slot = _renderer.Acquire(RenderPixelFormat.Nv12, width, height);
        if (slot.IsEmpty) return Fail("no slot");
        fixed (byte* dst = slot)
        {
            CopyRows(data, pitch, dst, width, width, height);
            CopyRows(data + pitch * height, pitch, dst + width * height, width, width, height / 2);
        }
        Publish(slot, RenderPixelFormat.Nv12, width, height, ts, false, FrameMatrix.Auto);
        return true;
    }

    private bool CopyYuy2(byte* data, int length, int width, int height, int pitch, long ts)
    {
        pitch = pitch > 0 ? pitch : width * 2;
        if ((width & 1) != 0 || length < pitch * height) return Fail("short YUYV buffer");
        var slot = _renderer.Acquire(RenderPixelFormat.Yuy2, width, height);
        if (slot.IsEmpty) return Fail("no slot");
        fixed (byte* dst = slot) CopyRows(data, pitch, dst, width * 2, width * 2, height);
        Publish(slot, RenderPixelFormat.Yuy2, width, height, ts, false, FrameMatrix.Auto);
        return true;
    }

    private static void CopyRows(byte* src, int srcPitch, byte* dst, int dstPitch, int rowBytes, int rows)
    {
        if (srcPitch == rowBytes && dstPitch == rowBytes)
        {
            Buffer.MemoryCopy(src, dst, (long)rowBytes * rows, (long)rowBytes * rows);
            return;
        }
        for (var y = 0; y < rows; y++) Buffer.MemoryCopy(src + (long)y * srcPitch, dst + (long)y * dstPitch, rowBytes, rowBytes);
    }

    private bool Decode(byte* data, int length, long ts)
    {
        // Some cards send a few non-JPEG buffers right after STREAMON (windows.md §2b): no SOI, drop them.
        if (length < 4 || data[0] != 0xFF || data[1] != 0xD8) return Fail("not a JPEG");
        if (_jpeg == 0)
        {
            _jpeg = TurboJpeg.tjInitDecompress();
            if (_jpeg == 0) return Fail("tjInitDecompress failed");
        }
        int width, height, subsamp, colorspace;
        if (TurboJpeg.tjDecompressHeader3(_jpeg, data, (nuint)length, &width, &height, &subsamp, &colorspace) != 0)
            return Fail($"header: {TurboJpeg.ErrorString(_jpeg)}");
        if ((width & 1) != 0 || (height & 1) != 0) return Fail($"odd JPEG size {width}x{height}");
        var format = subsamp switch
        {
            TurboJpeg.Samp420 or TurboJpeg.SampGray => RenderPixelFormat.I420,
            TurboJpeg.Samp422 => RenderPixelFormat.I422,
            _ => (RenderPixelFormat?)null,
        };
        if (format == null) return Fail($"unsupported JPEG subsampling {subsamp}");
        var slot = _renderer.Acquire(format.Value, width, height);
        if (slot.IsEmpty) return Fail("no slot");
        var chromaHeight = format == RenderPixelFormat.I420 ? height / 2 : height;
        fixed (byte* dst = slot)
        {
            byte* y = dst, cb = dst + width * height, cr = cb + width / 2 * chromaHeight;
            if (subsamp == TurboJpeg.SampGray)
            {
                // Only the Y plane is decoded; neutral chroma makes it grey.
                new Span<byte>(cb, width * chromaHeight).Fill(128);
            }
            var planes = stackalloc byte*[3] { y, cb, cr };
            var strides = stackalloc int[3] { width, width / 2, width / 2 };
            if (TurboJpeg.tjDecompressToYUVPlanes(_jpeg, data, (nuint)length, planes, width, strides, height, 0) != 0)
            {
                if (TurboJpeg.tjGetErrorCode(_jpeg) != TurboJpeg.ErrorWarning) return Fail(TurboJpeg.ErrorString(_jpeg));
                // A warning (premature end, extraneous bytes): the planes are filled; show the frame.
                JpegWarnings++;
            }
        }
        Publish(slot, format.Value, width, height, ts, true, FrameMatrix.Bt601);
        return true;
    }

    private bool Fail(string message)
    {
        LastError = message;
        return false;
    }

    public void Dispose()
    {
        if (_jpeg != 0) TurboJpeg.tjDestroy(_jpeg);
        _jpeg = 0;
    }
}
