using System.Runtime.InteropServices;
using HDMICaptain.Media.Capture;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>Media types, samples, transforms and codec properties for the encoders and the MP4 writer.</summary>
internal static unsafe class MfMedia
{
    // mferror.h (SDK 10.0.26100); CsWin32 doesn't project it.
    public const int TransformNeedMoreInput = unchecked((int)0xC00D6D72);
    public const int TransformStreamChange = unchecked((int)0xC00D6D61);
    public const int NotAccepting = unchecked((int)0xC00D36B5);

    // MFT_OUTPUT_STREAM_INFO flags (mftransform.h).
    public const uint OutputProvidesSamples = 0x100;
    public const uint OutputCanProvideSamples = 0x200;

    // MediaEventType values (mfobjects.h).
    public const uint MEError = 1;
    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;
    public const uint METransformDrainComplete = 603;

    public static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public static void SetUInt64(this IMFAttributes attributes, Guid key, ulong value) => attributes.SetUINT64(&key, value);

    public static void SetRatio(this IMFAttributes attributes, Guid key, uint high, uint low) =>
        attributes.SetUInt64(key, ((ulong)high << 32) | low);

    public static void SetBlob(this IMFAttributes attributes, Guid key, byte[] value) => attributes.SetBlob(&key, value, (uint)value.Length);

    public static byte[]? GetBlobOrNull(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetBlobSize(&key, out var size);
            var blob = new byte[size];
            attributes.GetBlob(&key, blob, size, null);
            return blob;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static IMFMediaType CreateMediaType()
    {
        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();
        return type;
    }

    public static IMFMediaType Clone(IMFMediaType source)
    {
        var copy = CreateMediaType();
        source.CopyAllItems(copy);
        return copy;
    }

    /// <summary>A sample holding a copy of <paramref name="data"/>.</summary>
    public static IMFSample CreateSample(ReadOnlySpan<byte> data, long time, long duration)
    {
        PInvoke.MFCreateMemoryBuffer((uint)Math.Max(1, data.Length), out var buffer).ThrowOnFailure();
        try
        {
            byte* target;
            buffer.Lock(&target, null, null);
            data.CopyTo(new Span<byte>(target, data.Length));
            buffer.Unlock();
            buffer.SetCurrentLength((uint)data.Length);
            return Wrap(buffer, time, duration);
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }

    /// <summary>A sample around <paramref name="buffer"/> (which keeps its own reference).</summary>
    public static IMFSample Wrap(IMFMediaBuffer buffer, long time, long duration)
    {
        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        sample.AddBuffer(buffer);
        sample.SetSampleTime(time);
        sample.SetSampleDuration(duration);
        return sample;
    }

    /// <summary>All of a sample's bytes, copied out.</summary>
    public static byte[] CopyBytes(IMFSample sample)
    {
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            byte* data;
            uint length;
            buffer.Lock(&data, null, &length);
            try
            {
                return new ReadOnlySpan<byte>(data, (int)length).ToArray();
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }

    public static bool IsKeyframe(IMFSample sample) => (sample as IMFAttributes)?.GetUInt32OrNull(PInvoke.MFSampleExtension_CleanPoint) == 1;

    public static long? TimeOrNull(IMFSample sample)
    {
        try
        {
            sample.GetSampleTime(out var time);
            return time;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static long? DurationOrNull(IMFSample sample)
    {
        try
        {
            sample.GetSampleDuration(out var duration);
            return duration;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Transforms of a category, best first (MFT_ENUM_FLAG_SORTANDFILTER), with their friendly names.</summary>
    public static List<(IMFActivate Activate, string Name)> Enumerate(Guid category, MFT_ENUM_FLAG flags, Guid major, Guid input, Guid output)
    {
        var inInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = input };
        var outInfo = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = output };
        IMFActivate_unmanaged** array;
        uint count;
        PInvoke.MFTEnumEx(category, flags | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER, &inInfo, &outInfo, out array, &count).ThrowOnFailure();
        var result = new List<(IMFActivate, string)>((int)count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var activate = (IMFActivate)Marshal.GetUniqueObjectForIUnknown((nint)array[i]);
                result.Add((activate, activate.GetStringOrNull(PInvoke.MFT_FRIENDLY_NAME_Attribute) ?? "?"));
            }
        }
        finally
        {
            for (var i = 0; i < count; i++) array[i]->Release();
            PInvoke.CoTaskMemFree(array);
        }
        return result;
    }

    public static IMFTransform ActivateTransform(IMFActivate activate)
    {
        var iid = typeof(IMFTransform).GUID;
        activate.ActivateObject(&iid, out var pointer);
        try
        {
            return (IMFTransform)Marshal.GetUniqueObjectForIUnknown((nint)pointer);
        }
        finally
        {
            Marshal.Release((nint)pointer);
        }
    }

    /// <summary>Sets a codec property, returning false when the encoder doesn't support it.</summary>
    public static bool TrySet(this ICodecAPI codec, Guid api, object value)
    {
        try
        {
            codec.SetValue(&api, in value);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parameter sets (H.264 SPS/PPS, HEVC VPS/SPS/PPS) from an Annex B keyframe, start codes kept: the
    /// MF_MT_MPEG_SEQUENCE_HEADER the MP4 sink needs when an encoder doesn't put it on its output type.
    /// </summary>
    public static byte[]? ParameterSets(ReadOnlySpan<byte> annexB, bool hevc)
    {
        var result = new List<byte>();
        var i = 0;
        while (NextStartCode(annexB, i, out var start, out var codeLength))
        {
            var payload = start + codeLength;
            var end = NextStartCode(annexB, payload, out var next, out _) ? next : annexB.Length;
            if (payload < annexB.Length)
            {
                var header = annexB[payload];
                var type = hevc ? (header >> 1) & 0x3F : header & 0x1F;
                var isParameterSet = hevc ? type is 32 or 33 or 34 : type is 7 or 8;
                if (isParameterSet)
                {
                    result.AddRange([0, 0, 0, 1]);
                    result.AddRange(annexB[payload..end].ToArray());
                }
            }
            i = end;
        }
        return result.Count > 0 ? result.ToArray() : null;
    }

    private static bool NextStartCode(ReadOnlySpan<byte> data, int from, out int start, out int length)
    {
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1)
            {
                (start, length) = i > from && data[i - 1] == 0 ? (i - 1, 4) : (i, 3);
                return true;
            }
        }
        (start, length) = (data.Length, 0);
        return false;
    }
}
