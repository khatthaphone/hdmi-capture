namespace HDMICaptain.Core;

/// <summary>
/// The one place that maps Media Foundation video subtype GUIDs (MF_MT_SUBTYPE) and FourCCs to the
/// fourCC names the format selector and the shared fixtures use (MJPG, YUY2, NV12, ...). Pure: the
/// GUIDs are plain <see cref="Guid"/> constants (a subtype GUID is {FOURCC-0000-0010-8000-00AA00389B71}),
/// so there is no Windows reference. Apple names (420v, yuvs, dmb1) are accepted like the selectors do.
/// </summary>
public static class MediaSubtypes
{
    private static readonly Guid Template = new("00000000-0000-0010-8000-00AA00389B71");

    public static readonly Guid Mjpg = FromFourCC("MJPG");
    public static readonly Guid Yuy2 = FromFourCC("YUY2");
    public static readonly Guid Nv12 = FromFourCC("NV12");
    public static readonly Guid Uyvy = FromFourCC("UYVY");
    public static readonly Guid I420 = FromFourCC("I420");
    public static readonly Guid H264 = FromFourCC("H264");

    /// <summary>Apple fourCC to the UVC/MF name of the same layout.</summary>
    private static readonly Dictionary<string, string> AppleToUvc = new()
    {
        ["420v"] = "NV12",
        ["420f"] = "NV12",
        ["yuvs"] = "YUY2",
        ["yuv2"] = "YUY2",
        ["2vuy"] = "UYVY",
        ["dmb1"] = "MJPG",
        ["mjpg"] = "MJPG",
        ["jpeg"] = "MJPG",
    };

    /// <summary>Builds the Media Foundation subtype GUID for a four-character code.</summary>
    public static Guid FromFourCC(string fourCC)
    {
        if (fourCC.Length != 4) throw new ArgumentException("A fourCC has four characters.", nameof(fourCC));
        var bytes = Template.ToByteArray();
        for (var i = 0; i < 4; i++) bytes[i] = (byte)fourCC[i];
        return new Guid(bytes);
    }

    /// <summary>The four-character code of a subtype GUID, or null when it isn't a FourCC-based subtype.</summary>
    public static string? ToFourCC(Guid subtype)
    {
        var bytes = subtype.ToByteArray();
        if (!bytes.AsSpan(4).SequenceEqual(Template.ToByteArray().AsSpan(4))) return null;
        var chars = new char[4];
        for (var i = 0; i < 4; i++)
        {
            if (bytes[i] < 0x20 || bytes[i] > 0x7E) return null;
            chars[i] = (char)bytes[i];
        }
        return new string(chars);
    }

    /// <summary>
    /// The fourCC the selector and fixtures use for an MF subtype, e.g. <c>MJPG</c>; the raw code for a
    /// FourCC subtype we don't know; null when the GUID isn't a FourCC subtype.
    /// </summary>
    public static string? ToSelectorName(Guid subtype) => ToFourCC(subtype) is { } cc ? Canonicalize(cc) : null;

    /// <summary>Maps Apple names to the UVC names (420v to NV12, yuvs to YUY2, dmb1 to MJPG); other codes are returned as given.</summary>
    public static string Canonicalize(string fourCC) => AppleToUvc.TryGetValue(fourCC, out var uvc) ? uvc : fourCC;

    /// <summary>The MF subtype GUID for a fixture or Apple fourCC, or null when we don't know the code.</summary>
    public static Guid? ToSubtype(string fourCC)
    {
        var name = Canonicalize(fourCC);
        return name is "MJPG" or "YUY2" or "NV12" or "UYVY" or "I420" or "H264" ? FromFourCC(name) : null;
    }
}
