package com.example.hdmicaptain.viewer

import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.HelpOutline
import androidx.compose.material.icons.automirrored.outlined.VolumeOff
import androidx.compose.material.icons.automirrored.outlined.VolumeUp
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material.icons.outlined.FiberManualRecord
import androidx.compose.material.icons.outlined.History
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.MoreHoriz
import androidx.compose.material.icons.outlined.PictureInPictureAlt
import androidx.compose.material.icons.outlined.PhotoCamera
import androidx.compose.material.icons.outlined.QueryStats
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.SettingsInputHdmi
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.IconButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.capture.Card
import com.example.hdmicaptain.model.AudioSettings
import com.example.hdmicaptain.model.GuideSettings
import com.example.hdmicaptain.model.TestPattern
import com.example.hdmicaptain.model.FilterMode
import com.example.hdmicaptain.model.RenderOptions
import com.example.hdmicaptain.model.ScaleMode

/** Everything the bar can ask for; the app decides (Pro gating, PiP, settings). */
interface ControlBarActions {
    fun selectInput(key: String)
    fun setMuted(muted: Boolean)
    fun setVolume(volume: Float)
    fun screenshot()
    fun record()
    /** Save Recent: [seconds] of the buffer, null = everything (without Pro this opens the upgrade sheet). */
    fun saveRecent(seconds: Int?)
    fun setScale(mode: ScaleMode)
    fun setSharpPixels(on: Boolean)
    fun setShowDiagnostics(on: Boolean)
    fun setTestPattern(pattern: TestPattern?)
    fun setGuides(guides: GuideSettings)
    fun setShowScopes(on: Boolean)
    fun copyFrame()
    fun pictureInPicture()
    fun openSettings()
    fun showHelp()
    fun poke()
}

/**
 * The floating control bar (Apple ControlBar): input, sound, screenshot, Save Recent, Record and
 * view options. It sits above the SurfaceView, so it can never reach screenshots or recordings.
 */
@Composable
fun ControlBar(
    cards: List<Card>,
    currentCard: String?,
    options: RenderOptions,
    audio: AudioSettings,
    showDiagnostics: Boolean,
    testPattern: TestPattern?,
    guides: GuideSettings,
    isPro: Boolean,
    showScopes: Boolean = false,
    isRecording: Boolean,
    actions: ControlBarActions,
    modifier: Modifier = Modifier,
) {
    // Narrow phones: the volume slider gives way (hardware volume keys remain); the bar fits 360 dp.
    BoxWithConstraints(modifier) {
    val roomForSlider = maxWidth >= 460.dp
    Surface(
        modifier = Modifier.padding(16.dp).testTag("control-bar"),
        shape = RoundedCornerShape(22.dp),
        color = Color(0xE6161B2B),
        tonalElevation = 6.dp,
    ) {
        Row(Modifier.padding(horizontal = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            InputMenu(cards, currentCard, actions)
            IconButton(onClick = { actions.setMuted(!audio.muted) }) {
                Icon(
                    if (audio.muted) Icons.AutoMirrored.Outlined.VolumeOff else Icons.AutoMirrored.Outlined.VolumeUp,
                    contentDescription = if (audio.muted) "Unmute" else "Mute",
                )
            }
            if (roomForSlider) {
                Slider(
                    value = if (audio.muted) 0f else audio.volume,
                    onValueChange = actions::setVolume,
                    modifier = Modifier.width(110.dp).testTag("volume"),
                )
            }
            BarButton(Icons.Outlined.PhotoCamera, "Screenshot", actions::screenshot)
            SaveRecentMenu(isPro, actions)
            BarButton(
                if (isPro) Icons.Outlined.FiberManualRecord else Icons.Outlined.Lock,
                if (isRecording) "Stop Recording" else "Record",
                actions::record,
                tint = if (isRecording) Color(0xFFFF453A) else Color.White,
            )
            MoreMenu(options, showDiagnostics, showScopes, testPattern, guides, actions)
        }
    }
    }
}

@Composable
private fun BarButton(icon: ImageVector, label: String, onClick: () -> Unit, tint: Color = Color.White) {
    IconButton(onClick = onClick) { Icon(icon, contentDescription = label, tint = tint) }
}

/** Spans as on Apple (MenuItems.swift). */
private val recentSpans = listOf(
    "Last 15 Seconds" to 15, "Last 30 Seconds" to 30, "Last 1 Minute" to 60, "Last 5 Minutes" to 300,
    "Last 15 Minutes" to 900, "Last 30 Minutes" to 1800, "Last 60 Minutes" to 3600,
)

@Composable
private fun SaveRecentMenu(isPro: Boolean, actions: ControlBarActions) {
    var open by remember { mutableStateOf(false) }
    IconButton(onClick = { if (isPro) open = true else actions.saveRecent(null); actions.poke() }) {
        Icon(if (isPro) Icons.Outlined.History else Icons.Outlined.Lock, contentDescription = "Save Recent")
    }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        recentSpans.forEach { (title, seconds) ->
            DropdownMenuItem(text = { Text(title) }, onClick = { open = false; actions.saveRecent(seconds) })
        }
        DropdownMenuItem(text = { Text("Everything") }, onClick = { open = false; actions.saveRecent(null) })
    }
}

@Composable
private fun InputMenu(cards: List<Card>, currentCard: String?, actions: ControlBarActions) {
    var open by remember { mutableStateOf(false) }
    IconButton(onClick = { open = true; actions.poke() }) { Icon(Icons.Outlined.SettingsInputHdmi, contentDescription = "Video Input") }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        if (cards.isEmpty()) DropdownMenuItem(text = { Text("No capture card connected") }, onClick = {}, enabled = false)
        cards.forEach { card ->
            DropdownMenuItem(
                text = { Text(card.name) },
                leadingIcon = { if (card.key == currentCard) Icon(Icons.Outlined.Check, null) },
                onClick = { open = false; actions.selectInput(card.key) },
            )
        }
    }
}

@Composable
private fun MoreMenu(options: RenderOptions, showDiagnostics: Boolean, showScopes: Boolean, testPattern: TestPattern?, guides: GuideSettings, actions: ControlBarActions) {
    var open by remember { mutableStateOf(false) }
    IconButton(onClick = { open = true; actions.poke() }) { Icon(Icons.Outlined.MoreHoriz, contentDescription = "More") }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        ScaleMode.entries.forEach { mode ->
            DropdownMenuItem(
                text = { Text(mode.title) },
                leadingIcon = { if (options.scale == mode) Icon(Icons.Outlined.Check, null) },
                onClick = { actions.setScale(mode) },
            )
        }
        HorizontalDivider()
        DropdownMenuItem(
            text = { Text("Sharp Pixels") },
            leadingIcon = { if (options.filter == FilterMode.Nearest) Icon(Icons.Outlined.Check, null) },
            onClick = { actions.setSharpPixels(options.filter != FilterMode.Nearest) },
        )
        DropdownMenuItem(
            text = { Text("Show Diagnostics") },
            leadingIcon = { Icon(if (showDiagnostics) Icons.Outlined.Check else Icons.Outlined.QueryStats, null) },
            onClick = { actions.setShowDiagnostics(!showDiagnostics) },
        )
        HorizontalDivider()
        Text("Guides", style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(start = 16.dp, top = 8.dp))
        Check("Rule of Thirds", guides.thirds) { actions.setGuides(guides.copy(thirds = it)) }
        Check("Center", guides.center) { actions.setGuides(guides.copy(center = it)) }
        Check("Safe Areas", guides.safeAreas) { actions.setGuides(guides.copy(safeAreas = it)) }
        GuideSettings.Mask.entries.forEach { mask ->
            Check("Mask ${mask.title}", guides.mask == mask) { actions.setGuides(guides.copy(mask = if (it) mask else null)) }
        }
        HorizontalDivider()
        Text("Test Pattern", style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(start = 16.dp, top = 8.dp))
        Check("Off (live picture)", testPattern == null) { actions.setTestPattern(null) }
        TestPattern.entries.forEach { pattern ->
            Check(pattern.title, testPattern == pattern) { actions.setTestPattern(if (it) pattern else null) }
        }
        HorizontalDivider()
        Check("Scopes", showScopes) { actions.setShowScopes(it) }
        DropdownMenuItem(text = { Text("Copy Frame") }, onClick = { open = false; actions.copyFrame() })
        DropdownMenuItem(
            text = { Text("Picture in Picture") },
            leadingIcon = { Icon(Icons.Outlined.PictureInPictureAlt, null) },
            onClick = { open = false; actions.pictureInPicture() },
        )
        HorizontalDivider()
        DropdownMenuItem(
            text = { Text("Settings…") },
            leadingIcon = { Icon(Icons.Outlined.Settings, null) },
            onClick = { open = false; actions.openSettings() },
        )
        DropdownMenuItem(
            text = { Text("Connection Help…") },
            leadingIcon = { Icon(Icons.AutoMirrored.Outlined.HelpOutline, null) },
            onClick = { open = false; actions.showHelp() },
        )
    }
}

@Composable
private fun Check(title: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    DropdownMenuItem(
        text = { Text(title) },
        leadingIcon = { if (checked) Icon(Icons.Outlined.Check, null) },
        onClick = { onChange(!checked) },
    )
}

/** Same names as the Apple scale menu. */
val ScaleMode.title: String
    get() = when (this) {
        ScaleMode.Fit -> "Fit"
        ScaleMode.Fill -> "Fill (crop)"
        ScaleMode.Stretch -> "Stretch"
        ScaleMode.Integer -> "Integer (pixel-perfect)"
    }
