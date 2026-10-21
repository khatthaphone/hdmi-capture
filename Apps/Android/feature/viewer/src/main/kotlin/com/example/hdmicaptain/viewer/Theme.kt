package com.example.hdmicaptain.viewer

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/** The app icon's colours: navy field, gold helm. The viewer is always dark around the video. */
object Brand {
    val navy = Color(0xFF0A1745)
    val navyLight = Color(0xFF173179)
    val gold = Color(0xFFF5C542)
    val goldDeep = Color(0xFFB8860B)
}

@Composable
fun HdmiCaptainTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = darkColorScheme(
            primary = Brand.gold,
            onPrimary = Color.Black,
            secondary = Brand.navyLight,
            background = Color.Black,
            surface = Color(0xFF111522),
            surfaceContainer = Color(0xFF161B2B),
        ),
        content = content,
    )
}
