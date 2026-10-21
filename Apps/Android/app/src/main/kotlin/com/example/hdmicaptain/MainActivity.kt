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
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.viewer.ViewerScreen
import java.io.File

class MainActivity : ComponentActivity() {
    private val graph by lazy { CaptureGraph(applicationContext, lifecycleScope) }
    private val renderer get() = graph.renderer

    // UVC cards need CAMERA (Android 9+) before the USB grant; RECORD_AUDIO for the card's sound.
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { startCapture() }

    private fun startCapture() {
        graph.controller.start()
        // Capture follows visibility (STARTED/STOPPED), not focus: split screen and PiP keep it live.
        lifecycle.addObserver(LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> graph.controller.onVisible()
                Lifecycle.Event.ON_STOP -> graph.controller.onHidden()
                else -> Unit
            }
        })
    }

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
            startCapture()
        } else {
            permissions.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO))
        }
        setContent {
            val phase by graph.controller.phase.collectAsState()
            MaterialTheme(colorScheme = darkColorScheme()) {
                ViewerScreen(
                    renderer = renderer,
                    showVideo = pattern != null || phase is ViewerPhase.Live,
                    status = phase.title(),
                    statsLine = if (showStats) ({ statsLine() }) else null,
                )
            }
        }
        val soakMinutes = intent.getIntExtra("qaSoakMinutes", 0)
        if (BuildConfig.DEBUG && soakMinutes > 0) QASoak(this, graph).start(lifecycleScope, soakMinutes)
        if (BuildConfig.DEBUG && intent.getStringExtra("qaAction") == "screenshot") {
            window.decorView.postDelayed({ saveQAScreenshot() }, 1500)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        graph.catalog.refresh()  // launched again by a USB attach
    }

    override fun onDestroy() {
        graph.release()
        super.onDestroy()
    }

    private fun statsLine(): String = graph.audioLevels?.let { a ->
        "audio ${"%.0f".format(a.bufferedMs)} ms buffered, ${a.underruns} underruns, peak ${"%.2f".format(a.peakLeft)}/${"%.2f".format(a.peakRight)}"
    } ?: ""

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

/** Status line until task 078's state screens. */
private fun ViewerPhase.title(): String = when (this) {
    ViewerPhase.Scanning -> "Connect a capture card"
    is ViewerPhase.UsbPermission -> if (denied) "Allow USB access to $cardName to see its picture" else "Allow USB access to $cardName"
    is ViewerPhase.Waiting -> "$cardName is disconnected"
    is ViewerPhase.Connecting -> if (slow) "Connecting to $cardName… (taking longer than usual)" else "Connecting to $cardName…"
    is ViewerPhase.Live -> "${info.deviceName} · ${info.width}×${info.height} ${info.fourCC} @ ${info.frameRate} · $speed"
    is ViewerPhase.Recovering -> if (reason == "No signal") "No HDMI signal" else "Reconnecting… ($reason)"
    is ViewerPhase.Paused -> "Paused"
    is ViewerPhase.Failed -> message
}
