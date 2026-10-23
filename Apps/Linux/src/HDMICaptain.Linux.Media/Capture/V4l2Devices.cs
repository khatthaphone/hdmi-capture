using System.Globalization;
using System.Runtime.InteropServices;
using HDMICaptain.Core;

namespace HDMICaptain.Linux.Media.Capture;

/// <summary>The USB device behind a video node, from sysfs (the stable identity; /dev/videoN is not).</summary>
public sealed record UsbIdentity(int VendorId, int ProductId, string? Serial, string? Manufacturer, string? Product, int? SpeedMbps, string SysPath)
{
    public string Key => DeviceKey.Of(VendorId, ProductId, Serial, Product);

    /// <summary>FormatSelector's link rule: high speed (480) is USB 2, 5000+ is USB 3.</summary>
    public UsbLink? Link => SpeedMbps switch { null => null, < 1000 => UsbLink.Usb2, _ => UsbLink.Usb3 };
}

/// <summary>A V4L2 capture node with its native formats.</summary>
public sealed record V4l2Device(string Path, string Name, string Driver, string BusInfo, UsbIdentity? Usb, IReadOnlyList<V4l2Format> Formats)
{
    public IReadOnlyList<FormatDescriptor> Descriptors => Formats.Select(f => f.Descriptor).ToList();
}

/// <summary>One native type: the V4L2 code plus the selector's view of it.</summary>
public sealed record V4l2Format(string V4l2FourCC, bool Compressed, int Width, int Height, IReadOnlyList<(uint Num, uint Den)> Intervals)
{
    public FormatDescriptor Descriptor { get; } = new(Width, Height, MediaSubtypes.Canonicalize(V4l2FourCC),
        Intervals.Where(i => i.Num > 0).Select(i => (double)i.Den / i.Num).Select(fps => new FrameRateRange(fps, fps)).ToList());
}

public static unsafe partial class V4l2Devices
{
    public const string SysClass = "/sys/class/video4linux";

    /// <summary>Every capture node (metadata nodes skipped), in /dev order. Nodes we can't open are left out.</summary>
    public static IReadOnlyList<V4l2Device> List(Action<string>? log = null)
    {
        var devices = new List<V4l2Device>();
        if (!Directory.Exists(SysClass)) return devices;
        var nodes = Directory.GetDirectories(SysClass, "video*").Select(System.IO.Path.GetFileName).OfType<string>()
            .OrderBy(n => int.TryParse(n.AsSpan(5), out var i) ? i : int.MaxValue);
        foreach (var node in nodes)
        {
            var path = "/dev/" + node;
            var device = Probe(path, out var error);
            if (device != null) devices.Add(device);
            else if (error != 0) log?.Invoke($"v4l2: {path}: {Errno.Name(error)}");
        }
        return devices;
    }

    /// <summary>Query + formats for one node; null (and errno) when it isn't a capture node or can't be opened.</summary>
    public static V4l2Device? Probe(string path, out int error)
    {
        V4l2Native.Info info;
        error = V4l2Native.hc_v4l2_query(path, &info);
        if (error != 0) return null;
        var caps = info.Capabilities;
        if ((caps & V4l2Native.CapVideoCapture) == 0 || (caps & V4l2Native.CapStreaming) == 0) return null;
        var formats = new V4l2Native.Format[64];
        int count;
        fixed (V4l2Native.Format* f = formats) count = V4l2Native.hc_v4l2_enum_formats(path, f, formats.Length);
        if (count < 0)
        {
            error = count;
            return null;
        }
        var list = new List<V4l2Format>();
        for (var i = 0; i < Math.Min(count, formats.Length); i++)
        {
            ref var f = ref formats[i];
            var intervals = new List<(uint, uint)>();
            for (var k = 0; k < f.IntervalCount; k++) intervals.Add((f.IntervalNum[k], f.IntervalDen[k]));
            list.Add(new V4l2Format(V4l2Native.FourCCText(f.FourCC), (f.Flags & V4l2Native.FmtFlagCompressed) != 0, f.Width, f.Height, intervals));
        }
        return new V4l2Device(path, V4l2Native.Text(info.Card, 32), V4l2Native.Text(info.Driver, 16), V4l2Native.Text(info.BusInfo, 32),
            ReadUsb(System.IO.Path.GetFileName(path)), list);
    }

    /// <summary>Walks from /sys/class/video4linux/videoN/device (the USB interface) up to the USB device.</summary>
    public static UsbIdentity? ReadUsb(string node, string sysClass = SysClass)
    {
        var start = System.IO.Path.Combine(sysClass, node, "device");
        if (!Directory.Exists(start)) return null;
        // The class entry is itself a symlink and "device" is relative to its target, so resolve the whole path the way
        // the kernel does (.NET's ResolveLinkTarget applies the relative link to the unresolved path and lands elsewhere).
        for (var dir = new DirectoryInfo(RealPath(start) ?? start); dir != null; dir = dir.Parent)
        {
            var vendor = ReadAttr(dir.FullName, "idVendor");
            var product = ReadAttr(dir.FullName, "idProduct");
            if (vendor == null || product == null) continue;
            return new UsbIdentity(
                int.Parse(vendor, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(product, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ReadAttr(dir.FullName, "serial"), ReadAttr(dir.FullName, "manufacturer"), ReadAttr(dir.FullName, "product"),
                int.TryParse(ReadAttr(dir.FullName, "speed"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
                dir.FullName);
        }
        return null;
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath(string path, nint resolved);

    [LibraryImport("libc")]
    private static partial void free(nint pointer);

    private static string? RealPath(string path)
    {
        var resolved = realpath(path, 0);
        if (resolved == 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    private static string? ReadAttr(string dir, string name)
    {
        try
        {
            var path = System.IO.Path.Combine(dir, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
