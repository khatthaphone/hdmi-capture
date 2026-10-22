using System.Runtime.CompilerServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Video;

namespace HDMICaptain.Media.Tests;

/// <summary>The frame sampler feeds SignalAnalyzer the same grid the Mac's native sampler does.</summary>
public class SignalMonitorTests
{
    private const int Samples = SignalAnalyzer.GridWidth * SignalAnalyzer.GridHeight;

    [Theory]
    [InlineData(PixelFormat.Nv12)]
    [InlineData(PixelFormat.Yuy2)]
    [InlineData(PixelFormat.I420)]
    [InlineData(PixelFormat.I422)]
    [InlineData(PixelFormat.I444)]
    public void BarsArePicture(PixelFormat format)
    {
        var frame = TestPattern.Pack(TestPatternKind.SmpteBars, format, 1280, 720).AsFrame();

        Assert.IsType<SignalContent.Picture>(Classify(frame));
    }

    [Fact]
    public void FlatLimitedBlackIsBlack()
    {
        var frame = Uniform(16, 128, 128, out var keep);

        Assert.IsType<SignalContent.Black>(Classify(frame));
        GC.KeepAlive(keep);
    }

    [Fact]
    public void CardBlueScreenIsSolid()
    {
        // BT.709 limited-range blue (0, 0, 191): Y 32, Cb 212, Cr 118 — what MS21xx cards show without input.
        var frame = Uniform(32, 212, 118, out var keep);

        var content = Assert.IsType<SignalContent.Solid>(Classify(frame));
        Assert.True(content.Blue > 150 && content.Red < 40, $"got {content}");
        GC.KeepAlive(keep);
    }

    [Fact]
    public void BgraIsNotSampled()
    {
        var frame = TestPattern.Pack(TestPatternKind.SmpteBars, PixelFormat.Bgra, 640, 360).AsFrame();

        Assert.False(SignalMonitor.Sample(frame, new int[Samples], new int[Samples], new int[Samples]));
    }

    private static SignalContent Classify(in VideoFrame frame)
    {
        int[] y = new int[Samples], cb = new int[Samples], cr = new int[Samples];
        Assert.True(SignalMonitor.Sample(frame, y, cb, cr));
        return SignalAnalyzer.Classify(SignalAnalyzer.ComputeStats(y, cb, cr), frame.FullRange ?? false);
    }

    private static unsafe VideoFrame Uniform(byte luma, byte cb, byte cr, out byte[][] planes)
    {
        const int w = 320, h = 180;
        var y = GC.AllocateArray<byte>(w * h, pinned: true);
        var uv = GC.AllocateArray<byte>(w * h / 2, pinned: true);
        Array.Fill(y, luma);
        for (var i = 0; i < uv.Length; i += 2)
        {
            uv[i] = cb;
            uv[i + 1] = cr;
        }
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
            FullRange = false,
            Matrix = ColorMatrix.Bt709,
        };
    }
}
