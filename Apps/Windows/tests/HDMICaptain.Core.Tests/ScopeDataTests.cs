namespace HDMICaptain.Core.Tests;

public class ScopeDataTests
{
    private static byte[] Grid(int columns, int rows, Func<int, int, (int Y, int Cb, int Cr)> ycc)
    {
        var output = new byte[columns * rows * 3];
        var i = 0;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                var (y, cb, cr) = ycc(c, r);
                output[i++] = (byte)y;
                output[i++] = (byte)cb;
                output[i++] = (byte)cr;
            }
        return output;
    }

    [Fact]
    public void FlatGrayFillsOneHistogramBinAndTheVectorscopeCentre()
    {
        var data = ScopeData.Analyze(Grid(320, 180, (_, _) => (126, 128, 128)), 320, 180);
        Assert.Equal(320 * 180, data.Histogram[126]);
        Assert.Equal(320 * 180, data.Histogram.Sum());
        var centre = (127 * ScopeData.VectorSize / 256) * ScopeData.VectorSize + 128 * ScopeData.VectorSize / 256;
        Assert.Equal(320 * 180, data.Vectorscope[centre]);
    }

    [Fact]
    public void RampSpreadsAcrossTheWaveform()
    {
        var data = ScopeData.Analyze(Grid(256, 10, (c, _) => (c, 128, 128)), 256, 10);
        // Column c holds luma c: the trace is a diagonal.
        foreach (var c in new[] { 0, 100, 255 }) Assert.Equal(10, data.Waveform[c * ScopeData.WaveformColumns + c]);
    }

    [Fact]
    public void BarTargetsAreInsideTheScope()
    {
        Assert.Equal(6, ScopeData.BarTargets.Count);
        foreach (var (_, x, y) in ScopeData.BarTargets) { Assert.InRange(x, 0.0, 1.0); Assert.InRange(y, 0.0, 1.0); }
    }

    [Fact]
    public void SeventyFivePercentYellowLandsOnItsTarget()
    {
        // 75 % yellow, BT.709 limited: Y 168, Cb 44, Cr 136.
        var data = ScopeData.Analyze(Grid(4, 4, (_, _) => (168, 44, 136)), 4, 4);
        var index = Array.FindIndex(data.Vectorscope, v => v > 0);
        var x = (index % ScopeData.VectorSize + 0.5) / ScopeData.VectorSize;
        var y = (index / ScopeData.VectorSize + 0.5) / ScopeData.VectorSize;
        var target = ScopeData.BarTargets.First(t => t.Name == "Yl");
        Assert.Equal(target.X, x, 0.01);
        Assert.Equal(target.Y, y, 0.01);
    }

    [Fact]
    public void ImagesHaveTheRightSize()
    {
        var data = ScopeData.Analyze(Grid(16, 9, (c, r) => (16 + c * 10, 128 + r, 128 - r)), 16, 9);
        Assert.Equal(256 * 256, data.WaveformPixels().Length);
        Assert.Equal(128 * 128, data.VectorscopePixels().Length);
        Assert.Equal(256 * 100, data.HistogramPixels().Length);
    }
}
