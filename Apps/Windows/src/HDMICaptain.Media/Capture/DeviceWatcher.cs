using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;

namespace HDMICaptain.Media.Capture;

/// <summary>
/// Camera hot-plug through <c>CM_Register_Notification</c> on KSCATEGORY_VIDEO_CAMERA, the interface class of
/// the symbolic links MF hands out (spec §13.2). Events arrive on a system thread pool thread.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    /// <summary>KSCATEGORY_VIDEO_CAMERA: the tail of every MF video capture symbolic link.</summary>
    public static readonly Guid VideoCameraCategory = new("E5323777-F976-4F5B-9B55-B94699C46E44");

    private readonly PCM_NOTIFY_CALLBACK _callback; // rooted for the registration's lifetime
    private readonly CM_Unregister_NotificationSafeHandle _registration;

    public unsafe DeviceWatcher()
    {
        _callback = OnNotify;
        var filter = new CM_NOTIFY_FILTER
        {
            cbSize = (uint)sizeof(CM_NOTIFY_FILTER),
            FilterType = CM_NOTIFY_FILTER_TYPE.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
        };
        filter.u.DeviceInterface.ClassGuid = VideoCameraCategory;
        var result = PInvoke.CM_Register_Notification(filter, null, _callback, out _registration);
        if (result != CONFIGRET.CR_SUCCESS) throw new InvalidOperationException($"CM_Register_Notification failed: {result}");
    }

    /// <summary>A camera interface appeared (symbolic link).</summary>
    public event Action<string>? Arrived;

    /// <summary>A camera interface went away (symbolic link): unplug, driver disable.</summary>
    public event Action<string>? Removed;

    private unsafe uint OnNotify(HCMNOTIFICATION notify, void* context, CM_NOTIFY_ACTION action, CM_NOTIFY_EVENT_DATA* data, uint size)
    {
        var link = new string(data->u.DeviceInterface.SymbolicLink.AsSpan(
            (int)(size - (uint)((byte*)&data->u.DeviceInterface.SymbolicLink - (byte*)data)) / sizeof(char))).TrimEnd('\0');
        switch (action)
        {
            case CM_NOTIFY_ACTION.CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL:
                Arrived?.Invoke(link);
                break;
            case CM_NOTIFY_ACTION.CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL:
                Removed?.Invoke(link);
                break;
        }
        return 0; // ERROR_SUCCESS
    }

    /// <summary>MF and CM spell the same link with different case; compare this way.</summary>
    public static bool SameLink(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _registration.Dispose(); // CM_Unregister_Notification waits for running callbacks
}
