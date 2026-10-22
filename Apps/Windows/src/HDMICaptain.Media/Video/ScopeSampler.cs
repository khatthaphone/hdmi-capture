using System.Diagnostics;
using HDMICaptain.Core;

namespace HDMICaptain.Media.Video;

/// <summary>
/// Feeds the scopes panel (task 097) like the Mac's ScopesModel: while enabled, ten times a second it copies a
/// 256×144 grid of Y/Cb/Cr samples straight from the frame planes on the source thread (no GPU work, no readback)
/// and runs <see cref="ScopeData.Analyze"/> on the thread pool. Reads the clean source only, never the UI.
/// </summary>
public sealed class ScopeSampler
{
    public const int Columns = 256;
    public const int Rows = 144;
    private const double IntervalSeconds = 0.1;

    private readonly byte[] _grid = new byte[Columns * Rows * 3];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last = double.NegativeInfinity;
    private int _busy;

    /// <summary>Off by default: sampling only runs while the panel is open.</summary>
    public volatile bool Enabled;

    /// <summary>New scope data (thread pool).</summary>
    public event Action<ScopeData>? DataReady;

    /// <summary>Call for every frame on the source thread.</summary>
    public void Observe(in VideoFrame frame)
    {
        if (!Enabled) return;
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _last < IntervalSeconds || Volatile.Read(ref _busy) == 1) return;
        _last = now;
        if (!Sample(frame, _grid)) return;
        Volatile.Write(ref _busy, 1);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                DataReady?.Invoke(ScopeData.Analyze(_grid, Columns, Rows));
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }, null);
    }

    /// <summary>Interleaved Y, Cb, Cr for a Columns×Rows grid; false for BGRA frames.</summary>
    public static unsafe bool Sample(in VideoFrame frame, Span<byte> grid)
    {
        if (frame.Format == PixelFormat.Bgra || frame.Width < Columns || frame.Height < Rows) return false;
        var (chromaW, chromaH) = frame.ChromaSize;
        var i = 0;
        for (var gy = 0; gy < Rows; gy++)
        {
            var py = (int)((gy + 0.5) * frame.Height / Rows);
            var cy = py * chromaH / frame.Height;
            for (var gx = 0; gx < Columns; gx++, i += 3)
            {
                var px = (int)((gx + 0.5) * frame.Width / Columns);
                var cx = px * chromaW / frame.Width;
                switch (frame.Format)
                {
                    case PixelFormat.Nv12:
                        grid[i] = ((byte*)frame.Plane0)[(long)py * frame.Stride0 + px];
                        var uv = (byte*)frame.Plane1 + (long)cy * frame.Stride1 + cx * 2;
                        grid[i + 1] = uv[0];
                        grid[i + 2] = uv[1];
                        break;
                    case PixelFormat.Yuy2:
                        var pair = (byte*)frame.Plane0 + (long)py * frame.Stride0 + cx * 4;
                        grid[i] = pair[(px & 1) * 2];
                        grid[i + 1] = pair[1];
                        grid[i + 2] = pair[3];
                        break;
                    default:
                        grid[i] = ((byte*)frame.Plane0)[(long)py * frame.Stride0 + px];
                        grid[i + 1] = ((byte*)frame.Plane1)[(long)cy * frame.Stride1 + cx];
                        grid[i + 2] = ((byte*)frame.Plane2)[(long)cy * frame.Stride2 + cx];
                        break;
                }
            }
        }
        return true;
    }
}
