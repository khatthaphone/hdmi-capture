using System.Runtime.InteropServices;

namespace HDMICaptain.Linux.Media.Render;

/// <summary>
/// The picture recordings get (task 120): a second shared renderer on its own thread and headless GL context
/// (EGL surfaceless), fed the same source frames as the window. It applies the window's look like pass 1 does for
/// screenshots and packs each frame as limited-range NV12 on the GPU for the encoder. It needs no window, so a
/// minimised or hidden viewer keeps recording. Frames in from the capture thread; <see cref="Packed"/> runs on this
/// renderer's thread; the newest frame wins if the GPU falls behind (counted in <see cref="Skipped"/>).
/// </summary>
public sealed unsafe partial class RecordingRenderer : IDisposable
{
    private const string Lib = "hcrender";

    /// <summary>One packed NV12 frame (BT.709 from 720 lines up, else BT.601, limited range). Valid during the call.</summary>
    public delegate void PackedHandler(ReadOnlySpan<byte> nv12, int width, int height, long timestampNs);

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stop;
    private long _pushed;

    public RecordingRenderer()
    {
        Renderer.FramePublished += () => _wake.Set();
        _thread = new Thread(Run) { Name = "Recording renderer", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Take the look and colour options from here (SetLook / SetOptions), as for the window's renderer.</summary>
    public GlesRenderer Renderer { get; } = new();

    public PackedHandler? Packed;

    public string? GpuName { get; private set; }
    public long PackedFrames { get; private set; }
    /// <summary>Frames that arrived while the GPU was still on an older one (newest wins).</summary>
    public long Skipped => Math.Max(0, Interlocked.Read(ref _pushed) - PackedFrames - Unpackable);
    public long Unpackable { get; private set; }

    /// <summary>The GPU pack needs a width that is a multiple of 4 and an even height.</summary>
    public static bool Supports(int width, int height) => width >= 4 && height >= 2 && width % 4 == 0 && height % 2 == 0;

    /// <summary>Waits until the headless context is up; false when EGL couldn't make one (record the raw source then).</summary>
    public bool WaitReady(int milliseconds = 3000) => _ready.Task.Wait(milliseconds) && _ready.Task.Result;

    /// <summary>Capture thread: copies a decoded source frame in (the same layout the window's renderer takes).</summary>
    public void Push(ReadOnlySpan<byte> frame, RenderPixelFormat format, int width, int height, long timestampNs, bool fullRange, FrameMatrix matrix)
    {
        var slot = Renderer.Acquire(format, width, height);
        if (slot.IsEmpty || frame.Length < slot.Length) return;
        frame[..slot.Length].CopyTo(slot);
        Renderer.Publish(timestampNs, fullRange, matrix);
        Interlocked.Increment(ref _pushed);
    }

    private void Run()
    {
        var context = hc_offscreen_create();
        if (context == 0)
        {
            LinuxLog.Write("recording renderer: no headless GL context");
            _ready.SetResult(false);
            return;
        }
        try
        {
            GpuName = Marshal.PtrToStringUTF8(hc_offscreen_renderer_name(context));
            if (!Renderer.GlInit())
            {
                LinuxLog.Write("recording renderer: GL init failed");
                _ready.SetResult(false);
                return;
            }
            LinuxLog.Write($"recording renderer: {GpuName}");
            _ready.SetResult(true);
            byte[]? buffer = null;
            while (!_stop)
            {
                _wake.WaitOne(200);
                var stats = Renderer.Stats;
                while (!_stop)
                {
                    // Size the buffer for the newest frame; a frame larger than expected is reported as unpackable once.
                    var need = Math.Max(stats.Width * stats.Height * 3 / 2, 1920 * 1080 * 3 / 2);
                    if (buffer == null || buffer.Length < need) buffer = new byte[need];
                    int w, h;
                    long ts;
                    int n;
                    fixed (byte* p = buffer) n = hc_renderer_convert_nv12(Renderer.Handle, p, buffer.Length, &w, &h, &ts);
                    if (n == 0) break;
                    if (n < 0)
                    {
                        Unpackable++;
                        stats = Renderer.Stats;
                        continue;
                    }
                    PackedFrames++;
                    Packed?.Invoke(buffer.AsSpan(0, n), w, h, ts);
                }
            }
            Renderer.GlRelease();
        }
        catch (Exception e)
        {
            LinuxLog.Write($"recording renderer: {e.GetType().Name}: {e.Message}");
            _ready.TrySetResult(false);
        }
        finally
        {
            hc_offscreen_destroy(context);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread) _thread.Join(2000);
        Renderer.Dispose();
        _wake.Dispose();
    }

    [LibraryImport(Lib)] private static partial nint hc_offscreen_create();
    [LibraryImport(Lib)] private static partial nint hc_offscreen_renderer_name(nint o);
    [LibraryImport(Lib)] private static partial void hc_offscreen_destroy(nint o);
    [LibraryImport(Lib)] private static partial int hc_renderer_convert_nv12(nint r, byte* output, int capacity, int* width, int* height, long* timestampNs);
}
