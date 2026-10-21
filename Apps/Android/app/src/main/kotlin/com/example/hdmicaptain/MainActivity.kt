package com.example.hdmicaptain

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.lifecycle.lifecycleScope
import com.example.hdmicaptain.model.RenderOptions
import com.example.hdmicaptain.model.TestPattern
import com.example.hdmicaptain.render.VideoRenderer
import com.example.hdmicaptain.viewer.ViewerScreen
import java.io.File

class MainActivity : ComponentActivity() {
    private val renderer by lazy { VideoRenderer() }
    private val capture by lazy { CaptureCoordinator(applicationContext, lifecycleScope, renderer) }

    // UVC cards need CAMERA (Android 9+) before the USB grant; RECORD_AUDIO for the card's sound.
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { capture.start() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // DEBUG launch extras mirror the Apple QA arguments: --es testPattern smpteBars, --ez qaShowStats true.
        val pattern = TestPattern.fromKey(intent.getStringExtra("testPattern"))
        val showStats = BuildConfig.DEBUG && intent.getBooleanExtra("qaShowStats", false)
        renderer.setOptions(RenderOptions())
        if (pattern != null) {
            renderer.startTestPattern(pattern)
        } else if (hasCapturePermissions()) {
            capture.start()
        } else {
            permissions.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO))
        }
        setContent {
            val live by capture.live.collectAsState()
            val status by capture.status.collectAsState()
            MaterialTheme(colorScheme = darkColorScheme()) {
                ViewerScreen(
                    renderer = renderer,
                    showVideo = pattern != null || live,
                    status = status,
                    statsLine = if (showStats) ({ capture.uvcStats?.let { "uvc ${it.frames} raw, ${it.shortFrames} short · mjpeg ${it.compressedFrames} in, ${it.decoded} decoded, ${it.decodeDropped} dropped, ${it.decodeFailed} bad, ${"%.1f".format(it.avgDecodeNanos / 1e6)} ms" + (capture.audioLevels?.let { a -> "\naudio ${"%.0f".format(a.bufferedMs)} ms buffered, ${a.underruns} underruns, peak ${"%.2f".format(a.peakLeft)}/${"%.2f".format(a.peakRight)}" } ?: "") } }) else null,
                )
            }
        }
        val soakMinutes = intent.getIntExtra("qaSoakMinutes", 0)
        if (BuildConfig.DEBUG && soakMinutes > 0) QASoak(this, renderer, capture).start(lifecycleScope, soakMinutes)
        if (BuildConfig.DEBUG && intent.getStringExtra("qaAction") == "screenshot") {
            window.decorView.postDelayed({ saveQAScreenshot() }, 1500)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        capture.catalog.refresh()  // launched again by a USB attach
    }

    override fun onDestroy() {
        capture.stop()
        renderer.release()
        super.onDestroy()
    }

    private fun hasCapturePermissions() = listOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO)
        .all { checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }

    /** DEBUG: writes the renderer's clean frame to app files (pull with `adb shell run-as`). */
    private fun saveQAScreenshot() {
        val bitmap = renderer.screenshot()
        if (bitmap == null) {
            Log.w("HDMICaptain", "qa screenshot: no frame")
            return
        }
        val file = File(filesDir, "qa-frame.png")
        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        Log.i("HDMICaptain", "qa screenshot: ${file.absolutePath} ${bitmap.width}x${bitmap.height}")
    }
}
