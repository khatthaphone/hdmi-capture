namespace HDMICaptain.Media.Capture;

/// <summary>
/// The HRESULTs capture maps to <c>CaptureState</c> (windows.md §2d). Values copied from <c>mferror.h</c> /
/// <c>winerror.h</c> in Windows SDK 10.0.26100; CsWin32 doesn't project mferror.h.
/// </summary>
public static class MfErrors
{
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);
    public const int Shutdown = unchecked((int)0xC00D3E85);
    public const int DeviceInvalidated = unchecked((int)0xC00D3EA2); // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED
    public const int DevicePreempted = unchecked((int)0xC00D3EA3); // MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED
    public const int HwMftFailedStartStreaming = unchecked((int)0xC00D3704);
    public const int VideoDeviceLocked = unchecked((int)0xC00D4E24);
    public const int AccessDenied = unchecked((int)0x80070005); // E_ACCESSDENIED
    public const int DeviceNotConnected = unchecked((int)0x8007048F); // HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_CONNECTED)
    public const int DeviceRemoved = unchecked((int)0x80070651); // HRESULT_FROM_WIN32(ERROR_DEVICE_REMOVED)
    public const int SharingViolation = unchecked((int)0x80070020); // HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)

    public static string Name(int hr) => hr switch
    {
        0 => "S_OK",
        NoMoreTypes => "MF_E_NO_MORE_TYPES",
        Shutdown => "MF_E_SHUTDOWN",
        DeviceInvalidated => "MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED",
        DevicePreempted => "MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED",
        HwMftFailedStartStreaming => "MF_E_HW_MFT_FAILED_START_STREAMING",
        VideoDeviceLocked => "MF_E_VIDEO_DEVICE_LOCKED",
        AccessDenied => "E_ACCESSDENIED",
        DeviceNotConnected => "ERROR_DEVICE_NOT_CONNECTED",
        DeviceRemoved => "ERROR_DEVICE_REMOVED",
        SharingViolation => "ERROR_SHARING_VIOLATION",
        _ => $"0x{hr:X8}",
    };
}
