using HDMICaptain.Core;

namespace HDMICaptain.Linux.Media.Capture;

/// <summary>What a V4L2 stream actually runs at (the driver may round the request).</summary>
public sealed record V4l2Stream(string FourCC, int Width, int Height, int BytesPerLine, double FrameRate, int Buffers);

/// <summary>Counters of a running stream (fps, drops), read from any thread.</summary>
public sealed class V4l2Counters
{
    public long Frames;        // handed to the frame handler
    public long Skipped;       // ready buffers requeued unseen because a newer one was ready (newest wins)
    public long Empty;         // bytesused == 0 or V4L2_BUF_FLAG_ERROR (dropped)
    public long SequenceGaps;  // frames the driver numbered but never delivered
    public long LastTimestampNs;
}

/// <summary>
/// One capture node streaming on its own thread (task 108): mmap buffers, poll, newest frame wins, and the
/// buffer goes back to the driver as soon as the handler returns. The handler runs on the capture thread and
/// must not keep the pointer. Errors end the stream and are reported once through <see cref="Stopped"/>.
/// </summary>
public sealed unsafe class V4l2Source : IDisposable
{
    public delegate void FrameHandler(byte* data, int length, V4l2Stream stream, long timestampNs);

    private readonly string _path;
    private readonly FrameHandler _onFrame;
    private nint _handle;
    private Thread? _thread;
    private volatile bool _stop;

    public V4l2Source(string path, FrameHandler onFrame)
    {
        _path = path;
        _onFrame = onFrame;
    }

    public V4l2Stream? Stream { get; private set; }
    public V4l2Counters Counters { get; } = new();

    /// <summary>The stream ended by itself: the errno (negative), e.g. -ENODEV when the card was unplugged. Capture thread.</summary>
    public event Action<int>? Stopped;

    /// <summary>Opens and starts streaming; returns 0 or -errno (EBUSY: another app streams the card, EACCES: no permission).</summary>
    public int Start(V4l2Format format, double frameRate, int buffers = 4)
    {
        if (_handle != 0) return -Errno.EBUSY;
        int error;
        _handle = V4l2Native.hc_v4l2_open(_path, &error);
        if (_handle == 0) return error;
        var interval = format.Intervals.Where(i => i.Num > 0).OrderBy(i => Math.Abs((double)i.Den / i.Num - frameRate)).FirstOrDefault();
        V4l2Native.Actual actual;
        var r = V4l2Native.hc_v4l2_start(_handle, V4l2Native.FourCCCode(format.V4l2FourCC), format.Width, format.Height,
            interval.Num, interval.Den, buffers, &actual);
        if (r != 0)
        {
            V4l2Native.hc_v4l2_close(_handle);
            _handle = 0;
            return r;
        }
        Stream = new V4l2Stream(MediaSubtypes.Canonicalize(V4l2Native.FourCCText(actual.FourCC)), actual.Width, actual.Height,
            (int)actual.BytesPerLine, actual.IntervalNum > 0 ? (double)actual.IntervalDen / actual.IntervalNum : 0, actual.BufferCount);
        _stop = false;
        _thread = new Thread(Run) { Name = "v4l2 capture", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        return 0;
    }

    private void Run()
    {
        var stream = Stream!;
        uint? lastSequence = null;
        var error = 0;
        while (!_stop)
        {
            V4l2Native.Buffer buffer;
            var r = V4l2Native.hc_v4l2_dequeue(_handle, 200, &buffer);
            if (r == 0) continue;
            if (r < 0)
            {
                error = r;
                break;
            }
            Counters.Skipped += buffer.Skipped;
            if (lastSequence is { } last && buffer.Sequence > last + 1 + (uint)buffer.Skipped)
                Counters.SequenceGaps += buffer.Sequence - last - 1 - buffer.Skipped;
            lastSequence = buffer.Sequence;
            if (buffer.BytesUsed == 0 || (buffer.Flags & V4l2Native.BufFlagError) != 0)
            {
                Counters.Empty++;
            }
            else
            {
                Counters.LastTimestampNs = buffer.TimestampNs;
                try
                {
                    _onFrame(buffer.Data, (int)buffer.BytesUsed, stream, buffer.TimestampNs);
                }
                catch (Exception e)
                {
                    LinuxLog.Write($"v4l2: frame handler threw {e.GetType().Name}: {e.Message}");
                }
                Interlocked.Increment(ref Counters.Frames);
            }
            r = V4l2Native.hc_v4l2_requeue(_handle, buffer.Index);
            if (r < 0)
            {
                error = r;
                break;
            }
        }
        if (error != 0 && !_stop)
        {
            LinuxLog.Write($"v4l2: {_path} stopped: {Errno.Name(error)}");
            Stopped?.Invoke(error);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(1000);
        _thread = null;
        if (_handle != 0) V4l2Native.hc_v4l2_close(_handle);
        _handle = 0;
    }
}
