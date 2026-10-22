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

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void LutAppliesAtItsStrength(double strength)
    {
        using var renderer = D3DRenderer.CreateWarp();
        const int size = 2;
        // Inverting LUT: out = 1 - in, red fastest as in .cube files.
        var rgb = new float[size * size * size * 3];
        for (var b = 0; b < size; b++)
        for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++)
        {
            var i = ((b * size + g) * size + r) * 3;
            (rgb[i], rgb[i + 1], rgb[i + 2]) = (1 - r, 1 - g, 1 - b);
        }
        renderer.SetLut(size, rgb, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, (float)strength);
        var frame = TestPattern.Pack(TestPatternKind.SmpteBars, PixelFormat.Nv12, Width, Height).AsFrame();

        renderer.Render(in frame);
        var pixels = renderer.ReadSourcePixels()!;

        for (var bar = 0; bar < 7; bar++)
        {
            var (r, g, b) = TestPattern.FullBarColors[bar];
            int Expected(double c) => (int)Math.Round((c * 0.75 * (1 - strength) + (1 - c * 0.75) * strength) * 255);
            AssertPixel(pixels, Width, (int)((bar + 0.5) * Width / 7), Height / 3, Expected(r), Expected(g), Expected(b), $"strength {strength} bar {bar}");
        }
    }

    public static TheoryData<string> Looks() => [.. LookPreset.All.Select(l => l.Title), "Extreme"];

    /// <summary>
    /// Task 097: every Picture Look on the 75 % bars matches MetalRenderer's adjustColor (Shaders in MetalRenderer.swift,
    /// ported line for line below), so the same look gives the same picture on both platforms.
    /// </summary>
    [Theory]
    [MemberData(nameof(Looks))]
    public void PictureLooksMatchTheMacFormula(string title)
    {
        var values = LookPreset.All.FirstOrDefault(l => l.Title == title)?.Values
            ?? new ImageAdjustments { Brightness = 0.1, Contrast = 1.4, Saturation = 0.3, Gamma = 1.6 };
        using var renderer = D3DRenderer.CreateWarp();
        renderer.Adjustments = ((float)values.Brightness, (float)values.Contrast, (float)values.Saturation, (float)values.Gamma, (float)values.Sharpness);
        var frame = TestPattern.Pack(TestPatternKind.SmpteBars, PixelFormat.Nv12, Width, Height).AsFrame();

        renderer.Render(in frame);
        var pixels = renderer.ReadSourcePixels()!;

        for (var bar = 0; bar < 7; bar++)
        {
            var (r, g, b) = TestPattern.FullBarColors[bar];
            var (er, eg, eb) = MacAdjust((r * 0.75, g * 0.75, b * 0.75), values);
            AssertPixel(pixels, Width, (int)((bar + 0.5) * Width / 7), Height / 3,
                (int)Math.Round(er * 255), (int)Math.Round(eg * 255), (int)Math.Round(eb * 255), $"{title} bar {bar}");
        }
    }

    private static (double R, double G, double B) MacAdjust((double R, double G, double B) rgb, ImageAdjustments a)
    {
        static double Clamp(double v) => Math.Clamp(v, 0, 1);
        var (r, g, b) = ((rgb.R - 0.5) * a.Contrast + 0.5 + a.Brightness, (rgb.G - 0.5) * a.Contrast + 0.5 + a.Brightness, (rgb.B - 0.5) * a.Contrast + 0.5 + a.Brightness);
        var luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        (r, g, b) = (Clamp(luma + (r - luma) * a.Saturation), Clamp(luma + (g - luma) * a.Saturation), Clamp(luma + (b - luma) * a.Saturation));
        var inverse = 1 / Math.Max(0.1, a.Gamma);
        return (Math.Pow(r, inverse), Math.Pow(g, inverse), Math.Pow(b, inverse));
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

    [Fact]
    public void GridDrawsEachCardIntoItsCell()
    {
        using var grid = GridRenderer.CreateWarp();
        grid.TileCount = 2;
        var white = Uniform(235, out var keepAlive, fullRange: false);

        grid.Render(0, in white);
        var pixels = grid.ReadComposedPixels(66, 32);

        AssertPixel(pixels, 66, 16, 16, 255, 255, 255, "card 1");
        AssertPixel(pixels, 66, 32, 16, 0, 0, 0, "gap");
        AssertPixel(pixels, 66, 50, 16, 0, 0, 0, "card 2 before its first frame");

        grid.ClearTile(0);
        AssertPixel(grid.ReadComposedPixels(66, 32), 66, 16, 16, 0, 0, 0, "card 1 after it stopped");
        GC.KeepAlive(keepAlive);
    }

    [Fact]
    public void GridPresentsFromItsComposeThread()
    {
        var grid = GridRenderer.CreateWarp();
        var attached = 0;
        var target = grid.CreateCompositionTarget(640, 360, _ => attached++);
        grid.TileCount = 3;
        var frame = TestPattern.Pack(TestPatternKind.Grid, PixelFormat.Nv12, 320, 180).AsFrame();

        grid.Render(2, in frame);
        target.Resize(800, 600);
        grid.Redraw();
        Thread.Sleep(300);
        grid.Dispose();

        Assert.Equal(1, attached);
        Assert.Equal((800, 600), (target.Width, target.Height));
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
