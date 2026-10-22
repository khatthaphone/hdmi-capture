using System.Runtime.InteropServices;

namespace HDMICaptain.Media.Video;

/// <summary>
/// P/Invoke into libjpeg-turbo's TurboJPEG 3 API (<c>turbojpeg.dll</c>, built by <c>make win-deps</c> from
/// <c>native/vcpkg.json</c>; licence in THIRD-PARTY.md). Only what the MJPEG path and its tests need.
/// Constants follow <c>turbojpeg.h</c> 3.1.
/// </summary>
internal static partial class TurboJpeg
{
    private const string Library = "turbojpeg.dll";

    public const int InitCompress = 0;
    public const int InitDecompress = 1;

    // TJSAMP
    public const int Samp444 = 0;
    public const int Samp422 = 1;
    public const int Samp420 = 2;
    public const int SampGray = 3;

    // TJPARAM
    public const int ParamQuality = 3;
    public const int ParamSubsamp = 4;
    public const int ParamJpegWidth = 5;
    public const int ParamJpegHeight = 6;
    public const int ParamColorspace = 8;

    // TJCS
    public const int ColorspaceYCbCr = 1;
    public const int ColorspaceGray = 2;

    // TJPF
    public const int PixelBgra = 8;

    // TJERR
    public const int ErrorWarning = 0;

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            if (!NativeLibrary.TryLoad(Library, typeof(TurboJpeg).Assembly, null, out _)) return false;
            var handle = tj3Init(InitDecompress);
            if (handle == 0) return false;
            tj3Destroy(handle);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    });

    /// <summary>True when turbojpeg.dll (3.x) loads next to the app. False on a build without <c>make win-deps</c>.</summary>
    public static bool IsAvailable => Available.Value;

    [LibraryImport(Library)]
    public static partial nint tj3Init(int initType);

    [LibraryImport(Library)]
    public static partial void tj3Destroy(nint handle);

    [LibraryImport(Library)]
    public static partial nint tj3GetErrorStr(nint handle);

    [LibraryImport(Library)]
    public static partial int tj3GetErrorCode(nint handle);

    [LibraryImport(Library)]
    public static partial int tj3Set(nint handle, int param, int value);

    [LibraryImport(Library)]
    public static partial int tj3Get(nint handle, int param);

    [LibraryImport(Library)]
    public static unsafe partial int tj3DecompressHeader(nint handle, byte* jpegBuf, nuint jpegSize);

    [LibraryImport(Library)]
    public static unsafe partial int tj3DecompressToYUVPlanes8(nint handle, byte* jpegBuf, nuint jpegSize, byte** dstPlanes, int* strides);

    [LibraryImport(Library)]
    public static partial int tj3YUVPlaneWidth(int componentId, int width, int subsamp);

    [LibraryImport(Library)]
    public static partial int tj3YUVPlaneHeight(int componentId, int height, int subsamp);

    [LibraryImport(Library)]
    public static unsafe partial int tj3Compress8(nint handle, byte* srcBuf, int width, int pitch, int height, int pixelFormat,
        byte** jpegBuf, nuint* jpegSize);

    [LibraryImport(Library)]
    public static unsafe partial void tj3Free(void* buffer);

    public static string ErrorString(nint handle) => Marshal.PtrToStringUTF8(tj3GetErrorStr(handle)) ?? "unknown error";

    /// <summary>Encodes a BGRA image to a baseline JPEG (standard Huffman tables). For tests and fixtures.</summary>
    public static unsafe byte[] CompressBgra(byte[] bgra, int width, int height, int stride, int subsamp, int quality = 95)
    {
        var handle = tj3Init(InitCompress);
        if (handle == 0) throw new InvalidOperationException("tj3Init(compress) failed");
        try
        {
            tj3Set(handle, ParamQuality, quality);
            tj3Set(handle, ParamSubsamp, subsamp);
            byte* jpeg = null;
            nuint size = 0;
            fixed (byte* src = bgra)
            {
                if (tj3Compress8(handle, src, width, stride, height, PixelBgra, &jpeg, &size) != 0)
                    throw new InvalidOperationException($"tj3Compress8: {ErrorString(handle)}");
            }
            try
            {
                return new ReadOnlySpan<byte>(jpeg, checked((int)size)).ToArray();
            }
            finally
            {
                tj3Free(jpeg);
            }
        }
        finally
        {
            tj3Destroy(handle);
        }
    }
}
