package com.example.hdmicaptain.viewer

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.filled.Lock
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.FlowRowScope
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material3.AssistChip
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.ModalBottomSheet
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
        contentColor = Color.White,
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
            IconButton(onClick = actions::record) {
                ProIcon(
                    Icons.Outlined.FiberManualRecord,
                    if (isRecording) "Stop Recording" else "Record",
                    locked = !isPro,
                    tint = if (isRecording) Color(0xFFFF453A) else Color.White,
                )
            }
            MoreMenu(options, showDiagnostics, showScopes, testPattern, guides, actions)
        }
    }
    }
}

/**
 * A Pro control keeps its own icon; without Pro it gets a small lock badge (Apple `proLock`),
 * so Record and Save Recent stay recognisable and the lock says "Pro".
 */
@Composable
private fun ProIcon(icon: ImageVector, label: String, locked: Boolean, tint: Color = Color.White) {
    Box {
        Icon(icon, contentDescription = if (locked) "$label (Pro)" else label, tint = tint)
        if (locked) {
            Icon(
                Icons.Filled.Lock,
                contentDescription = null,
                tint = Brand.gold,
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .offset(x = 4.dp, y = 4.dp)
                    .size(13.dp)
                    .background(Color(0xFF161B2B), CircleShape)
                    .padding(1.5.dp),
            )
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
        ProIcon(Icons.Outlined.History, "Save Recent", locked = !isPro)
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
    if (open) ViewOptionsSheet(options, showDiagnostics, showScopes, testPattern, guides, actions) { open = false }
}

/**
 * View options as a compact bottom sheet: grouped chips instead of a ~26-row menu, so it fits a
 * phone screen (about half its height) and still scrolls on very small windows.
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
private fun ViewOptionsSheet(
    options: RenderOptions,
    showDiagnostics: Boolean,
    showScopes: Boolean,
    testPattern: TestPattern?,
    guides: GuideSettings,
    actions: ControlBarActions,
    onDismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(
            Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 20.dp).padding(bottom = 24.dp).testTag("view-options"),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            ChipGroup("Scale") {
                ScaleMode.entries.forEach { mode -> Chip(mode.shortTitle, options.scale == mode) { actions.setScale(mode) } }
            }
            ChipGroup("View") {
                Chip("Sharp Pixels", options.filter == FilterMode.Nearest) { actions.setSharpPixels(options.filter != FilterMode.Nearest) }
                Chip("Diagnostics", showDiagnostics) { actions.setShowDiagnostics(!showDiagnostics) }
                Chip("Scopes", showScopes) { actions.setShowScopes(!showScopes) }
            }
            ChipGroup("Guides") {
                Chip("Thirds", guides.thirds) { actions.setGuides(guides.copy(thirds = !guides.thirds)) }
                Chip("Center", guides.center) { actions.setGuides(guides.copy(center = !guides.center)) }
                Chip("Safe Areas", guides.safeAreas) { actions.setGuides(guides.copy(safeAreas = !guides.safeAreas)) }
                GuideSettings.Mask.entries.forEach { mask ->
                    Chip(mask.title, guides.mask == mask) { actions.setGuides(guides.copy(mask = if (guides.mask == mask) null else mask)) }
                }
            }
            ChipGroup("Test Pattern") {
                Chip("Off", testPattern == null) { actions.setTestPattern(null) }
                TestPattern.entries.forEach { pattern ->
                    Chip(pattern.shortTitle, testPattern == pattern) { actions.setTestPattern(if (testPattern == pattern) null else pattern) }
                }
            }
            HorizontalDivider()
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                SheetAction(Icons.Outlined.ContentCopy, "Copy Frame") { onDismiss(); actions.copyFrame() }
                SheetAction(Icons.Outlined.PictureInPictureAlt, "Picture in Picture") { onDismiss(); actions.pictureInPicture() }
                SheetAction(Icons.Outlined.Settings, "Settings…") { onDismiss(); actions.openSettings() }
                SheetAction(Icons.AutoMirrored.Outlined.HelpOutline, "Connection Help…") { onDismiss(); actions.showHelp() }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ChipGroup(title: String, chips: @Composable FlowRowScope.() -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(title, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), content = chips)
    }
}

@Composable
private fun Chip(label: String, selected: Boolean, onClick: () -> Unit) {
    FilterChip(selected = selected, onClick = onClick, label = { Text(label) })
}

@Composable
private fun SheetAction(icon: ImageVector, label: String, onClick: () -> Unit) {
    AssistChip(onClick = onClick, label = { Text(label) }, leadingIcon = { Icon(icon, null, Modifier.size(18.dp)) })
}

/** Short names for chips (the full titles are in Settings). */
private val ScaleMode.shortTitle: String
    get() = when (this) {
        ScaleMode.Fit -> "Fit"
        ScaleMode.Fill -> "Fill"
        ScaleMode.Stretch -> "Stretch"
        ScaleMode.Integer -> "Integer"
    }

private val TestPattern.shortTitle: String
    get() = when (this) {
        TestPattern.SmpteBars -> "SMPTE Bars"
        TestPattern.FullBars -> "Color Bars"
        TestPattern.Grid -> "Grid"
        TestPattern.GrayRamp -> "Gray Ramp"
    }

/** Same names as the Apple scale menu. */
val ScaleMode.title: String
    get() = when (this) {
        ScaleMode.Fit -> "Fit"
        ScaleMode.Fill -> "Fill (crop)"
        ScaleMode.Stretch -> "Stretch"
        ScaleMode.Integer -> "Integer (pixel-perfect)"
    }
