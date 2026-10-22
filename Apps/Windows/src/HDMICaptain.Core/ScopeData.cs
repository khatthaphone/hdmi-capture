namespace HDMICaptain.Core;

/// <summary>
/// Luma histogram, luma waveform and CbCr vectorscope (port of CaptureCore's ScopeData and the Kotlin
/// one) from a sample grid of interleaved Y, Cb, Cr bytes. The GPU compute path can replace this later;
/// this is the reference and the tests' oracle.
/// </summary>
public sealed class ScopeData(int[] histogram, int[] waveform, int[] vectorscope)
{
    public const int Levels = 256;
    public const int WaveformColumns = 256;
    public const int VectorSize = 128;

    /// <summary>Counts per luma code value (0..255).</summary>
    public int[] Histogram { get; } = histogram;

    /// <summary>WaveformColumns columns x 256 rows of counts (row = luma code value).</summary>
    public int[] Waveform { get; } = waveform;

    /// <summary>VectorSize^2 counts over (Cb, Cr), y down = high Cr at the top.</summary>
    public int[] Vectorscope { get; } = vectorscope;

    public static ScopeData Analyze(ReadOnlySpan<byte> grid, int columns, int rows)
    {
        var histogram = new int[Levels];
        var waveform = new int[WaveformColumns * Levels];
        var vectorscope = new int[VectorSize * VectorSize];
        var i = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                int y = grid[i], cb = grid[i + 1], cr = grid[i + 2];
                i += 3;
                histogram[y]++;
                waveform[y * WaveformColumns + col * WaveformColumns / columns]++;
                vectorscope[((255 - cr) * VectorSize / 256) * VectorSize + cb * VectorSize / 256]++;
            }
        }
        return new ScopeData(histogram, waveform, vectorscope);
    }

    /// <summary>Where 75 % colour bars land on the vectorscope (normalised 0..1, y down), for target boxes.</summary>
    public static IReadOnlyList<(string Name, double X, double Y)> BarTargets { get; } = ComputeBarTargets();

    private static List<(string, double, double)> ComputeBarTargets()
    {
        (string Name, double R, double G, double B)[] bars =
        [
            ("Yl", 1, 1, 0), ("Cy", 0, 1, 1), ("G", 0, 1, 0), ("Mg", 1, 0, 1), ("R", 1, 0, 0), ("B", 0, 0, 1),
        ];
        var result = new List<(string, double, double)>();
        foreach (var (name, r, g, b) in bars)
        {
            var luma = 0.2126 * r * 0.75 + 0.7152 * g * 0.75 + 0.0722 * b * 0.75;
            var cb = RoundHalfUp(128 + 224 * ((b * 0.75 - luma) / 1.8556));
            var cr = RoundHalfUp(128 + 224 * ((r * 0.75 - luma) / 1.5748));
            result.Add((name, cb / 256, (255 - cr) / 256));
        }
        return result;
    }

    private static double RoundHalfUp(double v) => Math.Floor(v + 0.5);

    /// <summary>Waveform as ARGB pixels, 256 x 256, top row = luma 255.</summary>
    public uint[] WaveformPixels() => Pixels(WaveformColumns, Levels, Waveform, flipRows: true, tint: 0xFF59FF73);

    public uint[] VectorscopePixels() => Pixels(VectorSize, VectorSize, Vectorscope, flipRows: false, tint: 0xFF73FF8C);

    /// <summary>Histogram bars, 256 x height (square-root scale, as on Apple).</summary>
    public uint[] HistogramPixels(int height = 100)
    {
        var peak = Math.Max(Histogram.Max(), 1);
        var counts = new int[Levels * height];
        for (var level = 0; level < Levels; level++)
        {
            var bar = (int)(Math.Sqrt(Histogram[level] / (double)peak) * height);
            for (var row = 0; row < bar; row++) counts[(height - 1 - row) * Levels + level] = 1;
        }
        return Pixels(Levels, height, counts, flipRows: false, tint: 0xFFD9D9D9, binary: true);
    }

    private static uint[] Pixels(int width, int height, int[] counts, bool flipRows, uint tint, bool binary = false)
    {
        var peak = (double)Math.Max(counts.Max(), 1);
        var output = new uint[width * height];
        var tr = (tint >> 16) & 0xFF;
        var tg = (tint >> 8) & 0xFF;
        var tb = tint & 0xFF;
        for (var row = 0; row < height; row++)
        {
            var source = flipRows ? height - 1 - row : row;
            for (var col = 0; col < width; col++)
            {
                var count = counts[source * width + col];
                if (count == 0) continue;
                // Log scale so faint traces stay visible next to dense ones.
                var v = binary ? 1.0 : Math.Min(1.0, 0.25 + 0.75 * Math.Log(1.0 + count) / Math.Log(1.0 + peak));
                output[row * width + col] = 0xFF000000u | ((uint)(tr * v) << 16) | ((uint)(tg * v) << 8) | (uint)(tb * v);
            }
        }
        return output;
    }
}
