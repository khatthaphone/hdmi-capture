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

    private static void AssertPixel(byte[] rgba, int width, int x, int y, int r, int g, int b)
    {
        var i = (y * width + x) * 4;
        var actual = (rgba[i], rgba[i + 1], rgba[i + 2]);
        Assert.True(Math.Abs(actual.Item1 - r) <= 2 && Math.Abs(actual.Item2 - g) <= 2 && Math.Abs(actual.Item3 - b) <= 2,
            $"pixel ({x},{y}) = {actual}, expected ({r},{g},{b})");
    }

    /// <summary>libhcrender_testctx: an EGL surfaceless GLES 3 context with a w×h framebuffer, current on this thread.</summary>
    private sealed partial class HeadlessGl : IDisposable
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
