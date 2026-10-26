using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Linux.Media.Render;

namespace HDMICaptain.Linux.Media.Tests;

/// <summary>
/// The shared GLES renderer on a headless Mesa context (EGL surfaceless): the same 75 % bar and range
/// checks as the Windows WARP tests, plus geometry against the shared fixture. Each test owns its context.
/// </summary>
public unsafe partial class RendererPixelTests
{
    // BT.709 limited 75 % red (CaptureCore's TestPattern.ycbcr of (0.75, 0, 0)).
    private const byte RedY = 51, RedCb = 109, RedCr = 212;

    [Fact]
    public void Limited709RedConvertsTo75Percent()
    {
        using var gl = new HeadlessGl(1280, 720);
        using var r = new GlesRenderer();
        Assert.True(r.GlInit());
        FillNv12(r, 1280, 720, RedY, RedCb, RedCr, fullRange: false);
        gl.Bind();
        Assert.True(r.GlRender(1280, 720));
        var pass1 = r.GlReadPass1();
        Assert.NotNull(pass1);
        AssertPixel(pass1.Value.Rgba, 1280, 640, 360, 191, 0, 0);
        r.GlRelease();
    }

    [Fact]
    public void TheLookIsBakedIntoTheScreenshotSource()
    {
        // Saturation 0 turns 75 % red into its BT.709 luma (0.2126 × 191 ≈ 41) in pass 1, as on Windows/Mac.
        using var gl = new HeadlessGl(1280, 720);
        using var r = new GlesRenderer();
        Assert.True(r.GlInit());
        r.SetLook(new HDMICaptain.Core.ImageAdjustments { Saturation = 0 });
        FillNv12(r, 1280, 720, RedY, RedCb, RedCr, fullRange: false);
        gl.Bind();
        r.GlRender(1280, 720);
        AssertPixel(r.GlReadPass1()!.Value.Rgba, 1280, 640, 10, 41, 41, 41);
        AssertPixel(gl.Read(), 1280, 640, 10, 41, 41, 41); // and on screen, applied once
        r.SetLook(new HDMICaptain.Core.ImageAdjustments());
        FillNv12(r, 1280, 720, RedY, RedCb, RedCr, fullRange: false);
        gl.Bind();
        r.GlRender(1280, 720);
        AssertPixel(r.GlReadPass1()!.Value.Rgba, 1280, 640, 10, 191, 0, 0);
        r.GlRelease();
    }

    [Fact]
    public void FullRangeFlagChangesTheMaths()
    {
        using var gl = new HeadlessGl(1280, 720);
        using var r = new GlesRenderer();
        Assert.True(r.GlInit());
        // Y=16 is black in limited range but dark grey in full range.
        FillNv12(r, 1280, 720, 16, 128, 128, fullRange: true);
        gl.Bind();
        r.GlRender(1280, 720);
        AssertPixel(r.GlReadPass1()!.Value.Rgba, 1280, 10, 10, 16, 16, 16);
        r.SetOptions(ScaleMode.Fit, FilterMode.Linear, ColorRange.Limited);
        FillNv12(r, 1280, 720, 16, 128, 128, fullRange: true);
        gl.Bind();
        r.GlRender(1280, 720);
        AssertPixel(r.GlReadPass1()!.Value.Rgba, 1280, 10, 10, 0, 0, 0);
        r.GlRelease();
    }

    [Fact]
    public void FitPillarboxesIntoAWideTarget()
    {
        // 1280×720 into 1600×720: 1280 wide picture centred, 160 px black each side.
        using var gl = new HeadlessGl(1600, 720);
        using var r = new GlesRenderer();
        Assert.True(r.GlInit());
        FillNv12(r, 1280, 720, 235, 128, 128, fullRange: false);
        gl.Bind();
        r.GlRender(1600, 720);
        var px = gl.Read();
        AssertPixel(px, 1600, 80, 360, 0, 0, 0);
        AssertPixel(px, 1600, 800, 360, 255, 255, 255);
        AssertPixel(px, 1600, 1520, 360, 0, 0, 0);
        r.SetOptions(ScaleMode.Stretch, FilterMode.Linear, ColorRange.Auto);
        FillNv12(r, 1280, 720, 235, 128, 128, fullRange: false);
        gl.Bind();
        r.GlRender(1600, 720);
        AssertPixel(gl.Read(), 1600, 80, 360, 255, 255, 255);
        r.GlRelease();
    }

    [Fact]
    public void SmpteBarsPatternReachesPass1()
    {
        using var gl = new HeadlessGl(1280, 720);
        using var r = new GlesRenderer();
        Assert.True(r.GlInit());
        r.StartPattern(TestPatternKind.SmpteBars, 1280, 720, 60);
        var deadline = Environment.TickCount64 + 2000;
        while (r.Stats.FramesIn == 0 && Environment.TickCount64 < deadline) Thread.Sleep(5);
        gl.Bind();
        Assert.True(r.GlRender(1280, 720));
        r.StopPattern();
        var shot = r.GlReadPass1()!.Value;
        // Top section: 7 bars at 75 %: white, yellow, cyan, green, magenta, red, blue.
        int[][] bars = [[191, 191, 191], [191, 191, 0], [0, 191, 191], [0, 191, 0], [191, 0, 191], [191, 0, 0], [0, 0, 191]];
        for (var i = 0; i < 7; i++)
            AssertPixel(shot.Rgba, 1280, (int)((i + 0.5) * 1280 / 7), 100, bars[i][0], bars[i][1], bars[i][2]);
        r.GlRelease();
    }

    // The recording path (task 120) runs on its own thread and headless context. It lives in this class so it never
    // overlaps the other pixel tests' context (which terminates the shared EGL display when it's destroyed).
    [Fact]
    public void RecordingsGetLimitedNv12BackFromTheGpu()
    {
        var nv12 = PackOnce(1280, 720, RedY, RedCb, RedCr, null);
        AssertNv12(nv12, 1280, 720, 640, 360, RedY, RedCb, RedCr);
    }

    [Fact]
    public void RecordingsCarryTheLook()
    {
        // Saturation 0: 75 % red becomes grey 41/255, i.e. Y 51 with neutral chroma, in the encoder's frames too.
        var nv12 = PackOnce(1280, 720, RedY, RedCb, RedCr, new ImageAdjustments { Saturation = 0 });
        AssertNv12(nv12, 1280, 720, 100, 100, 51, 128, 128);
    }

    [Fact]
    public void RecordingsSkipWidthsTheGpuPackCannotHold()
    {
        Assert.True(RecordingRenderer.Supports(1920, 1080));
        Assert.False(RecordingRenderer.Supports(1366, 768));
        Assert.False(RecordingRenderer.Supports(720, 481));
    }

    private static byte[] PackOnce(int w, int h, byte y, byte cb, byte cr, ImageAdjustments? look)
    {
        using var gpu = new RecordingRenderer();
        Assert.True(gpu.WaitReady());
        if (look != null) gpu.Renderer.SetLook(look);
        byte[]? result = null;
        using var done = new ManualResetEventSlim();
        gpu.Packed = (frame, fw, fh, _) =>
        {
            Assert.Equal((w, h), (fw, fh));
            result ??= frame.ToArray();
            done.Set();
        };
        var source = new byte[w * h * 3 / 2];
        source.AsSpan(0, w * h).Fill(y);
        for (var i = w * h; i < source.Length; i += 2)
        {
            source[i] = cb;
            source[i + 1] = cr;
        }
        gpu.Push(source, RenderPixelFormat.Nv12, w, h, GlesRenderer.MonotonicNs, false, FrameMatrix.Auto);
        Assert.True(done.Wait(5000), "no packed frame");
        return result!;
    }

    private static void AssertNv12(byte[] nv12, int w, int h, int x, int y, int expectY, int expectCb, int expectCr)
    {
        var luma = nv12[y * w + x];
        var c = w * h + y / 2 * w + x / 2 * 2;
        Assert.True(Math.Abs(luma - expectY) <= 1 && Math.Abs(nv12[c] - expectCb) <= 1 && Math.Abs(nv12[c + 1] - expectCr) <= 1,
            $"({x},{y}) = Y{luma} Cb{nv12[c]} Cr{nv12[c + 1]}, expected Y{expectY} Cb{expectCb} Cr{expectCr}");
    }

    private static void FillNv12(GlesRenderer r, int w, int h, byte y, byte cb, byte cr, bool fullRange)
    {
        var span = r.Acquire(RenderPixelFormat.Nv12, w, h);
        span[..(w * h)].Fill(y);
        var chroma = span[(w * h)..];
        for (var i = 0; i < chroma.Length; i += 2)
        {
            chroma[i] = cb;
            chroma[i + 1] = cr;
        }
        r.Publish(GlesRenderer.MonotonicNs, fullRange);
    }

    internal static void AssertPixel(byte[] rgba, int width, int x, int y, int r, int g, int b)
    {
        var i = (y * width + x) * 4;
        var actual = (rgba[i], rgba[i + 1], rgba[i + 2]);
        Assert.True(Math.Abs(actual.Item1 - r) <= 2 && Math.Abs(actual.Item2 - g) <= 2 && Math.Abs(actual.Item3 - b) <= 2,
            $"pixel ({x},{y}) = {actual}, expected ({r},{g},{b})");
    }

    /// <summary>libhcrender_testctx: an EGL surfaceless GLES 3 context with a w×h framebuffer, current on this thread.</summary>
    internal sealed partial class HeadlessGl : IDisposable
    {
        private readonly int _w, _h;

        public HeadlessGl(int width, int height)
        {
            (_w, _h) = (width, height);
            Assert.NotEqual(0u, hc_test_context_create(width, height));
        }

        public void Bind() => hc_test_bind();

        /// <summary>Framebuffer pixels, flipped to top row first like pass 1.</summary>
        public byte[] Read()
        {
            var raw = new byte[_w * _h * 4];
            fixed (byte* p = raw) hc_test_read(_w, _h, p);
            var flipped = new byte[raw.Length];
            for (var row = 0; row < _h; row++)
                Array.Copy(raw, row * _w * 4, flipped, (_h - 1 - row) * _w * 4, _w * 4);
            return flipped;
        }

        public void Dispose() => hc_test_context_destroy();

        [LibraryImport("hcrender_testctx")] private static partial uint hc_test_context_create(int width, int height);
        [LibraryImport("hcrender_testctx")] private static partial void hc_test_bind();
        [LibraryImport("hcrender_testctx")] private static partial void hc_test_read(int width, int height, byte* rgba);
        [LibraryImport("hcrender_testctx")] private static partial void hc_test_context_destroy();
    }
}
