package com.example.hdmicaptain.viewer

import androidx.compose.foundation.AndroidExternalSurface
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import com.example.hdmicaptain.render.VideoRenderer

/**
 * The video: a SurfaceView (not TextureView) so frames go straight to the compositor, one vsync
 * sooner. Compose UI drawn on top can never reach the video buffer, screenshots or recordings.
 */
@Composable
fun VideoSurface(renderer: VideoRenderer, modifier: Modifier = Modifier) {
    AndroidExternalSurface(modifier = modifier, isOpaque = true) {
        onSurface { surface, _, _ ->
            renderer.setSurface(surface)
            surface.onDestroyed { renderer.setSurface(null) }
        }
    }
}
