using HDMICaptain.Core;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Render;
using HDMICaptain.Media.Video;

namespace HDMICaptain.Media.Tests;

/// <summary>
/// libjpeg-turbo MJPEG path (task 090): SMPTE bars encoded with tj3Compress8 at each subsampling decode to the
/// right planar layout and, through the WARP renderer, back to the 75 % bar colours within JPEG's loss.
/// Skipped when turbojpeg.dll isn't next to the tests (no <c>make win-deps</c>).
/// </summary>
public class MjpegDecoderTests
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int Tolerance = 8;

    private static void RequireTurboJpeg() =>
        Assert.SkipUnless(MjpegDecoder.IsAvailable, "turbojpeg.dll not found (run make win-deps)");

    private static byte[] Bars(int subsamp, int width = Width, int height = Height)
    {
        var bgra = TestPattern.Pack(TestPatternKind.SmpteBars, PixelFormat.Bgra, width, height);
        return TurboJpeg.CompressBgra(bgra.Planes[0], width, height, bgra.Strides[0], subsamp);
    }

    public static TheoryData<int, PixelFormat> Subsamplings() => new()
    {
        { TurboJpeg.Samp420, PixelFormat.I420 },
        { TurboJpeg.Samp422, PixelFormat.I422 },
        { TurboJpeg.Samp444, PixelFormat.I444 },
    };

    [Theory]
    [MemberData(nameof(Subsamplings))]
    public void DecodesBarsToPlanarFullRangeBt601(int subsamp, PixelFormat expected)
    {
        RequireTurboJpeg();
        using var decoder = new MjpegDecoder();
        using var renderer = D3DRenderer.CreateWarp();

        Assert.True(decoder.TryDecode(Bars(subsamp), 42, out var frame), decoder.LastError);
        Assert.Equal(expected, frame.Format);
        Assert.Equal((Width, Height), (frame.Width, frame.Height));
        Assert.Equal(true, frame.FullRange);
        Assert.Equal(ColorMatrix.Bt601, frame.Matrix);
        Assert.Equal(42, frame.Timestamp);

        renderer.Render(in frame);
        var pixels = renderer.ReadSourcePixels()!;
        for (var bar = 0; bar < 7; bar++)
        {
            var (r, g, b) = TestPattern.FullBarColors[bar];
            AssertPixel(pixels, (int)((bar + 0.5) * Width / 7), Height / 3,
                (int)Math.Round(r * 0.75 * 255), (int)Math.Round(g * 0.75 * 255), (int)Math.Round(b * 0.75 * 255), $"{expected} bar {bar}");
        }
    }

    [Fact]
    public void GrayscaleDecodesWithNeutralChroma()
    {
        RequireTurboJpeg();
        using var decoder = new MjpegDecoder();
        using var renderer = D3DRenderer.CreateWarp();

        Assert.True(decoder.TryDecode(Bars(TurboJpeg.SampGray), 0, out var frame), decoder.LastError);
        Assert.Equal(PixelFormat.I420, frame.Format);

        renderer.Render(in frame);
        var pixels = renderer.ReadSourcePixels()!;
        var white = (int)Math.Round(0.75 * 255);
        AssertPixel(pixels, Width / 14, Height / 3, white, white, white, "gray white bar");
        var i = (Height / 3 * Width + Width * 9 / 14) * 4; // magenta bar: grey, so R = G = B
        Assert.InRange(Math.Abs(pixels[i] - pixels[i + 1]) + Math.Abs(pixels[i + 1] - pixels[i + 2]), 0, 2);
    }

    [Fact]
    public unsafe void DecodesFramesWithoutHuffmanTables()
    {
        RequireTurboJpeg();
        using var decoder = new MjpegDecoder();
        var jpeg = Bars(TurboJpeg.Samp422);
        var stripped = StripSegments(jpeg, 0xC4);
        Assert.True(stripped.Length < jpeg.Length, "fixture had no DHT to strip");

        Assert.True(decoder.TryDecode(jpeg, 0, out var full), decoder.LastError);
        var reference = new ReadOnlySpan<byte>((void*)full.Plane0, full.Stride0 * Height).ToArray();
        Assert.True(decoder.TryDecode(stripped, 0, out var frame), decoder.LastError);
        var luma = new ReadOnlySpan<byte>((void*)frame.Plane0, frame.Stride0 * Height);

        // tj3Compress8 without TJPARAM_OPTIMIZE writes the standard tables, which libjpeg-turbo substitutes when absent.
        Assert.True(luma.SequenceEqual(reference));
    }

    [Fact]
    public void ReusesPlaneBuffersAcrossFrames()
    {
        RequireTurboJpeg();
        using var decoder = new MjpegDecoder();
        var jpeg = Bars(TurboJpeg.Samp422);

        Assert.True(decoder.TryDecode(jpeg, 0, out var first));
        Assert.True(decoder.TryDecode(jpeg, 1, out var second));
        Assert.Equal((first.Plane0, first.Plane1, first.Plane2), (second.Plane0, second.Plane1, second.Plane2));

        Assert.True(decoder.TryDecode(Bars(TurboJpeg.Samp420, 640, 360), 2, out var smaller));
        Assert.Equal((640, 360, PixelFormat.I420), (smaller.Width, smaller.Height, smaller.Format));
    }

    [Fact]
    public void RejectsGarbageAndSurvivesTruncation()
    {
        RequireTurboJpeg();
        using var decoder = new MjpegDecoder();

        Assert.False(decoder.TryDecode(new byte[1000], 0, out _));
        Assert.NotNull(decoder.LastError);

        var jpeg = Bars(TurboJpeg.Samp422);
        var truncated = jpeg.AsSpan(0, jpeg.Length / 2);
        // libjpeg pads a premature end with a warning; either outcome is fine as long as nothing throws.
        _ = decoder.TryDecode(truncated, 0, out _);
        Assert.True(decoder.TryDecode(jpeg, 0, out _), decoder.LastError);
    }

    private static readonly NativeVideoType[] UsbTwoCard =
    [
        new(0, MediaSubtypes.Mjpg, "MJPG", 1920, 1080, 60, 1, null, ColorMatrix.Auto, false),
        new(1, MediaSubtypes.FromFourCC("NV12"), "NV12", 1920, 1080, 60, 1, true, ColorMatrix.Bt601, true),
        new(2, MediaSubtypes.Mjpg, "MJPG", 3840, 2160, 30, 1, null, ColorMatrix.Auto, false),
        new(3, MediaSubtypes.FromFourCC("YUY2"), "YUY2", 640, 480, 60, 1, null, ColorMatrix.Auto, false),
    ];

    [Fact]
    public void AutoKeepsWindowsDecodedTwin()
    {
        var choice = MfDevices.Choose(UsbTwoCard, null, null, MjpegPolicy.Auto, canDecodeMjpeg: true);
        Assert.Equal(1, choice?.Index);
    }

    [Fact]
    public void PreferOwnDecodePicksRawMjpeg()
    {
        var choice = MfDevices.Choose(UsbTwoCard, null, null, MjpegPolicy.PreferOwnDecode, canDecodeMjpeg: true);
        Assert.Equal(0, choice?.Index);
    }

    [Fact]
    public void MjpegWithoutTwinNeedsTheDecoder()
    {
        NativeVideoType[] mjpegOnly = [UsbTwoCard[0], UsbTwoCard[3]];
        Assert.Equal(0, MfDevices.Choose(mjpegOnly, null, null, canDecodeMjpeg: true)?.Index);
        Assert.Equal(3, MfDevices.Choose(mjpegOnly, null, null, canDecodeMjpeg: false)?.Index);
        var pinned = new FormatPreference.Pinned(3840, 2160, 30);
        Assert.Equal(2, MfDevices.Choose(UsbTwoCard, pinned, null, canDecodeMjpeg: true)?.Index);
    }

    /// <summary>Removes every marker segment of the given type (e.g. 0xC4 = DHT) before the scan.</summary>
    private static byte[] StripSegments(byte[] jpeg, byte marker)
    {
        var output = new List<byte>(jpeg.Length) { jpeg[0], jpeg[1] }; // SOI
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var type = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (type == 0xDA) break; // SOS: the rest is entropy-coded data
            if (type != marker) output.AddRange(jpeg.AsSpan(i, length + 2).ToArray());
            i += length + 2;
        }
        output.AddRange(jpeg.AsSpan(i).ToArray());
        return [.. output];
    }

    private static void AssertPixel(byte[] rgba, int x, int y, int r, int g, int b, string what)
    {
        var i = (y * Width + x) * 4;
        var actual = (rgba[i], rgba[i + 1], rgba[i + 2]);
        Assert.True(Math.Abs(actual.Item1 - r) <= Tolerance && Math.Abs(actual.Item2 - g) <= Tolerance && Math.Abs(actual.Item3 - b) <= Tolerance,
            $"{what}: expected ({r}, {g}, {b}), got {actual}");
    }
}
