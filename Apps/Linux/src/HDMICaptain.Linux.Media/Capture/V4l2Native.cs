using System.Runtime.InteropServices;

namespace HDMICaptain.Linux.Media.Capture;

/// <summary>P/Invoke into native/linux-media (libhcmedia.so): the flat records of hc_v4l2.h.</summary>
internal static unsafe partial class V4l2Native
{
    private const string Lib = "hcmedia";
    public const int MaxIntervals = 16;

    public const uint CapVideoCapture = 0x00000001;
    public const uint CapMetaCapture = 0x00800000;
    public const uint CapStreaming = 0x04000000;
    public const uint FmtFlagCompressed = 0x0001;
    public const uint BufFlagError = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public fixed byte Driver[16];
        public fixed byte Card[32];
        public fixed byte BusInfo[32];
        public uint Capabilities;
        public uint Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Format
    {
        public uint FourCC;
        public uint Flags;
        public int Width, Height;
        public int IntervalCount;
        public fixed uint IntervalNum[MaxIntervals];
        public fixed uint IntervalDen[MaxIntervals];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Actual
    {
        public uint FourCC;
        public int Width, Height;
        public uint BytesPerLine, SizeImage;
        public uint IntervalNum, IntervalDen;
        public int BufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Buffer
    {
        public int Index;
        public int Skipped;
        public byte* Data;
        public uint BytesUsed;
        public uint Flags;
        public uint Sequence;
        public uint Reserved;
        public long TimestampNs;
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int hc_v4l2_query(string path, Info* info);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int hc_v4l2_enum_formats(string path, Format* formats, int max);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint hc_v4l2_open(string path, int* error);

    [LibraryImport(Lib)]
    public static partial int hc_v4l2_start(nint v, uint fourCC, int width, int height, uint intervalNum, uint intervalDen, int buffers, Actual* actual);

    [LibraryImport(Lib)]
    public static partial int hc_v4l2_dequeue(nint v, int timeoutMs, Buffer* buffer);

    [LibraryImport(Lib)]
    public static partial int hc_v4l2_requeue(nint v, int index);

    [LibraryImport(Lib)]
    public static partial void hc_v4l2_stop(nint v);

    [LibraryImport(Lib)]
    public static partial void hc_v4l2_close(nint v);

    [LibraryImport(Lib)]
    public static partial int hc_v4l2_struct_size(int which);

    public static string Text(byte* chars, int max)
    {
        var length = 0;
        while (length < max && chars[length] != 0) length++;
        return System.Text.Encoding.UTF8.GetString(chars, length);
    }

    public static string FourCCText(uint code) =>
        new([(char)(code & 0xFF), (char)((code >> 8) & 0xFF), (char)((code >> 16) & 0xFF), (char)(code >> 24)]);

    public static uint FourCCCode(string text) =>
        (uint)text[0] | (uint)text[1] << 8 | (uint)text[2] << 16 | (uint)text[3] << 24;
}

/// <summary>Errno values the capture code reacts to (asm-generic).</summary>
public static class Errno
{
    public const int EPERM = 1, ENOENT = 2, EIO = 5, ENXIO = 6, EAGAIN = 11, ENOMEM = 12, EACCES = 13, EBUSY = 16, ENODEV = 19, EINVAL = 22, ENOSPC = 28, EPROTO = 71;

    public static string Name(int errno) => Math.Abs(errno) switch
    {
        EPERM => "EPERM", ENOENT => "ENOENT", EIO => "EIO", ENXIO => "ENXIO", EAGAIN => "EAGAIN", ENOMEM => "ENOMEM",
        EACCES => "EACCES", EBUSY => "EBUSY", ENODEV => "ENODEV", EINVAL => "EINVAL", ENOSPC => "ENOSPC", EPROTO => "EPROTO",
        var n => $"errno {n}",
    };
}
