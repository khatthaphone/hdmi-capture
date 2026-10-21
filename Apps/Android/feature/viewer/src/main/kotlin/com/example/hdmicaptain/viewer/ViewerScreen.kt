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
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.delay

/**
 * The viewer: video surface + status screens + control bar (task 078).
 * Today: the video when a source is live (or a test pattern), otherwise a status card.
 */
@Composable
fun ViewerScreen(
    renderer: VideoRenderer,
    showVideo: Boolean,
    status: String,
    statsLine: (() -> String?)? = null,
    modifier: Modifier = Modifier,
) {
    Box(modifier.fillMaxSize().background(Color.Black)) {
        // Always composed, so the surface (and EGL) survive status changes.
        VideoSurface(renderer, Modifier.fillMaxSize())
        if (!showVideo) ConnectCard(status, Modifier.align(Alignment.Center))
        if (statsLine != null) StatsOverlay(renderer, statsLine, Modifier.align(Alignment.TopStart).padding(16.dp))
    }
}

@Composable
private fun ConnectCard(status: String, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.fillMaxSize().background(Color.Black).padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(status, style = MaterialTheme.typography.titleLarge, color = Color.White, textAlign = TextAlign.Center)
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
private fun StatsOverlay(renderer: VideoRenderer, extra: () -> String?, modifier: Modifier = Modifier) {
    var text by remember { mutableStateOf("") }
    LaunchedEffect(renderer) {
        var last = renderer.stats()
        while (true) {
            delay(500)
            val now = renderer.stats()
            val fpsIn = (now.framesIn - last.framesIn) * 2
            val fpsOut = (now.framesDrawn - last.framesDrawn) * 2
            text = "${now.width}×${now.height}  in $fpsIn fps  shown $fpsOut fps  ${"%.1f".format(now.latencyNanos / 1e6)} ms" +
                (extra()?.let { "\n$it" } ?: "")
            last = now
        }
    }
    Text(text, modifier.background(Color.Black.copy(alpha = 0.6f)).padding(8.dp), color = Color.White, style = MaterialTheme.typography.labelMedium)
}
