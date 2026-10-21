package com.example.hdmicaptain.viewer

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.model.AppInfo
import com.example.hdmicaptain.render.NativeRender

/**
 * The viewer: video surface + status screens + control bar (tasks 071, 078).
 * For now, the "Connect a capture card" state card, so the shell and native library are proven.
 */
@Composable
fun ViewerScreen(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black)
            .padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Connect a capture card", style = MaterialTheme.typography.titleLarge, color = Color.White)
        Text(
            "Plug an HDMI capture card into this device. The picture appears automatically.",
            style = MaterialTheme.typography.bodyLarge,
            color = Color.White.copy(alpha = 0.75f),
            textAlign = TextAlign.Center,
        )
        Text(
            "${AppInfo.NAME} · ${NativeRender.version}",
            style = MaterialTheme.typography.labelSmall,
            color = Color.White.copy(alpha = 0.4f),
        )
    }
}
