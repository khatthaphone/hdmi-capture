package com.example.hdmicaptain.viewer

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.model.RenderOptions
import com.example.hdmicaptain.model.TestPattern
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.delay

/**
 * The viewer: video surface + status screens + control bar (task 078).
 * Today: a test pattern when one is requested, otherwise the "Connect a capture card" card.
 */
@Composable
fun ViewerScreen(
    testPattern: TestPattern?,
    showStats: Boolean,
    onRenderer: (VideoRenderer) -> Unit = {},
    modifier: Modifier = Modifier,
) {
    val renderer = remember { VideoRenderer() }
    DisposableEffect(renderer) {
        onRenderer(renderer)
        onDispose { renderer.release() }
    }
    LaunchedEffect(testPattern) {
        renderer.setOptions(RenderOptions())
        if (testPattern != null) renderer.startTestPattern(testPattern) else renderer.stopSource()
    }
    Box(modifier.fillMaxSize().background(Color.Black)) {
        if (testPattern != null) {
            VideoSurface(renderer, Modifier.fillMaxSize())
            if (showStats) StatsOverlay(renderer, Modifier.align(Alignment.TopStart).padding(16.dp))
        } else {
            ConnectCard(Modifier.align(Alignment.Center))
        }
    }
}

@Composable
private fun ConnectCard(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Connect a capture card", style = MaterialTheme.typography.titleLarge, color = Color.White)
        Text(
            "Plug an HDMI capture card into this device. The picture appears automatically.",
            style = MaterialTheme.typography.bodyLarge,
            color = Color.White.copy(alpha = 0.75f),
            textAlign = TextAlign.Center,
        )
    }
}

/** Diagnostics, sampled twice a second (never per frame). */
@Composable
private fun StatsOverlay(renderer: VideoRenderer, modifier: Modifier = Modifier) {
    var text by remember { mutableStateOf("") }
    LaunchedEffect(renderer) {
        var last = renderer.stats()
        while (true) {
            delay(500)
            val now = renderer.stats()
            val fpsIn = (now.framesIn - last.framesIn) * 2
            val fpsOut = (now.framesDrawn - last.framesDrawn) * 2
            text = "${now.width}×${now.height}  in $fpsIn fps  shown $fpsOut fps  ${"%.1f".format(now.latencyNanos / 1e6)} ms"
            last = now
        }
    }
    Text(text, modifier.background(Color.Black.copy(alpha = 0.6f)).padding(8.dp), color = Color.White, style = MaterialTheme.typography.labelMedium)
}
