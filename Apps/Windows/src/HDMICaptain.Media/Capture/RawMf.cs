using System.Runtime.InteropServices;

namespace HDMICaptain.Media.Capture;

/// <summary>
/// The per-frame Media Foundation calls made straight through the vtable (task 101: zero allocations per frame).
/// Built-in COM interop allocates an RCW for every sample and buffer, about 500 bytes a frame; these don't.
/// Slot numbers follow the interface declarations in mfobjects.h (IUnknown = 0–2).
/// </summary>
internal static unsafe class RawMf
{
    public static readonly Guid IidMF2DBuffer = new("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB");

    // IMFSample : IMFAttributes (30 methods after IUnknown): GetBufferByIndex = 3 + 30 + 7.
    private const int SampleGetBufferByIndex = 40;
    private const int SampleConvertToContiguousBuffer = 41;
    // IMFMediaBuffer
    private const int BufferLock = 3, BufferUnlock = 4, BufferGetCurrentLength = 5;
    // IMF2DBuffer
    private const int Buffer2DLock2D = 3, Buffer2DUnlock2D = 4;

    private static void** Vtable(nint unknown) => *(void***)unknown;

    public static uint Release(nint unknown) => ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(unknown)[2])(unknown);

    public static int GetBufferByIndex(nint sample, uint index, out nint buffer)
    {
        nint result;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtable(sample)[SampleGetBufferByIndex])(sample, index, &result);
        buffer = result;
        return hr;
    }

    public static int ConvertToContiguousBuffer(nint sample, out nint buffer)
    {
        nint result;
        var hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(sample)[SampleConvertToContiguousBuffer])(sample, &result);
        buffer = result;
        return hr;
    }

    public static int Lock(nint buffer, byte** data, uint* maxLength, uint* currentLength) =>
        ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Vtable(buffer)[BufferLock])(buffer, data, maxLength, currentLength);

    public static int Unlock(nint buffer) => ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(buffer)[BufferUnlock])(buffer);

    public static int GetCurrentLength(nint buffer, uint* length) =>
        ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtable(buffer)[BufferGetCurrentLength])(buffer, length);

    /// <summary>IMF2DBuffer of a media buffer, or 0 when it isn't one (caller releases a non-zero result).</summary>
    public static nint As2D(nint buffer) => Marshal.QueryInterface(buffer, in IidMF2DBuffer, out var twoD) >= 0 ? twoD : 0;

    public static int Lock2D(nint buffer2D, byte** scanline0, int* pitch) =>
        ((delegate* unmanaged[Stdcall]<nint, byte**, int*, int>)Vtable(buffer2D)[Buffer2DLock2D])(buffer2D, scanline0, pitch);

    public static int Unlock2D(nint buffer2D) => ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(buffer2D)[Buffer2DUnlock2D])(buffer2D);
}

/// <summary>
/// IMFSourceReaderCallback declared with raw pointers, so the reader's calls reach managed code without an RCW per
/// sample. Same IID as the CsWin32 projection; the reader only ever sees our CCW.
/// </summary>
[ComImport, Guid("DEEC8D99-FA1D-4D82-84C2-2C8969944867"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRawSourceReaderCallback
{
    [PreserveSig]
    int OnReadSample(int hrStatus, uint streamIndex, uint streamFlags, long timestamp, nint sample);

    [PreserveSig]
    int OnFlush(uint streamIndex);

    [PreserveSig]
    int OnEvent(uint streamIndex, nint mediaEvent);
}
