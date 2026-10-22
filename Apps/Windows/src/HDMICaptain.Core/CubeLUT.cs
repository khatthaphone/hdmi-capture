using System.Globalization;

namespace HDMICaptain.Core;

/// <summary>
/// A colour lookup table parsed from an Adobe/Resolve .cube file (port of CaptureCore's CubeLUT and the Kotlin one).
/// Always held as 3D (1D tables are baked into a 33^3 grid), red varying fastest, as in the file format.
/// <see cref="Entries"/> holds size^3 RGB triples flattened: r0 g0 b0 r1 g1 b1 ...
/// </summary>
public sealed class CubeLUT(string title, int size, float[] domainMin, float[] domainMax, float[] entries)
{
    public sealed class ParseException(string message) : Exception(message);

    public string Title { get; } = title;
    public int Size { get; } = size;
    public float[] DomainMin { get; } = domainMin;
    public float[] DomainMax { get; } = domainMax;
    public float[] Entries { get; } = entries;

    public static CubeLUT Identity(int size = 17)
    {
        var output = new float[size * size * size * 3];
        var scale = (float)(size - 1);
        var i = 0;
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                {
                    output[i++] = r / scale;
                    output[i++] = g / scale;
                    output[i++] = b / scale;
                }
        return new CubeLUT("Identity", size, [0f, 0f, 0f], [1f, 1f, 1f], output);
    }

    public static CubeLUT Parse(string text, string fallbackTitle = "LUT")
    {
        var title = fallbackTitle;
        int? size3D = null;
        int? size1D = null;
        float[] domainMin = [0f, 0f, 0f];
        float[] domainMax = [1f, 1f, 1f];
        var values = new List<float>();
        var count = 0;

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            ParseException Bad() => new($"Can't read line {index + 1}.");

            switch (parts[0].ToUpperInvariant())
            {
                case "TITLE":
                    title = line[5..].Trim().Trim('"');
                    break;
                case "LUT_3D_SIZE":
                    size3D = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n3) ? n3 : throw Bad();
                    break;
                case "LUT_1D_SIZE":
                    size1D = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n1) ? n1 : throw Bad();
                    break;
                case "DOMAIN_MIN":
                    domainMin = Triple(parts[1..]) ?? throw Bad();
                    break;
                case "DOMAIN_MAX":
                    domainMax = Triple(parts[1..]) ?? throw Bad();
                    break;
                case "LUT_3D_INPUT_RANGE":
                case "LUT_1D_INPUT_RANGE":
                    if (parts.Length != 3 || !TryFloat(parts[1], out var lo) || !TryFloat(parts[2], out var hi)) throw Bad();
                    domainMin = [lo, lo, lo];
                    domainMax = [hi, hi, hi];
                    break;
                default:
                    var v = Triple(parts) ?? throw Bad();
                    values.AddRange(v);
                    count++;
                    break;
            }
        }

        if (size3D is { } n)
        {
            if (n is < 2 or > 65) throw new ParseException($"Unsupported LUT size {n}.");
            if (count != n * n * n) throw new ParseException($"Expected {n * n * n} entries, found {count}.");
            return new CubeLUT(title, n, domainMin, domainMax, [.. values]);
        }
        if (size1D is { } m)
        {
            if (m is < 2 or > 65_536) throw new ParseException($"Unsupported LUT size {m}.");
            if (count != m) throw new ParseException($"Expected {m} entries, found {count}.");
            return Bake1D([.. values], title, domainMin, domainMax);
        }
        throw new ParseException("Not a .cube LUT (no LUT_3D_SIZE or LUT_1D_SIZE).");
    }

    /// <summary>Applies a per-channel 1D curve on a 33^3 grid.</summary>
    private static CubeLUT Bake1D(float[] curve, string title, float[] domainMin, float[] domainMax)
    {
        const int size = 33;
        var n = curve.Length / 3;
        float Lookup(float x, int channel)
        {
            var position = x * (n - 1);
            var i0 = (int)position;
            var i1 = Math.Min(i0 + 1, n - 1);
            var t = position - i0;
            return curve[i0 * 3 + channel] * (1 - t) + curve[i1 * 3 + channel] * t;
        }
        var output = new float[size * size * size * 3];
        var i = 0;
        const float scale = size - 1;
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                {
                    output[i++] = Lookup(r / scale, 0);
                    output[i++] = Lookup(g / scale, 1);
                    output[i++] = Lookup(b / scale, 2);
                }
        return new CubeLUT(title, size, domainMin, domainMax, output);
    }

    private static bool TryFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static float[]? Triple(string[] parts)
    {
        if (parts.Length != 3) return null;
        var result = new float[3];
        for (var i = 0; i < 3; i++)
            if (!TryFloat(parts[i], out result[i])) return null;
        return result;
    }
}
