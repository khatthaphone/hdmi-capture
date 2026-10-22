using System.Runtime.CompilerServices;
using HDMICaptain.Core;

namespace HDMICaptain.Media.Video;

/// <summary>
/// Port of CaptureCore's TestPattern: the same four images, built as limited-range BT.709 YCbCr
/// and packed into any <see cref="PixelFormat"/> so every renderer input path runs without a card.
/// </summary>
public static class TestPattern
{
    /// <summary>The eight 100 % bars, left to right (white, yellow, cyan, green, magenta, red, blue, black).</summary>
    public static readonly (double R, double G, double B)[] FullBarColors =
    [
        (1, 1, 1), (1, 1, 0), (0, 1, 1), (0, 1, 0), (1, 0, 1), (1, 0, 0), (0, 0, 1), (0, 0, 0),
    ];

    /// <summary>Full-resolution Y, Cb, Cr planes (4:4:4) for the pattern.</summary>
    public static (byte[] Y, byte[] Cb, byte[] Cr) Generate444(TestPatternKind kind, int width, int height)
    {
        var y = new byte[width * height];
        var cb = new byte[width * height];
        var cr = new byte[width * height];
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
            {
                var (l, b, r) = Sample(kind, (double)col / width, (double)row / height, width, height, col, row);
                var i = row * width + col;
                y[i] = l;
                cb[i] = b;
                cr[i] = r;
            }
        return (y, cb, cr);
    }

    /// <summary>Packs the pattern into <paramref name="format"/>. Chroma is sited at the even pixel, like TestPattern.swift.</summary>
    public static PackedFrame Pack(TestPatternKind kind, PixelFormat format, int width, int height)
    {
        var (y, cb, cr) = Generate444(kind, width, height);
        var cw = (width + 1) / 2;
        switch (format)
        {
            case PixelFormat.Nv12:
            {
                var ch = (height + 1) / 2;
                var uv = Pinned(cw * 2 * ch);
                for (var row = 0; row < ch; row++)
                    for (var col = 0; col < cw; col++)
                    {
                        var src = row * 2 * width + col * 2;
                        uv[row * cw * 2 + col * 2] = cb[src];
                        uv[row * cw * 2 + col * 2 + 1] = cr[src];
                    }
                return new PackedFrame(format, width, height, [Copy(y), uv], [width, cw * 2]);
            }
            case PixelFormat.Yuy2:
            {
                var packed = Pinned(cw * 4 * height);
                for (var row = 0; row < height; row++)
                    for (var col = 0; col < cw; col++)
                    {
                        var x0 = row * width + col * 2;
                        var x1 = row * width + Math.Min(col * 2 + 1, width - 1);
                        var o = row * cw * 4 + col * 4;
                        packed[o] = y[x0];
                        packed[o + 1] = cb[x0];
                        packed[o + 2] = y[x1];
                        packed[o + 3] = cr[x0];
                    }
                return new PackedFrame(format, width, height, [packed], [cw * 4]);
            }
            case PixelFormat.I420 or PixelFormat.I422 or PixelFormat.I444:
            {
                var sx = format == PixelFormat.I444 ? 1 : 2;
                var sy = format == PixelFormat.I420 ? 2 : 1;
                var pw = (width + sx - 1) / sx;
                var ph = (height + sy - 1) / sy;
                var u = Pinned(pw * ph);
                var v = Pinned(pw * ph);
                for (var row = 0; row < ph; row++)
                    for (var col = 0; col < pw; col++)
                    {
                        var src = row * sy * width + col * sx;
                        u[row * pw + col] = cb[src];
                        v[row * pw + col] = cr[src];
                    }
                return new PackedFrame(format, width, height, [Copy(y), u, v], [width, pw, pw]);
            }
            default:
            {
                var bgra = Pinned(width * 4 * height);
                for (var i = 0; i < width * height; i++)
                {
                    var (r, g, b) = ToRgb709Limited(y[i], cb[i], cr[i]);
                    bgra[i * 4] = b;
                    bgra[i * 4 + 1] = g;
                    bgra[i * 4 + 2] = r;
                    bgra[i * 4 + 3] = 255;
                }
                return new PackedFrame(format, width, height, [bgra], [width * 4]);
            }
        }
    }

    private static (byte Y, byte Cb, byte Cr) Sample(TestPatternKind kind, double x, double y, int width, int height, int col, int row)
    {
        switch (kind)
        {
            case TestPatternKind.FullBars:
                return YCbCr(FullBarColors[Math.Min(7, (int)(x * 8))]);
            case TestPatternKind.SmpteBars:
            {
                var seventh = Math.Min(6, (int)(x * 7));
                if (y < 0.67)
                {
                    var c = FullBarColors[seventh];
                    return YCbCr((c.R * 0.75, c.G * 0.75, c.B * 0.75));
                }
                if (y < 0.75)
                {
                    // Reverse blue bars: blue, black, magenta, black, cyan, black, white (75 %).
                    (double, double, double)[] reverse =
                        [(0, 0, 0.75), (0, 0, 0), (0.75, 0, 0.75), (0, 0, 0), (0, 0.75, 0.75), (0, 0, 0), (0.75, 0.75, 0.75)];
                    return YCbCr(reverse[seventh]);
                }
                // -I, 100 % white, +Q, black, then PLUGE (below black, black, above black).
                return (x * 7) switch
                {
                    < 1.25 => (16, 158, 95),
                    < 2.5 => YCbCr((1, 1, 1)),
                    < 3.75 => (16 + 18, 155, 166),
                    < 5 => (16, 128, 128),
                    < 5.33 => (12, 128, 128),
                    < 5.67 => (16, 128, 128),
                    < 6 => (20, 128, 128),
                    _ => (16, 128, 128),
                };
            }
            case TestPatternKind.Grid:
            {
                var cell = Math.Max(8, height / 18);
                var onLine = col % cell == 0 || row % cell == 0 || col == width - 1 || row == height - 1;
                var center = Math.Abs(col - width / 2) <= 1 || Math.Abs(row - height / 2) <= 1;
                return onLine || center ? ((byte)235, (byte)128, (byte)128) : ((byte)16, (byte)128, (byte)128);
            }
            default:
            {
                var level = y < 0.5 ? x : Math.Min(10, (int)(x * 11)) / 10.0;
                return YCbCr((level, level, level));
            }
        }
    }

    /// <summary>BT.709 limited-range conversion of RGB 0…1.</summary>
    public static (byte Y, byte Cb, byte Cr) YCbCr((double R, double G, double B) rgb)
    {
        var luma = 0.2126 * rgb.R + 0.7152 * rgb.G + 0.0722 * rgb.B;
        var cb = (rgb.B - luma) / 1.8556;
        var cr = (rgb.R - luma) / 1.5748;
        return (ToByte(16 + 219 * luma), ToByte(128 + 224 * cb), ToByte(128 + 224 * cr));
    }

    /// <summary>The inverse; builds the BGRA variant and gives renderer tests their expected values.</summary>
    public static (byte R, byte G, byte B) ToRgb709Limited(byte y, byte cb, byte cr)
    {
        var l = (y - 16) / 219.0;
        var b = (cb - 128) / 224.0;
        var r = (cr - 128) / 224.0;
        return (ToByte(255 * (l + 1.5748 * r)), ToByte(255 * (l - 0.1873 * b - 0.4681 * r)), ToByte(255 * (l + 1.8556 * b)));
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    private static byte[] Pinned(int length) => GC.AllocateUninitializedArray<byte>(length, pinned: true);

    private static byte[] Copy(byte[] source)
    {
        var copy = Pinned(source.Length);
        source.CopyTo(copy, 0);
        return copy;
    }
}

/// <summary>A generated frame; its planes are pinned arrays that live as long as this object.</summary>
public sealed class PackedFrame(PixelFormat format, int width, int height, byte[][] planes, int[] strides)
{
    public PixelFormat Format { get; } = format;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[][] Planes { get; } = planes;
    public int[] Strides { get; } = strides;

    public unsafe VideoFrame AsFrame(long timestamp = 0, bool fullRange = false)
    {
        nint Ptr(int i) => i < Planes.Length ? (nint)Unsafe.AsPointer(ref Planes[i][0]) : 0;
        int Stride(int i) => i < Strides.Length ? Strides[i] : 0;
        return new VideoFrame
        {
            Format = Format,
            Width = Width,
            Height = Height,
            Plane0 = Ptr(0),
            Stride0 = Stride(0),
            Plane1 = Ptr(1),
            Stride1 = Stride(1),
            Plane2 = Ptr(2),
            Stride2 = Stride(2),
            FullRange = Format == PixelFormat.Bgra || fullRange,
            Matrix = ColorMatrix.Bt709,
            Timestamp = timestamp,
        };
    }
}
