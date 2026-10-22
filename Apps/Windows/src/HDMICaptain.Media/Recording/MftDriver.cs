using System.Runtime.InteropServices;
using HDMICaptain.Media.Capture;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>Output collection shared by the encoders: ProcessOutput with MFT- or caller-allocated samples.</summary>
internal sealed unsafe class MftDriver(IMFTransform transform)
{
    private bool _providesSamples;
    private uint _bufferSize;

    public IMFTransform Transform { get; } = transform;

    /// <summary>Re-reads the output stream's allocation rules (after the types are set or changed).</summary>
    public void ReadStreamInfo()
    {
        MFT_OUTPUT_STREAM_INFO info;
        Transform.GetOutputStreamInfo(0, &info);
        _providesSamples = (info.dwFlags & (MfMedia.OutputProvidesSamples | MfMedia.OutputCanProvideSamples)) != 0;
        _bufferSize = info.cbSize > 0 ? info.cbSize : 1u << 20;
    }

    /// <summary>
    /// One ProcessOutput call. Returns the sample (the caller owns it), or null when the MFT needs more input.
    /// A format change is renegotiated (first available type) and reported through <paramref name="typeChanged"/>.
    /// </summary>
    public IMFSample? Pull(Action? typeChanged = null)
    {
        while (true)
        {
            var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
            IMFSample? allocated = null;
            if (!_providesSamples)
            {
                PInvoke.MFCreateMemoryBuffer(_bufferSize, out var buffer).ThrowOnFailure();
                allocated = MfMedia.Wrap(buffer, 0, 0);
                MfInterop.Release(buffer);
                buffers[0].pSample = allocated;
            }
            try
            {
                Transform.ProcessOutput(0, 1, buffers, out _);
            }
            catch (COMException e) when (e.HResult == MfMedia.TransformNeedMoreInput)
            {
                MfInterop.Release(allocated);
                return null;
            }
            catch (COMException e) when (e.HResult == MfMedia.TransformStreamChange)
            {
                MfInterop.Release(allocated);
                Transform.GetOutputAvailableType(0, 0, out var type);
                Transform.SetOutputType(0, type, 0);
                MfInterop.Release(type);
                ReadStreamInfo();
                typeChanged?.Invoke();
                continue;
            }
            catch
            {
                MfInterop.Release(allocated);
                throw;
            }
            MfInterop.Release(buffers[0].pEvents);
            var sample = buffers[0].pSample;
            if (allocated != null && !ReferenceEquals(sample, allocated)) MfInterop.Release(allocated);
            return sample;
        }
    }

    public void Message(MFT_MESSAGE_TYPE message, nuint param = 0) => Transform.ProcessMessage(message, param);
}
