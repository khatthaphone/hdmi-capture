package com.example.hdmicaptain.viewer

import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Cable
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material.icons.outlined.PauseCircle
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material.icons.outlined.SettingsInputHdmi
import androidx.compose.material.icons.outlined.Usb
import androidx.compose.material.icons.outlined.VideogameAsset
import androidx.compose.material.icons.outlined.VideocamOff
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.model.PauseReason

// Full-screen state screens (everything that isn't the live picture), ported from the Apple
// StatusScreens: one layout — visual, title, one-line explanation, actions — for every state.

/** What the status screens can ask the app to do. */
interface StatusActions {
    fun continueFromWelcome()
    fun openAppSettings()
    fun retry()
    fun showHelp()
}

/** Screens that come before capture: camera/microphone permission. */
enum class PermissionPhase { Welcome, Denied }

@Composable
fun StateCard(
    title: String,
    message: String,
    modifier: Modifier = Modifier,
    detail: String? = null,
    visual: @Composable () -> Unit,
    actions: @Composable RowScope.() -> Unit = {},
) {
    Column(
        modifier = modifier
            .widthIn(max = 460.dp)
            .verticalScroll(rememberScrollState())
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(Modifier.heightIn(min = 56.dp).padding(bottom = 16.dp), contentAlignment = Alignment.Center) { visual() }
        Text(
            title,
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.SemiBold,
            color = Color.White,
            textAlign = TextAlign.Center,
            modifier = Modifier.semantics { heading() },
        )
        Text(
            message,
            style = MaterialTheme.typography.bodyLarge,
            color = Color.White.copy(alpha = 0.75f),
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 8.dp),
        )
        if (detail != null) {
            Text(
                detail,
                style = MaterialTheme.typography.bodySmall,
                color = Color.White.copy(alpha = 0.5f),
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 6.dp),
            )
        }
        Row(Modifier.padding(top = 20.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), content = actions)
    }
}

@Composable
private fun Symbol(icon: ImageVector) = Icon(icon, contentDescription = null, tint = Color.White, modifier = Modifier.size(44.dp))

/** Camera/microphone permission screens (before the capture controller runs). */
@Composable
fun PermissionScreen(phase: PermissionPhase, actions: StatusActions, modifier: Modifier = Modifier) {
    Overlay(modifier.testTag("status-${phase.name}")) {
        when (phase) {
            PermissionPhase.Welcome -> StateCard(
                title = "Welcome to HDMI Captain",
                message = "Android sees an HDMI capture card as a camera and a microphone, so it will ask for access next. " +
                    "HDMI Captain uses it only to show the picture and play the sound, and nothing leaves your device.",
                visual = { ConnectIllustration() },
            ) { Button(onClick = actions::continueFromWelcome) { Text("Continue") } }
            PermissionPhase.Denied -> StateCard(
                title = "Camera access required",
                message = "Allow Camera and Microphone for HDMI Captain in Settings › Apps, then come back.",
                visual = { Symbol(Icons.Outlined.VideocamOff) },
            ) {
                Button(onClick = actions::openAppSettings) { Text("Open Settings") }
                OutlinedButton(onClick = actions::continueFromWelcome) { Text("Check Again") }
            }
        }
    }
}

/** Covers the video whenever there is no live picture worth showing. */
@Composable
fun StatusOverlay(phase: ViewerPhase, actions: StatusActions, modifier: Modifier = Modifier) {
    if (phase is ViewerPhase.Live) return
    Overlay(modifier.testTag("status-${phase::class.simpleName}")) {
        when (phase) {
            ViewerPhase.Scanning -> StateCard(
                title = "Connect a capture card",
                message = "Plug an HDMI capture card into this device. The picture appears automatically.",
                visual = { ConnectIllustration() },
            ) { OutlinedButton(onClick = actions::showHelp) { Text("Connection Help…") } }
            is ViewerPhase.UsbPermission -> StateCard(
                title = if (phase.denied) "USB access needed" else "Allow USB access",
                message = if (phase.denied) {
                    "HDMI Captain needs access to ${phase.cardName} to show its picture. Tap Try Again and choose Allow."
                } else {
                    "Choose Allow to let HDMI Captain use ${phase.cardName}. Tick \"Always open\" to skip this next time."
                },
                visual = { Symbol(Icons.Outlined.Usb) },
            ) { if (phase.denied) Button(onClick = actions::retry) { Text("Try Again") } }
            is ViewerPhase.Waiting -> StateCard(
                title = "${phase.cardName} is disconnected",
                message = "Plug it back in to continue where you left off.",
                visual = { Symbol(Icons.Outlined.Cable) },
            )
            is ViewerPhase.Connecting -> StateCard(
                title = "Connecting to ${phase.cardName}…",
                message = if (phase.slow) "This is taking longer than usual." else "Starting the capture card.",
                visual = { CircularProgressIndicator(color = Color.White) },
            ) { if (phase.slow) OutlinedButton(onClick = actions::retry) { Text("Reconnect") } }
            is ViewerPhase.Recovering -> RecoveringCard(phase.reason, actions)
            is ViewerPhase.Paused -> StateCard(
                title = "Paused",
                message = pauseMessage(phase.reason),
                detail = pauseHint(phase.reason),
                visual = { Symbol(Icons.Outlined.PauseCircle) },
            )
            is ViewerPhase.Failed -> StateCard(
                title = "The capture card stopped responding",
                message = "Try reconnecting. If that doesn't help, unplug the card and plug it back in.",
                detail = phase.message,
                visual = { Symbol(Icons.Outlined.ErrorOutline) },
            ) { Button(onClick = actions::retry) { Text("Reconnect") } }
            is ViewerPhase.Live -> Unit
        }
    }
}

@Composable
private fun RecoveringCard(reason: String, actions: StatusActions) = when (reason) {
    "No signal" -> StateCard(
        title = "No HDMI signal",
        message = "Check that the source is on and the HDMI cable is plugged in.",
        visual = { Symbol(Icons.Outlined.SettingsInputHdmi) },
    ) { OutlinedButton(onClick = actions::showHelp) { Text("Connection Help…") } }
    "No frames" -> StateCard(
        title = "No picture from the capture card",
        message = "If Camera access is off in Quick Settings, turn it on. Some cards also need more power: try a powered USB-C hub.",
        visual = { Symbol(Icons.Outlined.VideocamOff) },
    ) { OutlinedButton(onClick = actions::showHelp) { Text("Connection Help…") } }
    else -> StateCard(
        title = "Reconnecting…",
        message = "The capture card stopped sending video. Reconnecting automatically.",
        detail = reason,
        visual = { CircularProgressIndicator(color = Color.White) },
    )
}

/** The card is live but shows no picture: its own "no signal" screen, or black. */
@Composable
fun NoSignalOverlay(black: Boolean, actions: StatusActions, modifier: Modifier = Modifier) {
    Overlay(modifier.testTag("status-NoSignal"), alpha = 0.55f) {
        StateCard(
            title = "No HDMI signal",
            message = if (black) {
                "The card receives a black picture. If the source is a PlayStation or Apple TV, turn off HDCP in its settings."
            } else {
                "Check that the source is on and the HDMI cable is plugged in."
            },
            visual = { Symbol(Icons.Outlined.SettingsInputHdmi) },
        ) { OutlinedButton(onClick = actions::showHelp) { Text("Connection Help…") } }
    }
}

@Composable
private fun Overlay(modifier: Modifier = Modifier, alpha: Float = 0.7f, content: @Composable () -> Unit) {
    Box(modifier.fillMaxSize().background(Color.Black.copy(alpha = alpha)), contentAlignment = Alignment.Center) { content() }
}

private fun pauseMessage(reason: PauseReason) = when (reason) {
    PauseReason.Background -> "HDMI Captain is in the background"
    PauseReason.InUseByAnotherApp -> "Another app is using the capture card"
    PauseReason.PrivacyToggle -> "Camera access is turned off in Quick Settings"
    PauseReason.TooHot -> "This device is too hot"
    PauseReason.Other -> "Capture was interrupted"
}

private fun pauseHint(reason: PauseReason) = when (reason) {
    PauseReason.Background -> "Come back to HDMI Captain and the picture returns right away."
    PauseReason.InUseByAnotherApp -> "Close the other app to continue."
    PauseReason.PrivacyToggle -> "Swipe down and turn on Camera access."
    PauseReason.TooHot -> "Capture resumes when it cools down."
    PauseReason.Other -> "It resumes by itself."
}

/** Source → capture card → this device, with a gently pulsing signal. */
@Composable
fun ConnectIllustration() {
    val pulse by rememberInfiniteTransition(label = "signal").animateFloat(
        initialValue = 0.35f, targetValue = 1f,
        animationSpec = infiniteRepeatable(tween(900), RepeatMode.Reverse), label = "pulse",
    )
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Icon(Icons.Outlined.VideogameAsset, null, tint = Color.White, modifier = Modifier.size(40.dp))
        Link(pulse)
        Icon(Icons.Outlined.SettingsInputHdmi, null, tint = Color.White, modifier = Modifier.size(40.dp))
        Link(pulse)
        Icon(Icons.Outlined.PhoneAndroid, null, tint = Color.White, modifier = Modifier.size(40.dp))
    }
}

@Composable
private fun Link(alpha: Float) {
    Box(Modifier.size(width = 26.dp, height = 3.dp).alpha(alpha).background(Color.White.copy(alpha = 0.6f), RoundedCornerShape(2.dp)))
}
