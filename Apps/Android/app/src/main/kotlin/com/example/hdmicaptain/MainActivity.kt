package com.example.hdmicaptain

import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import com.example.hdmicaptain.model.TestPattern
import com.example.hdmicaptain.render.VideoRenderer
import com.example.hdmicaptain.viewer.ViewerScreen
import java.io.File

class MainActivity : ComponentActivity() {
    private var renderer: VideoRenderer? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // DEBUG launch extras mirror the Apple QA arguments: --es testPattern smpteBars, --ez qaShowStats true.
        val pattern = TestPattern.fromKey(intent.getStringExtra("testPattern"))
        val showStats = BuildConfig.DEBUG && intent.getBooleanExtra("qaShowStats", false)
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                ViewerScreen(testPattern = pattern, showStats = showStats, onRenderer = { renderer = it })
            }
        }
        if (BuildConfig.DEBUG && intent.getStringExtra("qaAction") == "screenshot") {
            window.decorView.postDelayed({ saveQAScreenshot() }, 1500)
        }
    }

    /** DEBUG: writes the renderer's clean frame to app files (pull with `adb shell run-as`). */
    private fun saveQAScreenshot() {
        val bitmap = renderer?.screenshot()
        if (bitmap == null) {
            Log.w("HDMICaptain", "qa screenshot: no frame")
            return
        }
        val file = File(filesDir, "qa-frame.png")
        file.outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
        Log.i("HDMICaptain", "qa screenshot: ${file.absolutePath} ${bitmap.width}x${bitmap.height}")
    }
}
