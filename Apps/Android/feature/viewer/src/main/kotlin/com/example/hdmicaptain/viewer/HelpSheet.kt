package com.example.hdmicaptain.viewer

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Bolt
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.SettingsInputHdmi
import androidx.compose.material.icons.outlined.Speed
import androidx.compose.material.icons.outlined.Usb
import androidx.compose.material.icons.outlined.VolumeOff
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp

/** Connection Help (the Apple HelpSheet with Android steps): the problems people actually hit. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HelpSheet(onDismiss: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        LazyColumn(Modifier.padding(horizontal = 24.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
            item { Text("Connection Help", style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { heading() }) }
            item {
                Topic("No HDMI signal", Icons.Outlined.SettingsInputHdmi, listOf(
                    "Turn the source on and make sure it isn't asleep.",
                    "Unplug the HDMI cable at both ends and plug it back in.",
                    "Pick the source's HDMI output (not its built-in screen) and a standard resolution such as 1080p.",
                ))
            }
            item {
                Topic("Black picture from a PlayStation or Apple TV", Icons.Outlined.Lock, listOf(
                    "These sources protect video with HDCP, and capture cards can't show protected video.",
                    "PS5: Settings › System › HDMI › turn off Enable HDCP. PS4: Settings › System › turn off Enable HDCP.",
                    "Apple TV and streaming apps always use HDCP for movies and TV; games and menus usually work.",
                ))
            }
            item {
                Topic("Capture card not found or black", Icons.Outlined.Usb, listOf(
                    "Your phone or tablet needs USB-C with USB host (OTG) support. Most do.",
                    "Swipe down to Quick Settings and make sure Camera access and Microphone access are on.",
                    "Use a UVC capture card (most USB HDMI capture cards are). Try another cable; some only charge.",
                    "When Android asks, allow HDMI Captain to use the card. Tick \"Always open\" to skip the question.",
                ))
            }
            item {
                Topic("Needs more power", Icons.Outlined.Bolt, listOf(
                    "Some cards draw more power than a phone gives. Use a powered USB-C hub between the phone and the card.",
                ))
            }
            item {
                Topic("No sound", Icons.Outlined.VolumeOff, listOf(
                    "Check that the source isn't muted and that sound is on in HDMI Captain (speaker button on the bar).",
                    "Allow Microphone for HDMI Captain; capture cards deliver sound as a microphone.",
                    "Unplug other USB microphones or headsets, then unplug and replug the card.",
                ))
            }
            item {
                Topic("Picture lags or stutters", Icons.Outlined.Speed, listOf(
                    "Phones with a USB 2.0 port can't carry uncompressed 1080p; HDMI Captain then uses the card's compressed mode automatically.",
                    "Plug the card straight into the phone, not through a hub, when you can.",
                    "Turn on Show Diagnostics to see frame rate, dropped frames and latency.",
                ))
            }
            item { Row(Modifier.padding(bottom = 32.dp)) {} }
        }
    }
}

@Composable
private fun Topic(title: String, icon: ImageVector, steps: List<String>) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Icon(icon, contentDescription = null)
            Text(title, style = MaterialTheme.typography.titleMedium)
        }
        steps.forEachIndexed { index, step ->
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("${index + 1}.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(step)
            }
        }
    }
}
