package com.example.hdmicaptain.model

/**
 * Identity of a capture card that survives re-enumeration (the Android twin of
 * `CaptureDeviceInfo.stableKey`): vendor + product ID, plus the serial when the card reports one,
 * else the product name. Presets and "last used device" are stored under this key.
 */
object DeviceKey {
    fun of(vendorId: Int, productId: Int, serial: String?, productName: String?): String {
        val id = "usb:%04X:%04X".format(vendorId, productId)
        return when {
            !serial.isNullOrBlank() -> "$id|serial:$serial"
            !productName.isNullOrBlank() -> "$id|name:$productName"
            else -> id
        }
    }
}
