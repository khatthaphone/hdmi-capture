using System.Runtime.InteropServices;
using HDMICaptain.Media.Capture;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>
/// A Sink Writer in passthrough: already-encoded H.264/HEVC and AAC samples go into an MP4 without
/// re-encoding. Fragmented MP4 (MFTranscodeContainerType_FMPEG4) when asked and available, so a crash
/// or unplug leaves a playable file; plain MP4 otherwise. Not thread-safe: callers serialize.
/// </summary>
internal sealed class Mp4Writer : IDisposable
{
    private IMFSinkWriter? _writer;
    private readonly uint _video;
    private readonly uint? _audio;

    public Mp4Writer(string path, bool fragmented, IMFMediaType videoType, IMFMediaType? audioType)
    {
        if (File.Exists(path)) File.Delete(path);
        Exception? last = null;
        foreach (var container in fragmented
                     ? new[] { PInvoke.MFTranscodeContainerType_FMPEG4, PInvoke.MFTranscodeContainerType_MPEG4 }
                     : [PInvoke.MFTranscodeContainerType_MPEG4])
        {
            try
            {
                (_writer, _video, _audio) = Open(path, container, videoType, audioType);
                Fragmented = container == PInvoke.MFTranscodeContainerType_FMPEG4;
                return;
            }
            catch (COMException e)
            {
                last = e;
                if (File.Exists(path)) File.Delete(path);
            }
        }
        throw last!;
    }

    public bool Fragmented { get; }

    private static (IMFSinkWriter, uint, uint?) Open(string path, Guid container, IMFMediaType videoType, IMFMediaType? audioType)
    {
        var attributes = MfInterop.CreateAttributes(2);
        IMFSinkWriter writer;
        try
        {
            attributes.SetGuid(PInvoke.MF_TRANSCODE_CONTAINERTYPE, container);
            attributes.SetUInt32(PInvoke.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            PInvoke.MFCreateSinkWriterFromURL(path, null, attributes, out writer).ThrowOnFailure();
        }
        finally
        {
            MfInterop.Release(attributes);
        }
        try
        {
            writer.AddStream(videoType, out var video);
            writer.SetInputMediaType(video, videoType, null);
            uint? audio = null;
            if (audioType != null)
            {
                writer.AddStream(audioType, out var a);
                writer.SetInputMediaType(a, audioType, null);
                audio = a;
            }
            writer.BeginWriting();
            return (writer, video, audio);
        }
        catch
        {
            MfInterop.Release(writer);
            throw;
        }
    }

    public bool HasAudio => _audio != null;

    public void WriteVideo(IMFSample sample) => _writer!.WriteSample(_video, sample);

    public void WriteAudio(IMFSample sample)
    {
        if (_audio is { } audio) _writer!.WriteSample(audio, sample);
    }

    /// <summary>Writes the index (moov or the last fragment) and closes the file.</summary>
    public void Finish()
    {
        if (_writer == null) return;
        try
        {
            _writer.Finalize();
        }
        finally
        {
            MfInterop.Release(_writer);
            _writer = null;
        }
    }

    public void Dispose()
    {
        MfInterop.Release(_writer);
        _writer = null;
    }
}
