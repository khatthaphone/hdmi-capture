using HDMICaptain.Core;
using HDMICaptain.Media.Video;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Capture;

/// <summary>A video capture source as MFEnumDeviceSources reports it, plus where it sits on USB.</summary>
public sealed record VideoDevice(string Name, string SymbolicLink, UsbDetails? Usb);

/// <summary>An audio capture endpoint (WASAPI endpoint id) from MFEnumDeviceSources, with the container its device node belongs to.</summary>
public sealed record AudioDevice(string Name, string EndpointId, string SymbolicLink = "", Guid? ContainerId = null);

/// <summary>One native media type of the first video stream, in the reader's own index order.</summary>
public sealed record NativeVideoType(
    int Index,
    Guid Subtype,
    string FourCC,
    int Width,
    int Height,
    uint RateNumerator,
    uint RateDenominator,
    bool? FullRange,
    ColorMatrix Matrix,
    bool DecodedByWindows)
{
    public double FrameRate => RateDenominator == 0 ? 0 : (double)RateNumerator / RateDenominator;
}

/// <summary>Who decodes MJPEG when a card offers both raw MJPG and Windows' decoded NV12 twin (windows.md §2d, §3).</summary>
public enum MjpegPolicy
{
    /// <summary>Today: take Windows' decoded twin when there is one; our libjpeg-turbo path only for MJPG without a twin.</summary>
    Auto,
    /// <summary>Capture the raw MJPG and decode it ourselves (task 090), for the 092 latency comparison.</summary>
    PreferOwnDecode,
}

/// <summary>Media Foundation device enumeration and format listing (spec §13.2, windows.md §2a–2c).</summary>
public static class MfDevices
{
    /// <summary>
    /// Undocumented (not in SDK 26100 headers). Observed in 089 = 1 only on the NV12 twin of each MJPEG type of
    /// a USB 2 card: Windows' camera pipeline decodes MJPEG and offers the result as a "native" NV12 type, even
    /// with converters disabled. Such types don't cross the USB link uncompressed, so the link rule must skip them.
    /// </summary>
    private static readonly Guid DecodedTypeMarker = new("EA031A62-8BBB-43C5-B5C4-572D2D231C18");

    public static IReadOnlyList<VideoDevice> Video() =>
        Enumerate(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID, a =>
        {
            var name = a.GetStringOrNull(PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? "Camera";
            var link = a.GetStringOrNull(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK) ?? "";
            return new VideoDevice(name, link, link.Length > 0 ? UsbTopology.Describe(link) : null);
        });

    public static IReadOnlyList<AudioDevice> Audio() =>
        Enumerate(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_GUID, a =>
        {
            var link = a.GetStringOrNull(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_SYMBOLIC_LINK) ?? "";
            return new AudioDevice(
                a.GetStringOrNull(PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? "Microphone",
                a.GetStringOrNull(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_AUDCAP_ENDPOINT_ID) ?? "",
                link,
                link.Length > 0 ? UsbTopology.ContainerOfInterface(link) : null);
        });

    private static unsafe List<T> Enumerate<T>(Guid sourceType, Func<IMFAttributes, T> describe)
    {
        MediaFoundation.Startup();
        try
        {
            var filter = MfInterop.CreateAttributes(1);
            filter.SetGuid(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, sourceType);
            PInvoke.MFEnumDeviceSources(filter, out var array, out var count).ThrowOnFailure();
            MfInterop.Release(filter);
            var result = new List<T>((int)count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var activate = (IMFActivate)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown((nint)array[i]);
                    try
                    {
                        result.Add(describe(activate));
                    }
                    finally
                    {
                        MfInterop.Release(activate);
                    }
                }
            }
            finally
            {
                for (var i = 0; i < count; i++) array[i]->Release(); // GetObjectForIUnknown took its own reference
                PInvoke.CoTaskMemFree(array);
            }
            return result;
        }
        finally
        {
            MediaFoundation.Shutdown();
        }
    }

    /// <summary>Opens the device briefly and lists its native types (no converters, so these are what the wire carries).</summary>
    public static IReadOnlyList<NativeVideoType> NativeTypes(string symbolicLink)
    {
        MediaFoundation.Startup();
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            source = OpenSource(symbolicLink);
            var attributes = MfInterop.CreateAttributes(1);
            attributes.SetUInt32(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 1);
            PInvoke.MFCreateSourceReaderFromMediaSource(source, attributes, out reader).ThrowOnFailure();
            MfInterop.Release(attributes);
            return ReadNativeTypes(reader);
        }
        finally
        {
            MfInterop.Release(reader);
            MfInterop.Shutdown(source); // releasing the reader normally shut it down already
            MfInterop.Release(source);
            MediaFoundation.Shutdown();
        }
    }

    /// <summary>Every attribute of each native type as "key = value" lines, for research dumps (inventory --attributes).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> NativeTypeAttributes(string symbolicLink)
    {
        MediaFoundation.Startup();
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            source = OpenSource(symbolicLink);
            var attributes = MfInterop.CreateAttributes(1);
            attributes.SetUInt32(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 1);
            PInvoke.MFCreateSourceReaderFromMediaSource(source, attributes, out reader).ThrowOnFailure();
            MfInterop.Release(attributes);
            var result = new List<IReadOnlyList<string>>();
            const uint firstVideoStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            for (var i = 0; ; i++)
            {
                IMFMediaType type;
                try
                {
                    reader.GetNativeMediaType(firstVideoStream, (uint)i, out type);
                }
                catch (System.Runtime.InteropServices.COMException e) when (e.HResult == MfErrors.NoMoreTypes)
                {
                    return result;
                }
                result.Add(MfInterop.Dump(type));
                MfInterop.Release(type);
            }
        }
        finally
        {
            MfInterop.Release(reader);
            MfInterop.Shutdown(source);
            MfInterop.Release(source);
            MediaFoundation.Shutdown();
        }
    }

    internal static IMFMediaSource OpenSource(string symbolicLink)
    {
        var attributes = MfInterop.CreateAttributes(2);
        try
        {
            attributes.SetGuid(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            attributes.SetString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, symbolicLink);
            PInvoke.MFCreateDeviceSource(attributes, out var source).ThrowOnFailure();
            return source;
        }
        finally
        {
            MfInterop.Release(attributes);
        }
    }

    internal static List<NativeVideoType> ReadNativeTypes(IMFSourceReader reader)
    {
        const uint firstVideoStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        var types = new List<NativeVideoType>();
        for (var i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                reader.GetNativeMediaType(firstVideoStream, (uint)i, out type);
            }
            catch (System.Runtime.InteropServices.COMException e) when (e.HResult == MfErrors.NoMoreTypes)
            {
                return types;
            }
            try
            {
                types.Add(Describe(i, type));
            }
            finally
            {
                MfInterop.Release(type);
            }
        }
    }

    internal static NativeVideoType Describe(int index, IMFMediaType type)
    {
        var subtype = type.GetGuid(PInvoke.MF_MT_SUBTYPE);
        var (w, h) = MfInterop.Unpack(type.GetUInt64OrNull(PInvoke.MF_MT_FRAME_SIZE) ?? 0);
        var (num, den) = MfInterop.Unpack(type.GetUInt64OrNull(PInvoke.MF_MT_FRAME_RATE) ?? 0);
        bool? fullRange = type.GetUInt32OrNull(PInvoke.MF_MT_VIDEO_NOMINAL_RANGE) switch
        {
            (uint)MFNominalRange.MFNominalRange_0_255 => true,
            (uint)MFNominalRange.MFNominalRange_16_235 => false,
            _ => null,
        };
        var matrix = type.GetUInt32OrNull(PInvoke.MF_MT_YUV_MATRIX) switch
        {
            (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709 => ColorMatrix.Bt709,
            (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT601 => ColorMatrix.Bt601,
            _ => ColorMatrix.Auto,
        };
        var fourCC = MediaSubtypes.ToSelectorName(subtype) ?? subtype.ToString();
        var decoded = type.GetUInt32OrNull(DecodedTypeMarker) == 1;
        return new NativeVideoType(index, subtype, fourCC, (int)w, (int)h, num, den, fullRange, matrix, decoded);
    }

    /// <summary>Groups native types into the selector's descriptors: one per (fourCC, size), each rate a point range.</summary>
    public static IReadOnlyList<FormatDescriptor> Descriptors(IEnumerable<NativeVideoType> types) =>
        [.. types
            .GroupBy(t => (t.FourCC, t.Width, t.Height))
            .Select(g => new FormatDescriptor(g.Key.Width, g.Key.Height, g.Key.FourCC,
                [.. g.Select(t => t.FrameRate).Distinct().OrderDescending().Select(r => new FrameRateRange(r, r))]))];

    /// <summary>
    /// The type to capture: the selector's pick among types we can show: NV12 and YUY2 as they come, MJPG when
    /// libjpeg-turbo loads (<paramref name="canDecodeMjpeg"/>, default <see cref="MjpegDecoder.IsAvailable"/>).
    /// The selector ranks uncompressed over MJPG at the same size and rate, so with <see cref="MjpegPolicy.Auto"/>
    /// Windows' decoded NV12 twin of an MJPG type wins and MJPG is only picked where it has no twin.
    /// <see cref="MjpegPolicy.PreferOwnDecode"/> drops those twins so the raw MJPG goes through our decoder.
    /// The link rule guards against uncompressed types the wire can't carry, so it is skipped when Windows
    /// offers decoded MJPEG, which never crosses the link raw.
    /// </summary>
    public static NativeVideoType? Choose(IReadOnlyList<NativeVideoType> types, FormatPreference? preference, UsbLink? link,
        MjpegPolicy mjpeg = MjpegPolicy.Auto, bool? canDecodeMjpeg = null)
    {
        var decode = canDecodeMjpeg ?? MjpegDecoder.IsAvailable;
        var uploadable = types.Where(t => MfSource.ToPixelFormat(t.FourCC) != null || (decode && t.FourCC == "MJPG")).ToList();
        if (decode && mjpeg == MjpegPolicy.PreferOwnDecode)
        {
            var raw = uploadable.Where(t => t.FourCC == "MJPG").Select(t => (t.Width, t.Height, Math.Round(t.FrameRate, 2))).ToHashSet();
            uploadable.RemoveAll(t => t.DecodedByWindows && raw.Contains((t.Width, t.Height, Math.Round(t.FrameRate, 2))));
        }
        var effectiveLink = uploadable.Any(t => t.DecodedByWindows) ? null : link;
        var choice = FormatSelector.Select(Descriptors(uploadable), preference, effectiveLink);
        return choice != null ? Match(uploadable, choice) : null;
    }

    /// <summary>The native type that realises a selector choice: same layout and size, nearest frame rate.</summary>
    public static NativeVideoType? Match(IEnumerable<NativeVideoType> types, FormatChoice choice) =>
        types.Where(t => t.FourCC == choice.Format.FourCC && t.Width == choice.Format.Width && t.Height == choice.Format.Height)
            .MinBy(t => Math.Abs(t.FrameRate - choice.FrameRate));

}
