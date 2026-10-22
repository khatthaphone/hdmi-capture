using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Capture;

/// <summary>Small helpers over the CsWin32 MF projections: attribute access by GUID value and prompt RCW release.</summary>
internal static unsafe class MfInterop
{
    public static Guid GetGuid(this IMFAttributes attributes, Guid key)
    {
        Guid value;
        attributes.GetGUID(&key, &value);
        return value;
    }

    public static ulong? GetUInt64OrNull(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetUINT64(&key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static uint? GetUInt32OrNull(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetUINT32(&key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static string? GetStringOrNull(this IMFAttributes attributes, Guid key)
    {
        PWSTR text;
        try
        {
            attributes.GetAllocatedString(&key, &text, out _);
        }
        catch (COMException)
        {
            return null;
        }
        try
        {
            return text.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(text.Value);
        }
    }

    /// <summary>All items as "key = value" (UINT32/UINT64/GUID/string/blob size); keys by GUID.</summary>
    public static List<string> Dump(IMFAttributes attributes)
    {
        attributes.GetCount(out var count);
        var lines = new List<string>((int)count);
        for (var i = 0u; i < count; i++)
        {
            Guid key;
            attributes.GetItemByIndex(i, &key, null);
            MF_ATTRIBUTE_TYPE type;
            attributes.GetItemType(&key, &type);
            string value = type switch
            {
                MF_ATTRIBUTE_TYPE.MF_ATTRIBUTE_UINT32 => attributes.GetUInt32OrNull(key)?.ToString() ?? "?",
                MF_ATTRIBUTE_TYPE.MF_ATTRIBUTE_UINT64 => attributes.GetUInt64OrNull(key) is { } v ? $"{v >> 32}:{(uint)v}" : "?",
                MF_ATTRIBUTE_TYPE.MF_ATTRIBUTE_GUID => attributes.GetGuid(key).ToString(),
                MF_ATTRIBUTE_TYPE.MF_ATTRIBUTE_STRING => attributes.GetStringOrNull(key) ?? "?",
                MF_ATTRIBUTE_TYPE.MF_ATTRIBUTE_BLOB => BlobSize(attributes, key),
                _ => type.ToString(),
            };
            lines.Add($"{key} = {value}");
        }
        return lines;

        static string BlobSize(IMFAttributes a, Guid key)
        {
            a.GetBlobSize(&key, out var size);
            return $"blob[{size}]";
        }
    }

    public static void SetGuid(this IMFAttributes attributes, Guid key, Guid value) => attributes.SetGUID(&key, &value);

    public static void SetUInt32(this IMFAttributes attributes, Guid key, uint value) => attributes.SetUINT32(&key, value);

    public static void SetString(this IMFAttributes attributes, Guid key, string value)
    {
        fixed (char* v = value) attributes.SetString(&key, v);
    }

    public static void SetUnknown(this IMFAttributes attributes, Guid key, object value) => attributes.SetUnknown(&key, value);

    public static IMFAttributes CreateAttributes(uint size = 4)
    {
        PInvoke.MFCreateAttributes(out var attributes, size).ThrowOnFailure();
        return attributes;
    }

    /// <summary>Packed MF ratio (MF_MT_FRAME_SIZE, MF_MT_FRAME_RATE): high 32 bits, low 32 bits.</summary>
    public static (uint High, uint Low) Unpack(ulong packed) => ((uint)(packed >> 32), (uint)packed);

    /// <summary>
    /// Shuts a device source down, ignoring MF_E_SHUTDOWN (releasing its reader already did it) and the
    /// errors an invalidated (unplugged) source returns.
    /// </summary>
    public static void Shutdown(IMFMediaSource? source)
    {
        try
        {
            source?.Shutdown();
        }
        catch (COMException)
        {
        }
    }

    /// <summary>
    /// Releases an RCW now instead of at the next GC. Capture samples come from a small pool owned by the
    /// driver, so a sample that waits for the finalizer stalls the stream.
    /// </summary>
    public static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject)) Marshal.FinalReleaseComObject(comObject);
    }
}
