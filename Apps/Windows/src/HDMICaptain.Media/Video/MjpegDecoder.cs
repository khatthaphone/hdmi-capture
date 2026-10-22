using System.Diagnostics;
using System.Runtime.InteropServices;
using HDMICaptain.Core;

namespace HDMICaptain.Media.Video;

/// <summary>
/// Decodes one MJPEG frame at a time with libjpeg-turbo straight into planar YCbCr (windows.md §3, task 090):
/// 4:2:0/4:2:2/4:4:4 map to <see cref="PixelFormat.I420"/>/<see cref="PixelFormat.I422"/>/<see cref="PixelFormat.I444"/>,
/// grayscale to I420 with neutral chroma, and the GPU does the colour conversion. The plane buffers are native,
/// 64-byte aligned and reused, so steady-state decoding allocates nothing; they are only reallocated when the
/// size or subsampling changes. Frames are JFIF: full range BT.601, whatever the UVC descriptor says.
/// Missing Huffman tables (UVC cards often omit the DHT) fall back to the standard tables inside libjpeg-turbo.
/// Not thread-safe: use one decoder per thread. The returned frame is valid until the next decode or dispose.
/// </summary>
public sealed unsafe class MjpegDecoder : IDisposable
{
    private const int Align = 64;

    private nint _handle;
    private readonly byte*[] _planes = new byte*[3];
    private readonly int[] _strides = new int[3];
    private readonly nuint[] _sizes = new nuint[3];
    private int _width, _height, _subsamp = -1;
    private readonly Stopwatch _clock = new();

    /// <summary>True when turbojpeg.dll is loadable; without it MJPEG types stay unusable.</summary>
    public static bool IsAvailable => TurboJpeg.IsAvailable;

    /// <summary>
    /// Encodes BGRA to a baseline JFIF JPEG with libjpeg-turbo (standard Huffman tables), subsampled like
    /// <paramref name="layout"/> (I420, I422 or I444). For fixtures and benchmarks, not the capture path.
    /// </summary>
    public static byte[] EncodeBgra(byte[] bgra, int width, int height, int stride, PixelFormat layout, int quality = 95) =>
        TurboJpeg.CompressBgra(bgra, width, height, stride, layout switch
        {
            PixelFormat.I420 => TurboJpeg.Samp420,
            PixelFormat.I422 => TurboJpeg.Samp422,
            PixelFormat.I444 => TurboJpeg.Samp444,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "I420, I422 or I444"),
        }, quality);

    /// <summary>Throws <see cref="DllNotFoundException"/> when turbojpeg.dll is missing.</summary>
    public MjpegDecoder()
    {
        _handle = TurboJpeg.tj3Init(TurboJpeg.InitDecompress);
        if (_handle == 0) throw new InvalidOperationException("tj3Init(decompress) failed");
    }

    /// <summary>The last failure (corrupt frame, unsupported subsampling), or a warning on a frame that still decoded.</summary>
    public string? LastError { get; private set; }

    /// <summary>Wall time of the last <see cref="TryDecode(byte*, nint, long, out VideoFrame)"/> (header + planes).</summary>
    public double LastDecodeMilliseconds { get; private set; }

    /// <summary>Frames that decoded with a libjpeg warning (e.g. truncated or padded data). They are still shown.</summary>
    public long Warnings { get; private set; }

    public bool TryDecode(ReadOnlySpan<byte> jpeg, long timestamp, out VideoFrame frame)
    {
        fixed (byte* data = jpeg) return TryDecode(data, jpeg.Length, timestamp, out frame);
    }

    public bool TryDecode(byte* data, nint length, long timestamp, out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        frame = default;
        _clock.Restart();
        try
        {
            if (TurboJpeg.tj3DecompressHeader(_handle, data, (nuint)length) != 0)
            {
                LastError = $"header: {TurboJpeg.ErrorString(_handle)}";
                return false;
            }
            var width = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegWidth);
            var height = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegHeight);
            var subsamp = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamSubsamp);
            var colorspace = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamColorspace);
            PixelFormat format;
            switch (subsamp)
            {
                case TurboJpeg.Samp420: format = PixelFormat.I420; break;
                case TurboJpeg.Samp422: format = PixelFormat.I422; break;
                case TurboJpeg.Samp444: format = PixelFormat.I444; break;
                case TurboJpeg.SampGray: format = PixelFormat.I420; break;
                default:
                    LastError = $"unsupported subsampling {subsamp}";
                    return false;
            }
            if (colorspace != TurboJpeg.ColorspaceYCbCr && colorspace != TurboJpeg.ColorspaceGray)
            {
                LastError = $"unsupported colorspace {colorspace}";
                return false;
            }
            Ensure(width, height, subsamp);

            fixed (byte** planes = _planes)
            fixed (int* strides = _strides)
            {
                if (TurboJpeg.tj3DecompressToYUVPlanes8(_handle, data, (nuint)length, planes, strides) != 0)
                {
                    var message = TurboJpeg.ErrorString(_handle);
                    if (TurboJpeg.tj3GetErrorCode(_handle) != TurboJpeg.ErrorWarning)
                    {
                        LastError = message;
                        return false;
                    }
                    // A warning (premature end, extraneous bytes): the planes are filled; show the frame.
                    Warnings++;
                    LastError = $"warning: {message}";
                }
            }
            frame = new VideoFrame
            {
                Format = format,
                Width = width,
                Height = height,
                Plane0 = (nint)_planes[0],
                Stride0 = _strides[0],
                Plane1 = (nint)_planes[1],
                Stride1 = _strides[1],
                Plane2 = (nint)_planes[2],
                Stride2 = _strides[2],
                FullRange = true,
                Matrix = ColorMatrix.Bt601,
                Timestamp = timestamp,
            };
            return true;
        }
        finally
        {
            LastDecodeMilliseconds = _clock.Elapsed.TotalMilliseconds;
        }
    }

    private void Ensure(int width, int height, int subsamp)
    {
        if (width == _width && height == _height && subsamp == _subsamp) return;
        Free();
        var gray = subsamp == TurboJpeg.SampGray;
        // Grayscale fills only Y; the chroma planes are laid out as 4:2:0 and held at 128 (neutral).
        var layout = gray ? TurboJpeg.Samp420 : subsamp;
        for (var c = 0; c < 3; c++)
        {
            var w = TurboJpeg.tj3YUVPlaneWidth(c, width, layout);
            var h = TurboJpeg.tj3YUVPlaneHeight(c, height, layout);
            if (w <= 0 || h <= 0) throw new InvalidOperationException($"bad plane size for {width}x{height} subsamp {subsamp}");
            _strides[c] = (w + Align - 1) / Align * Align;
            _sizes[c] = (nuint)_strides[c] * (nuint)h;
            _planes[c] = (byte*)NativeMemory.AlignedAlloc(_sizes[c], Align);
            if (gray && c > 0) NativeMemory.Fill(_planes[c], _sizes[c], 128);
        }
        (_width, _height, _subsamp) = (width, height, subsamp);
    }

    private void Free()
    {
        for (var c = 0; c < 3; c++)
        {
            if (_planes[c] != null) NativeMemory.AlignedFree(_planes[c]);
            _planes[c] = null;
        }
        (_width, _height, _subsamp) = (0, 0, -1);
    }

    public void Dispose()
    {
        Free();
        if (_handle != 0) TurboJpeg.tj3Destroy(_handle);
        _handle = 0;
        GC.SuppressFinalize(this);
    }

    ~MjpegDecoder()
    {
        Free();
        if (_handle != 0) TurboJpeg.tj3Destroy(_handle);
    }
}
