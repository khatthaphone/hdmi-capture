namespace HDMICaptain.Core;

/// <summary>
/// Identity of a capture card that survives re-enumeration (port of the Kotlin DeviceKey and
/// CaptureDeviceInfo.stableKey): vendor + product ID, plus the serial when the card reports one,
/// else the product name. Presets and "last used device" are stored under this key.
/// </summary>
public static class DeviceKey
{
    public static string Of(int vendorId, int productId, string? serial, string? productName)
    {
        var id = $"usb:{vendorId:X4}:{productId:X4}";
        if (!string.IsNullOrWhiteSpace(serial)) return $"{id}|serial:{serial}";
        if (!string.IsNullOrWhiteSpace(productName)) return $"{id}|name:{productName}";
        return id;
    }
}
