package com.example.hdmicaptain.viewer

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import com.example.hdmicaptain.model.GuideSettings
import com.example.hdmicaptain.model.Rect
import com.example.hdmicaptain.model.RenderGeometry
import com.example.hdmicaptain.model.ScaleMode

/**
 * Framing guides on top of the picture (CaptureCore's GuideSettings): thirds, centre cross,
 * 90 %/80 % safe areas and an aspect mask. Drawn as UI above the SurfaceView, so they never reach
 * screenshots or recordings.
 */
@Composable
fun GuidesOverlay(guides: GuideSettings, sourceWidth: Int, sourceHeight: Int, scale: ScaleMode, modifier: Modifier = Modifier) {
    if (guides.isEmpty || sourceWidth == 0) return
    Canvas(modifier.fillMaxSize()) {
        val v = RenderGeometry.compute(sourceWidth.toDouble(), sourceHeight.toDouble(), size.width.toDouble(), size.height.toDouble(), scale).viewport
        val line = Color.White.copy(alpha = guides.opacity.toFloat())
        val stroke = 1.dp.toPx()
        fun rect(r: Rect) = drawRect(line, Offset(r.x.toFloat(), r.y.toFloat()), Size(r.width.toFloat(), r.height.toFloat()), style = Stroke(stroke))
        guides.mask?.let { mask ->
            val m = GuideSettings.maskRect(mask, v)
            val shade = Color.Black.copy(alpha = 0.6f * guides.opacity.toFloat() / 0.6f)
            // Shade outside the mask, inside the picture.
            drawRect(shade, Offset(v.x.toFloat(), v.y.toFloat()), Size(v.width.toFloat(), (m.y - v.y).toFloat()))
            drawRect(shade, Offset(v.x.toFloat(), (m.y + m.height).toFloat()), Size(v.width.toFloat(), (v.y + v.height - m.y - m.height).toFloat()))
            drawRect(shade, Offset(v.x.toFloat(), m.y.toFloat()), Size((m.x - v.x).toFloat(), m.height.toFloat()))
            drawRect(shade, Offset((m.x + m.width).toFloat(), m.y.toFloat()), Size((v.x + v.width - m.x - m.width).toFloat(), m.height.toFloat()))
        }
        if (guides.thirds) {
            for (i in 1..2) {
                val x = (v.x + v.width * i / 3).toFloat()
                val y = (v.y + v.height * i / 3).toFloat()
                drawLine(line, Offset(x, v.y.toFloat()), Offset(x, (v.y + v.height).toFloat()), stroke)
                drawLine(line, Offset(v.x.toFloat(), y), Offset((v.x + v.width).toFloat(), y), stroke)
            }
        }
        if (guides.center) {
            val cx = (v.x + v.width / 2).toFloat()
            val cy = (v.y + v.height / 2).toFloat()
            val arm = (minOf(v.width, v.height) * 0.04).toFloat()
            drawLine(line, Offset(cx - arm, cy), Offset(cx + arm, cy), stroke)
            drawLine(line, Offset(cx, cy - arm), Offset(cx, cy + arm), stroke)
        }
        if (guides.safeAreas) {
            for (inset in listOf(0.05, 0.10)) {  // action safe 90 %, title safe 80 %
                rect(Rect(v.x + v.width * inset, v.y + v.height * inset, v.width * (1 - 2 * inset), v.height * (1 - 2 * inset)))
            }
        }
    }
}

private val Int.dp get() = androidx.compose.ui.unit.Dp(this.toFloat())
