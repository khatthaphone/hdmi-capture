package com.example.hdmicaptain.capture

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbConstants
import android.hardware.usb.UsbDevice
import android.hardware.usb.UsbManager
import android.os.Build
import androidx.core.content.ContextCompat
import com.example.hdmicaptain.model.DeviceKey
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/** A USB device with a UVC video interface: an HDMI capture card (or a webcam). */
data class UsbCard(val device: UsbDevice, override val key: String, override val name: String) : Card {
    val vendorId: Int get() = device.vendorId
    val productId: Int get() = device.productId
}

/** A capture card Android exposes through Camera2 (spec §12.3 secondary path). */
data class Camera2Card(val cameraId: String, val formats: List<com.example.hdmicaptain.model.FormatDescriptor>) : Card {
    override val key = "camera2:$cameraId"
    override val name = "External camera"
}

/**
 * Lists attached UVC devices and follows plug/unplug (spec §12.2). The Android twin of
 * CaptureCore's DeviceCatalog; most recently attached last. When no USB card is visible, Camera2
 * external cameras are listed instead (USB not passed through, e.g. ChromeOS).
 */
class UsbCardCatalog(private val context: Context) : CardCatalog {
    private val usb = context.getSystemService(UsbManager::class.java)
    private val _cards = MutableStateFlow<List<Card>>(emptyList())
    override val cards: StateFlow<List<Card>> = _cards.asStateFlow()

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) = refresh()
    }

    override fun start() {
        val filter = IntentFilter().apply {
            addAction(UsbManager.ACTION_USB_DEVICE_ATTACHED)
            addAction(UsbManager.ACTION_USB_DEVICE_DETACHED)
        }
        ContextCompat.registerReceiver(context, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        refresh()
    }

    override fun stop() = context.unregisterReceiver(receiver)

    /** Re-reads the device list (also call when the activity gets an attach intent). */
    fun refresh() {
        val previous = _cards.value.map { it.key }
        val usbCards: List<Card> = usb.deviceList.values.filter(::isUvc).map { card(it) }
        val now = usbCards.ifEmpty { camera2Cards() }
        // Keep the known order; newly attached cards go last.
        _cards.value = now.sortedBy { previous.indexOf(it.key).let { i -> if (i < 0) Int.MAX_VALUE else i } }
    }

    private fun camera2Cards(): List<Card> = runCatching {
        Camera2Source(context).let { cam -> cam.externalCameras().also { cam.release() } }
            .filter { it.formats.isNotEmpty() }.map { Camera2Card(it.id, it.formats) }
    }.getOrDefault(emptyList())

    override fun hasPermission(card: Card) = card !is UsbCard || usb.hasPermission(card.device)

    override suspend fun requestPermission(card: Card): Boolean = card !is UsbCard || requestUsbPermission(card)

    /** Shows Android's "Allow HDMI Captain to access…" dialog; true when granted. */
    private suspend fun requestUsbPermission(card: UsbCard): Boolean {
        if (usb.hasPermission(card.device)) return true
        val action = "${context.packageName}.USB_PERMISSION"
        return suspendCancellableCoroutine { continuation ->
            val receiver = object : BroadcastReceiver() {
                override fun onReceive(context: Context, intent: Intent) {
                    context.unregisterReceiver(this)
                    if (continuation.isActive) continuation.resume(intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false))
                }
            }
            ContextCompat.registerReceiver(context, receiver, IntentFilter(action), ContextCompat.RECEIVER_NOT_EXPORTED)
            continuation.invokeOnCancellation { runCatching { context.unregisterReceiver(receiver) } }
            // Explicit + mutable: the system fills in EXTRA_DEVICE and EXTRA_PERMISSION_GRANTED (Android 14+ rule).
            val intent = Intent(action).setPackage(context.packageName)
            val flags = if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0
            usb.requestPermission(card.device, PendingIntent.getBroadcast(context, 0, intent, flags))
        }
    }

    /** Opens the device for libusb; null if permission is missing or the card just went away. */
    fun open(card: UsbCard) = usb.openDevice(card.device)

    private fun card(device: UsbDevice) = UsbCard(
        device = device,
        key = DeviceKey.of(device.vendorId, device.productId, runCatching { device.serialNumber }.getOrNull(), device.productName),
        name = device.productName?.takeIf { it.isNotBlank() } ?: "USB capture card",
    )

    companion object {
        fun isUvc(device: UsbDevice) =
            (0 until device.interfaceCount).any { device.getInterface(it).interfaceClass == UsbConstants.USB_CLASS_VIDEO }
    }
}
