package com.example.hdmicaptain.capture

import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.util.Range
import com.example.hdmicaptain.model.FormatDescriptor
import com.example.hdmicaptain.model.FrameRateRange
import org.json.JSONArray
import org.json.JSONObject

/**
 * Secondary capture path (spec §12.3): a USB camera that Android itself exposes through Camera2
 * as LENS_FACING_EXTERNAL (some Chromebooks, a few OEM builds). Lower frame rates than our own
 * UVC stack, so it is used only when no USB card can be opened directly.
 */
class Camera2Source(private val context: Context) {
    data class ExternalCamera(val id: String, val formats: List<FormatDescriptor>)

    private val cameras = context.getSystemService(CameraManager::class.java)
    private val thread = HandlerThread("camera2-capture").apply { start() }
    private val handler = Handler(thread.looper)
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var reader: ImageReader? = null

    @Volatile var framesDelivered = 0L
        private set

    /** External cameras and what they offer (YUV_420_888 sizes × the camera's AE fps ranges). */
    fun externalCameras(): List<ExternalCamera> = cameras.cameraIdList.mapNotNull { id ->
        val c = cameras.getCameraCharacteristics(id)
        if (c.get(CameraCharacteristics.LENS_FACING) != CameraCharacteristics.LENS_FACING_EXTERNAL) return@mapNotNull null
        val map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return@mapNotNull null
        val ranges = c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES).orEmpty()
        val formats = map.getOutputSizes(ImageFormat.YUV_420_888).orEmpty().map { size ->
            // Highest fps the camera can sustain at this size, from the minimum frame duration.
            val minDuration = map.getOutputMinFrameDuration(ImageFormat.YUV_420_888, size)
            val cap = if (minDuration > 0) 1e9 / minDuration else Double.MAX_VALUE
            val rates = ranges.map { it.upper.toDouble() }.filter { it <= cap + 0.5 }.distinct().ifEmpty { listOf(minOf(cap, 30.0)) }
            FormatDescriptor(size.width, size.height, "I420", rates.map { FrameRateRange(it, it) })
        }
        ExternalCamera(id, formats)
    }

    /** JSON for docs/research (task 073): feature flag, cameras, sizes and fps. */
    fun probe(): String = JSONObject().apply {
        put("featureCameraExternal", context.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_EXTERNAL))
        put("cameraIds", JSONArray(cameras.cameraIdList.toList()))
        put("external", JSONArray(externalCameras().map { cam ->
            JSONObject().put("id", cam.id).put("formats", JSONArray(cam.formats.map {
                JSONObject().put("w", it.width).put("h", it.height).put("rates", JSONArray(it.frameRateRanges.map { r -> r.max }))
            }))
        }))
    }.toString(2)

    /** Streams [format] from camera [id] into the renderer's frame ring ([ringHandle]). */
    @SuppressLint("MissingPermission")  // CAMERA is requested before capture starts
    fun start(id: String, format: FormatDescriptor, frameRate: Int, ringHandle: Long, onError: (String) -> Unit) {
        stop()
        val imageReader = ImageReader.newInstance(format.width, format.height, ImageFormat.YUV_420_888, 3)
        imageReader.setOnImageAvailableListener({ r ->
            val image = r.acquireLatestImage() ?: return@setOnImageAvailableListener
            val (y, u, v) = image.planes
            nativePushYuv420(
                ringHandle, image.width, image.height,
                y.buffer, y.rowStride, u.buffer, v.buffer, u.rowStride, u.pixelStride,
            )
            framesDelivered++
            image.close()
        }, handler)
        reader = imageReader
        cameras.openCamera(id, object : CameraDevice.StateCallback() {
            override fun onOpened(camera: CameraDevice) {
                device = camera
                val request = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                    addTarget(imageReader.surface)
                    set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(frameRate, frameRate))
                }
                @Suppress("DEPRECATION")  // the SessionConfiguration API needs an Executor; same behaviour
                camera.createCaptureSession(listOf(imageReader.surface), object : CameraCaptureSession.StateCallback() {
                    override fun onConfigured(s: CameraCaptureSession) {
                        session = s
                        s.setRepeatingRequest(request.build(), null, handler)
                    }

                    override fun onConfigureFailed(s: CameraCaptureSession) = onError("Camera2 session failed")
                }, handler)
            }

            override fun onDisconnected(camera: CameraDevice) {
                camera.close()
                onError("Camera disconnected")
            }

            override fun onError(camera: CameraDevice, error: Int) {
                camera.close()
                onError("Camera error $error")
            }
        }, handler)
    }

    fun stop() {
        session?.close()
        session = null
        device?.close()
        device = null
        reader?.close()
        reader = null
    }

    fun release() {
        stop()
        thread.quitSafely()
    }

    private companion object {
        init {
            System.loadLibrary("hdmicapture")
        }

        @JvmStatic external fun nativePushYuv420(
            ring: Long, width: Int, height: Int,
            y: java.nio.ByteBuffer, yRowStride: Int,
            u: java.nio.ByteBuffer, v: java.nio.ByteBuffer, uvRowStride: Int, uvPixelStride: Int,
        )
    }
}
