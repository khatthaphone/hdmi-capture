package com.example.hdmicaptain.capture

import android.content.Context
import com.example.hdmicaptain.model.FormatChoice
import kotlin.math.roundToInt

/** The real [CaptureDriver]: our UVC stack for USB cards, Camera2 for Camera2 external cameras. */
class AndroidCaptureDriver(
    private val context: Context,
    private val catalog: UsbCardCatalog,
    /** The renderer's frame ring (VideoRenderer.ringHandle). */
    private val ringHandle: () -> Long,
    /** Receives each opened card's descriptor JSON (DEBUG dump for docs/research/devices). */
    private val onDescribed: (String) -> Unit = {},
) : CaptureDriver {
    private var uvc: UvcSource? = null
    private var camera2: Camera2Source? = null
    private var camera2Id: String? = null
    @Volatile private var camera2Error = false

    override fun open(card: Card): CaptureDriver.Opened {
        close()
        return when (card) {
            is UsbCard -> {
                val connection = catalog.open(card) ?: throw UvcSource.UvcException("Couldn't open ${card.name}")
                val source = UvcSource()
                try {
                    source.open(connection)
                    val d = source.describe()
                    onDescribed(d.json)
                    uvc = source
                    CaptureDriver.Opened(d.formats, d.link, d.speed)
                } catch (e: Exception) {
                    source.release()
                    throw e
                }
            }
            is Camera2Card -> {
                camera2 = Camera2Source(context)
                camera2Id = card.cameraId
                CaptureDriver.Opened(card.formats, link = null, speed = "camera2")
            }
            else -> throw IllegalArgumentException("Unknown card ${card.key}")
        }
    }

    override fun start(choice: FormatChoice) {
        uvc?.start(choice, ringHandle())
        camera2?.let { cam ->
            camera2Error = false
            cam.start(camera2Id!!, choice.format, choice.frameRate.roundToInt(), ringHandle()) { camera2Error = true }
        }
    }

    override fun progress(): CaptureDriver.Progress {
        uvc?.stats()?.let { return CaptureDriver.Progress(it.frames + it.decoded, it.lastFrameNanos) }
        // A Camera2 error stops frames; the stall rule then restarts it.
        camera2?.let { return CaptureDriver.Progress(it.framesDelivered, if (camera2Error) 0 else it.lastFrameNanos) }
        return CaptureDriver.Progress(0, 0)
    }

    override fun close() {
        uvc?.release()
        uvc = null
        camera2?.release()
        camera2 = null
    }
}
