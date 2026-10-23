using System.Runtime.InteropServices;

namespace HDMICaptain.Linux.Media.Video;

/// <summary>
/// P/Invoke into the distro's libturbojpeg (TurboJPEG 2 API: Ubuntu 24.04 ships 2.1.5, and 3.x keeps these calls).
/// Constants follow turbojpeg.h.
/// </summary>
internal static unsafe partial class TurboJpeg
{
    private const string Library = "libturbojpeg.so.0";

    public const int Samp444 = 0, Samp422 = 1, Samp420 = 2, SampGray = 3;
    public const int ColorspaceYCbCr = 1, ColorspaceGray = 2;
    public const int PixelBgra = 8;
    public const int ErrorWarning = 0;

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var handle = tjInitDecompress();
            if (handle == 0) return false;
            tjDestroy(handle);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;

    [LibraryImport(Library)] public static partial nint tjInitDecompress();
    [LibraryImport(Library)] public static partial nint tjInitCompress();
    [LibraryImport(Library)] public static partial int tjDestroy(nint handle);
    [LibraryImport(Library)] public static partial nint tjGetErrorStr2(nint handle);
    [LibraryImport(Library)] public static partial int tjGetErrorCode(nint handle);

    [LibraryImport(Library)]
    public static partial int tjDecompressHeader3(nint handle, byte* jpeg, nuint size, int* width, int* height, int* subsamp, int* colorspace);

    [LibraryImport(Library)]
    public static partial int tjDecompressToYUVPlanes(nint handle, byte* jpeg, nuint size, byte** planes, int width, int* strides, int height, int flags);

    [LibraryImport(Library)]
    public static partial int tjCompress2(nint handle, byte* src, int width, int pitch, int height, int pixelFormat,
        byte** jpeg, nuint* size, int subsamp, int quality, int flags);

    [LibraryImport(Library)] public static partial void tjFree(byte* buffer);

    public static string ErrorString(nint handle) => Marshal.PtrToStringUTF8(tjGetErrorStr2(handle)) ?? "unknown error";

    /// <summary>Encodes BGRA to a baseline JPEG (standard Huffman tables). For tests and fixtures.</summary>
    public static byte[] CompressBgra(ReadOnlySpan<byte> bgra, int width, int height, int subsamp, int quality = 95)
    {
        var handle = tjInitCompress();
        if (handle == 0) throw new InvalidOperationException("tjInitCompress failed");
        try
        {
            byte* jpeg = null;
            nuint size = 0;
            fixed (byte* src = bgra)
                if (tjCompress2(handle, src, width, width * 4, height, PixelBgra, &jpeg, &size, subsamp, quality, 0) != 0)
                    throw new InvalidOperationException($"tjCompress2: {ErrorString(handle)}");
            try
            {
                return new ReadOnlySpan<byte>(jpeg, checked((int)size)).ToArray();
            }
            finally
            {
                tjFree(jpeg);
            }
        }
        finally
        {
            tjDestroy(handle);
        }
    }
}
