using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HDMICaptain.Core;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Devices.Usb;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace HDMICaptain.Media.Capture;

/// <summary>Where a capture interface sits in the PnP tree: the USB device behind it and the link it runs at.</summary>
public sealed record UsbDetails(
    string InstanceId,
    string? UsbDeviceInstanceId,
    int? VendorId,
    int? ProductId,
    string? Serial,
    Guid? ContainerId,
    string? BusReportedName,
    UsbLink? Link,
    string? LinkSpeed);

/// <summary>
/// Walks from an MF symbolic link (a device interface) to the USB device node and asks its hub for the
/// negotiated speed (spec §13.2, windows.md §2c): the same link rule as Android's format-bandwidth fixture.
/// </summary>
public static partial class UsbTopology
{
    private const uint IoctlUsbGetNodeConnectionInformationExV2 = 0x22045C;
    private const uint GenericWrite = 0x40000000;

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();

    public static unsafe UsbDetails? Describe(string symbolicLink)
    {
        if (InterfaceString(symbolicLink, PInvoke.DEVPKEY_Device_InstanceId) is not { } instanceId) return null;
        uint node;
        fixed (char* id = instanceId)
        {
            if (PInvoke.CM_Locate_DevNode(out node, id, CM_LOCATE_DEVNODE_FLAGS.CM_LOCATE_DEVNODE_NORMAL) != CONFIGRET.CR_SUCCESS)
                return new UsbDetails(instanceId, null, null, null, null, null, null, null, null);
        }

        // A composite card's camera is an interface child (…&MI_00); the USB device is its parent.
        var usbNode = node;
        if (instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) && PInvoke.CM_Get_Parent(out var parent, node, 0) == CONFIGRET.CR_SUCCESS)
            usbNode = parent;
        var usbId = DeviceId(usbNode);

        int? vid = null, pid = null;
        if (VidPid().Match(usbId ?? instanceId) is { Success: true } m)
        {
            vid = Convert.ToInt32(m.Groups[1].Value, 16);
            pid = Convert.ToInt32(m.Groups[2].Value, 16);
        }
        // USB\VID_x&PID_y\<serial>: Windows makes up an instance path containing '&' when there is no serial.
        var lastPart = usbId?.Split('\\').LastOrDefault();
        var serial = lastPart is { Length: > 0 } && !lastPart.Contains('&') ? lastPart : null;

        var container = NodeGuid(usbNode, PInvoke.DEVPKEY_Device_ContainerId);
        var busName = NodeString(usbNode, PInvoke.DEVPKEY_Device_BusReportedDeviceDesc);
        var (link, speed) = LinkSpeed(usbNode);
        return new UsbDetails(instanceId, usbId, vid, pid, serial, container, busName, link, speed);
    }

    /// <summary>Asks the parent hub about the port (DEVPKEY_Device_Address) the device is plugged into.</summary>
    private static unsafe (UsbLink?, string?) LinkSpeed(uint usbNode)
    {
        if (PInvoke.CM_Get_Parent(out var hubNode, usbNode, 0) != CONFIGRET.CR_SUCCESS) return (null, null);
        if (NodeUInt32(usbNode, PInvoke.DEVPKEY_Device_Address) is not { } port || DeviceId(hubNode) is not { } hubId) return (null, null);
        if (InterfacePaths(PInvoke.GUID_DEVINTERFACE_USB_HUB, hubId).FirstOrDefault() is not { } hubPath) return (null, null);

        using var hub = PInvoke.CreateFile(hubPath, GenericWrite, FILE_SHARE_MODE.FILE_SHARE_WRITE, null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, null);
        if (hub.IsInvalid) return (null, null);

        var info = new USB_NODE_CONNECTION_INFORMATION_EX { ConnectionIndex = port };
        var size = (uint)sizeof(USB_NODE_CONNECTION_INFORMATION_EX);
        if (!PInvoke.DeviceIoControl((HANDLE)hub.DangerousGetHandle(), PInvoke.IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
                &info, size, &info, size, null, null))
            return (null, null);

        // EX reports at most high speed on some hubs; EX_V2 has the authoritative SuperSpeed flag (Windows 8+).
        var v2 = new USB_NODE_CONNECTION_INFORMATION_EX_V2
        {
            ConnectionIndex = port,
            Length = (uint)sizeof(USB_NODE_CONNECTION_INFORMATION_EX_V2),
        };
        v2.SupportedUsbProtocols._bitfield = 0b111; // USB 1.10, 2.00, 3.00
        var superSpeed = PInvoke.DeviceIoControl((HANDLE)hub.DangerousGetHandle(), IoctlUsbGetNodeConnectionInformationExV2,
                &v2, v2.Length, &v2, v2.Length, null, null)
            && (v2.Flags._bitfield & 0b11) != 0; // DeviceIsOperatingAtSuperSpeedOrHigher | …SuperSpeedPlusOrHigher

        return superSpeed || info.Speed >= 3 ? (UsbLink.Usb3, (v2.Flags._bitfield & 0b10) != 0 ? "super+" : "super")
            : info.Speed switch
            {
                2 => (UsbLink.Usb2, "high"),
                1 => (UsbLink.Usb2, "full"),
                0 => (UsbLink.Usb2, "low"),
                _ => (null, null),
            };
    }

    internal static unsafe List<string> InterfacePaths(Guid interfaceClass, string? deviceId)
    {
        fixed (char* id = deviceId)
        {
            if (PInvoke.CM_Get_Device_Interface_List_Size(out var length, interfaceClass, id,
                    CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CONFIGRET.CR_SUCCESS || length <= 1)
                return [];
            var buffer = new char[length];
            fixed (char* b = buffer)
            {
                if (PInvoke.CM_Get_Device_Interface_List(interfaceClass, id, b, length,
                        CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CONFIGRET.CR_SUCCESS)
                    return [];
            }
            return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
    }

    private static string? DeviceId(uint node)
    {
        Span<char> buffer = stackalloc char[400];
        return PInvoke.CM_Get_Device_IDW(node, buffer, 0) == CONFIGRET.CR_SUCCESS ? new string(buffer).TrimEnd('\0') : null;
    }

    private static string? InterfaceString(string path, in DEVPROPKEY key)
    {
        var size = 0u;
        PInvoke.CM_Get_Device_Interface_Property(path, key, out _, default, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        return PInvoke.CM_Get_Device_Interface_Property(path, key, out var type, buffer, ref size, 0) == CONFIGRET.CR_SUCCESS
            && type == DEVPROPTYPE.DEVPROP_TYPE_STRING
            ? MemoryMarshal.Cast<byte, char>(buffer).ToString().TrimEnd('\0') : null;
    }

    private static byte[]? NodeProperty(uint node, in DEVPROPKEY key, DEVPROPTYPE expected)
    {
        var size = 0u;
        PInvoke.CM_Get_DevNode_Property(node, key, out _, default, ref size, 0);
        if (size == 0) return null;
        var buffer = new byte[size];
        return PInvoke.CM_Get_DevNode_Property(node, key, out var type, buffer, ref size, 0) == CONFIGRET.CR_SUCCESS && type == expected
            ? buffer : null;
    }

    private static string? NodeString(uint node, in DEVPROPKEY key) =>
        NodeProperty(node, key, DEVPROPTYPE.DEVPROP_TYPE_STRING) is { } b ? MemoryMarshal.Cast<byte, char>(b).ToString().TrimEnd('\0') : null;

    private static Guid? NodeGuid(uint node, in DEVPROPKEY key) =>
        NodeProperty(node, key, DEVPROPTYPE.DEVPROP_TYPE_GUID) is { Length: 16 } b ? new Guid(b) : null;

    private static uint? NodeUInt32(uint node, in DEVPROPKEY key) =>
        NodeProperty(node, key, DEVPROPTYPE.DEVPROP_TYPE_UINT32) is { Length: 4 } b ? BitConverter.ToUInt32(b) : null;
}
