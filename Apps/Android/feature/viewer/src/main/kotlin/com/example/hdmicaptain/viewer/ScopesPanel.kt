package com.example.hdmicaptain.viewer

import android.graphics.Bitmap
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.model.ScopeData
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

/**
 * Histogram, waveform and vectorscope of the live picture (the Apple ScopesPanel), refreshed
 * 4× a second off the main thread. UI only: never part of screenshots or recordings.
 */
@Composable
fun ScopesPanel(renderer: VideoRenderer, modifier: Modifier = Modifier) {
    var images by remember { mutableStateOf<Triple<ImageBitmap, ImageBitmap, ImageBitmap>?>(null) }
    DisposableEffect(renderer) {
        renderer.setScopes(true)
        onDispose { renderer.setScopes(false) }
    }
    LaunchedEffect(renderer) {
        val grid = ByteArray(VideoRenderer.SCOPE_COLUMNS * VideoRenderer.SCOPE_ROWS * 3)
        var last = 0L
        while (true) {
            delay(250)
            images = withContext(Dispatchers.Default) {
                val sequence = renderer.scopeSamples(grid)
                if (sequence == 0L || sequence == last) return@withContext images
                last = sequence
                val data = ScopeData.analyze(grid, VideoRenderer.SCOPE_COLUMNS, VideoRenderer.SCOPE_ROWS)
                Triple(
                    bitmap(data.histogramPixels(), ScopeData.LEVELS, 100),
                    bitmap(data.waveformPixels(), ScopeData.WAVEFORM_COLUMNS, ScopeData.LEVELS),
                    bitmap(data.vectorscopePixels(), ScopeData.VECTOR_SIZE, ScopeData.VECTOR_SIZE),
                )
            }
        }
    }
    Row(
        modifier.background(Color.Black.copy(alpha = 0.75f), RoundedCornerShape(12.dp)).padding(10.dp).testTag("scopes"),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Scope("Histogram") { images?.let { Image(it.first, null, Modifier.size(width = 150.dp, height = 110.dp), filterQuality = FilterQuality.None) } }
        Scope("Waveform") { images?.let { Image(it.second, null, Modifier.size(150.dp, 110.dp), filterQuality = FilterQuality.Low) } }
        Scope("Vectorscope") {
            Box(Modifier.size(110.dp)) {
                images?.let { Image(it.third, null, Modifier.size(110.dp), filterQuality = FilterQuality.Low) }
                // 75 % colour-bar targets, as on Apple.
                Canvas(Modifier.size(110.dp)) {
                    ScopeData.barTargets.forEach { (_, p) ->
                        val box = 8.dp.toPx()
                        drawRect(Color.White.copy(alpha = 0.6f), Offset(p.first.toFloat() * size.width - box / 2, p.second.toFloat() * size.height - box / 2), Size(box, box), style = Stroke(1f))
                    }
                }
            }
        }
    }
}

@Composable
private fun Scope(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(title, color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.labelSmall)
        content()
    }
}

private fun bitmap(pixels: IntArray, width: Int, height: Int): ImageBitmap =
    Bitmap.createBitmap(pixels, width, height, Bitmap.Config.ARGB_8888).asImageBitmap()
