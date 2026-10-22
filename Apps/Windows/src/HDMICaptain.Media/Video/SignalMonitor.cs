using System.Diagnostics;
using HDMICaptain.Core;

namespace HDMICaptain.Media.Video;

/// <summary>
/// Watches the picture for "no signal" (spec §13.6, task 096). The card says nothing over MF when HDMI drops
/// (windows.md §2d): it keeps sending its own black or blue screen. Four times a second this samples a 160×90
/// Y/Cb/Cr grid straight from the frame planes on the source thread (14 400 reads, no GPU readback), classifies
/// it with <see cref="SignalAnalyzer"/> and debounces with <see cref="SignalDebouncer"/>.
/// </summary>
public sealed class SignalMonitor
{
    private const double IntervalSeconds = 0.25;
    private const int Samples = SignalAnalyzer.GridWidth * SignalAnalyzer.GridHeight;

    private readonly SignalDebouncer _debouncer = new();
    private readonly int[] _y = new int[Samples];
    private readonly int[] _cb = new int[Samples];
    private readonly int[] _cr = new int[Samples];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last = double.NegativeInfinity;

    /// <summary>The debounced content changed (source thread).</summary>
    public event Action<SignalContent>? Changed;

    public SignalContent Current => _debouncer.Current;

    public void Reset()
    {
        _debouncer.Reset();
        _last = double.NegativeInfinity;
    }

    /// <summary>Call for every frame on the source thread; it only samples every 250 ms.</summary>
    public void Observe(in VideoFrame frame)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _last < IntervalSeconds) return;
        _last = now;
        if (!Sample(frame, _y, _cb, _cr)) return;
        var content = SignalAnalyzer.Classify(SignalAnalyzer.ComputeStats(_y, _cb, _cr), frame.FullRange ?? false);
        if (_debouncer.Observe(content, now)) Changed?.Invoke(_debouncer.Current);
    }

    /// <summary>Fills the grid from the frame's planes; false for layouts without YCbCr (BGRA).</summary>
    public static unsafe bool Sample(in VideoFrame frame, Span<int> y, Span<int> cb, Span<int> cr)
    {
        if (frame.Format == PixelFormat.Bgra || frame.Width < SignalAnalyzer.GridWidth || frame.Height < SignalAnalyzer.GridHeight) return false;
        var (chromaW, chromaH) = frame.ChromaSize;
        var i = 0;
        for (var gy = 0; gy < SignalAnalyzer.GridHeight; gy++)
        {
            var py = (int)((gy + 0.5) * frame.Height / SignalAnalyzer.GridHeight);
            var cy = py * chromaH / frame.Height;
            for (var gx = 0; gx < SignalAnalyzer.GridWidth; gx++, i++)
            {
                var px = (int)((gx + 0.5) * frame.Width / SignalAnalyzer.GridWidth);
                var cx = px * chromaW / frame.Width;
                switch (frame.Format)
                {
                    case PixelFormat.Nv12:
                        y[i] = ((byte*)frame.Plane0)[(long)py * frame.Stride0 + px];
                        var uv = (byte*)frame.Plane1 + (long)cy * frame.Stride1 + cx * 2;
                        cb[i] = uv[0];
                        cr[i] = uv[1];
                        break;
                    case PixelFormat.Yuy2:
                        // Y0 Cb Y1 Cr per pixel pair.
                        var pair = (byte*)frame.Plane0 + (long)py * frame.Stride0 + cx * 4;
                        y[i] = pair[(px & 1) * 2];
                        cb[i] = pair[1];
                        cr[i] = pair[3];
                        break;
                    default: // planar 4:2:0 / 4:2:2 / 4:4:4
                        y[i] = ((byte*)frame.Plane0)[(long)py * frame.Stride0 + px];
                        cb[i] = ((byte*)frame.Plane1)[(long)cy * frame.Stride1 + cx];
                        cr[i] = ((byte*)frame.Plane2)[(long)cy * frame.Stride2 + cx];
                        break;
                }
            }
        }
        return true;
    }
}
