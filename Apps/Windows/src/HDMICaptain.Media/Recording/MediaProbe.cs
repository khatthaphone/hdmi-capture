using System.Runtime.InteropServices;
using HDMICaptain.Media.Capture;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>One track of a media file as a Source Reader delivers it (compressed, as stored).</summary>
public sealed record TrackInfo(string Subtype, int Samples, long Bytes, TimeSpan Start, TimeSpan End, int Keyframes);

/// <summary>What <see cref="MediaProbe.Read"/> found: video size and both tracks.</summary>
public sealed record MediaFileInfo(int Width, int Height, TrackInfo Video, TrackInfo? Audio, int AudioSampleRate, int AudioChannels)
{
    public TimeSpan Duration => Audio is { } a && a.End > Video.End ? a.End : Video.End;
}

/// <summary>Reads a recording back with a Source Reader (tests, the inventory tool's --record check).</summary>
public static unsafe class MediaProbe
{
    private const uint FirstVideo = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    private const uint FirstAudio = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_AUDIO_STREAM);
    private const uint AllStreams = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_ALL_STREAMS);

    public static MediaFileInfo Read(string path)
    {
        MediaFoundation.Startup();
        try
        {
            var (video, width, height, _, _) = ReadTrack(path, FirstVideo)!.Value;
            var audio = ReadTrack(path, FirstAudio);
            return new MediaFileInfo(width, height, video, audio?.Track, audio?.Rate ?? 0, audio?.Channels ?? 0);
        }
        finally
        {
            MediaFoundation.Shutdown();
        }
    }

    private static (TrackInfo Track, int Width, int Height, int Rate, int Channels)? ReadTrack(string path, uint stream)
    {
        PInvoke.MFCreateSourceReaderFromURL(path, null, out var reader).ThrowOnFailure();
        try
        {
            IMFMediaType type;
            try
            {
                reader.GetCurrentMediaType(stream, out type);
            }
            catch (COMException) when (stream == FirstAudio)
            {
                return null; // no audio track
            }
            var subtype = type.GetGuid(PInvoke.MF_MT_SUBTYPE);
            var size = type.GetUInt64OrNull(PInvoke.MF_MT_FRAME_SIZE) ?? 0;
            var rate = (int)(type.GetUInt32OrNull(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND) ?? 0);
            var channels = (int)(type.GetUInt32OrNull(PInvoke.MF_MT_AUDIO_NUM_CHANNELS) ?? 0);
            MfInterop.Release(type);

            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(stream, true);
            int samples = 0, keyframes = 0;
            long bytes = 0, start = long.MaxValue, end = 0;
            while (true)
            {
                uint flags;
                long timestamp;
                IMFSample_unmanaged* raw = null;
                reader.ReadSample(stream, 0, null, &flags, &timestamp, &raw);
                if (raw != null)
                {
                    var sample = (IMFSample)Marshal.GetUniqueObjectForIUnknown((nint)raw);
                    raw->Release();
                    try
                    {
                        sample.GetTotalLength(out var length);
                        bytes += length;
                        samples++;
                        if (MfMedia.IsKeyframe(sample)) keyframes++;
                        var duration = MfMedia.DurationOrNull(sample) ?? 0;
                        start = Math.Min(start, timestamp);
                        end = Math.Max(end, timestamp + duration);
                    }
                    finally
                    {
                        MfInterop.Release(sample);
                    }
                }
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) break;
                if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0) throw new COMException("read error");
            }
            var track = new TrackInfo(FourCC(subtype), samples, bytes, TimeSpan.FromTicks(start == long.MaxValue ? 0 : start),
                TimeSpan.FromTicks(end), keyframes);
            return (track, (int)(size >> 32), (int)(uint)size, rate, channels);
        }
        finally
        {
            MfInterop.Release(reader);
        }
    }

    /// <summary>
    /// Decodes video frame <paramref name="index"/> to NV12 (MF's decoder) and returns the (Y, Cb, Cr) sampler for it,
    /// in display pixels. For checking the picture that went into the file.
    /// </summary>
    public static Func<int, int, (byte Y, byte Cb, byte Cr)> DecodeFrame(string path, int index)
    {
        MediaFoundation.Startup();
        try
        {
            PInvoke.MFCreateSourceReaderFromURL(path, null, out var reader).ThrowOnFailure();
            try
            {
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(FirstVideo, true);
                var nv12 = MfMedia.CreateMediaType();
                nv12.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
                nv12.SetGuid(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                reader.SetCurrentMediaType(FirstVideo, null, nv12);
                MfInterop.Release(nv12);
                for (var i = 0; ; i++)
                {
                    uint flags;
                    IMFSample_unmanaged* raw = null;
                    reader.ReadSample(FirstVideo, 0, null, &flags, null, &raw);
                    if (raw == null)
                    {
                        if ((flags & (uint)MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0) throw new InvalidOperationException($"only {i} frames");
                        i--;
                        continue;
                    }
                    var sample = (IMFSample)Marshal.GetUniqueObjectForIUnknown((nint)raw);
                    raw->Release();
                    try
                    {
                        if (i < index) continue;
                        // The decoder's type is only final once frames come out (it may pad 1080 to 1088).
                        reader.GetCurrentMediaType(FirstVideo, out var type);
                        var size = type.GetUInt64OrNull(PInvoke.MF_MT_FRAME_SIZE) ?? 0;
                        var stride = (int)(type.GetUInt32OrNull(PInvoke.MF_MT_DEFAULT_STRIDE) ?? (uint)(size >> 32));
                        MfInterop.Release(type);
                        var height = (int)(uint)size;
                        var bytes = MfMedia.CopyBytes(sample);
                        return (x, y) =>
                        {
                            var chroma = stride * height + (y / 2) * stride + (x / 2) * 2;
                            return (bytes[y * stride + x], bytes[chroma], bytes[chroma + 1]);
                        };
                    }
                    finally
                    {
                        MfInterop.Release(sample);
                    }
                }
            }
            finally
            {
                MfInterop.Release(reader);
            }
        }
        finally
        {
            MediaFoundation.Shutdown();
        }
    }

    private static string FourCC(Guid subtype)
    {
        var data = subtype.ToByteArray();
        var code = BitConverter.ToUInt32(data, 0);
        if (code == 0x1610) return "AAC";
        if (code < 0x10000) return $"0x{code:X4}";
        return System.Text.Encoding.ASCII.GetString(data, 0, 4);
    }
}
