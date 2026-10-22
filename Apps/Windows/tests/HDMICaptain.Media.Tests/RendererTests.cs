using System.Runtime.CompilerServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;

namespace HDMICaptain.Media.Tests;

/// <summary>
/// D3D11 renderer on WARP: every input layout converts the 75 % bars to the right RGB, and the range
/// switch maps black/white as MetalRenderer does. Pixels are read back from pass 1 (the clean source).
/// </summary>
public class RendererTests
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int Tolerance = 3;

    public static TheoryData<PixelFormat> Formats() =>
        [PixelFormat.Nv12, PixelFormat.Yuy2, PixelFormat.I420, PixelFormat.I422, PixelFormat.I444, PixelFormat.Bgra];

    [Theory]
    [MemberData(nameof(Formats))]
    public void SmpteBarsConvertToSeventyFivePercentColors(PixelFormat format)
    {
        using var renderer = D3DRenderer.CreateWarp();
        var pattern = TestPattern.Pack(TestPatternKind.SmpteBars, format, Width, Height);

        var frame = pattern.AsFrame();
        renderer.Render(in frame);
        var pixels = renderer.ReadSourcePixels()!;

        for (var bar = 0; bar < 7; bar++)
        {
            var x = (int)((bar + 0.5) * Width / 7);
            var y = Height / 3;
            var (r, g, b) = TestPattern.FullBarColors[bar];
            AssertPixel(pixels, Width, x, y, (int)Math.Round(r * 0.75 * 255), (int)Math.Round(g * 0.75 * 255), (int)Math.Round(b * 0.75 * 255), $"{format} bar {bar}");
        }
    }

    [Theory]
    [InlineData(16, false, 0)]
    [InlineData(235, false, 255)]
    [InlineData(16, true, 16)]
    [InlineData(235, true, 235)]
    public void RangeFlagFromSource(byte luma, bool fullRange, int expected)
    {
        using var renderer = D3DRenderer.CreateWarp();
        var frame = Uniform(luma, out var keepAlive, fullRange);

        renderer.Render(in frame);

        AssertPixel(renderer.ReadSourcePixels()!, 16, 8, 8, expected, expected, expected, $"Y={luma} full={fullRange}");
        GC.KeepAlive(keepAlive);
    }

    [Fact]
    public void RangeOverrideBeatsTheSourceFlag()
    {
        using var renderer = D3DRenderer.CreateWarp();
        renderer.Options = new RenderOptions(ColorRange: ColorRange.Full);
        var frame = Uniform(16, out var keepAlive, fullRange: false);

        renderer.Render(in frame);

        AssertPixel(renderer.ReadSourcePixels()!, 16, 8, 8, 16, 16, 16, "forced full range");
        GC.KeepAlive(keepAlive);
    }

    [Fact]
    public void PresentsIntoACompositionSwapChainAndResizes()
    {
        using var renderer = D3DRenderer.CreateWarp();
        var attached = 0;
        var target = renderer.CreateCompositionTarget(640, 360, _ => attached++);
        var frame = TestPattern.Pack(TestPatternKind.Grid, PixelFormat.Nv12, 320, 180).AsFrame();

        renderer.Render(in frame);
        target.Resize(800, 800, compositionScale: 1.5f);
        renderer.Render(in frame);

        Assert.Equal(1, attached);
        Assert.Equal((800, 800), (target.Width, target.Height));
    }

    private static unsafe VideoFrame Uniform(byte luma, out byte[][] planes, bool fullRange)
    {
        const int w = 16, h = 16;
        var y = GC.AllocateArray<byte>(w * h, pinned: true);
        var uv = GC.AllocateArray<byte>(w * h / 2, pinned: true);
        Array.Fill(y, luma);
        Array.Fill(uv, (byte)128);
        planes = [y, uv];
        return new VideoFrame
        {
            Format = PixelFormat.Nv12,
            Width = w,
            Height = h,
            Plane0 = (nint)Unsafe.AsPointer(ref y[0]),
            Stride0 = w,
            Plane1 = (nint)Unsafe.AsPointer(ref uv[0]),
            Stride1 = w,
            FullRange = fullRange,
            Matrix = ColorMatrix.Bt709,
        };
    }

    private static void AssertPixel(byte[] rgba, int width, int x, int y, int r, int g, int b, string what)
    {
        var i = (y * width + x) * 4;
        var actual = (rgba[i], rgba[i + 1], rgba[i + 2]);
        Assert.True(Math.Abs(actual.Item1 - r) <= Tolerance && Math.Abs(actual.Item2 - g) <= Tolerance && Math.Abs(actual.Item3 - b) <= Tolerance,
            $"{what}: expected ({r}, {g}, {b}), got {actual}");
    }
}
