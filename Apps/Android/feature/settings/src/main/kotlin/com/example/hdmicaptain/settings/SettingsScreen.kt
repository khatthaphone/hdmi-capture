package com.example.hdmicaptain.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.example.hdmicaptain.model.ChannelMode
import com.example.hdmicaptain.model.ColorRange
import com.example.hdmicaptain.model.DevicePreset
import com.example.hdmicaptain.model.FilterMode
import com.example.hdmicaptain.model.FormatPreference
import com.example.hdmicaptain.model.ScaleMode
import kotlin.math.roundToInt

/** Everything Settings shows; the viewer builds it from the controller and the card's preset. */
data class SettingsState(
    val cards: List<Pair<String, String>>,
    val currentCardKey: String?,
    val formats: List<FormatPreference.Pinned>,
    val preset: DevicePreset,
    val showDiagnostics: Boolean,
    val appVersion: String,
)

interface SettingsActions {
    fun selectCard(key: String)
    fun setFormat(format: FormatPreference)
    fun update(preset: DevicePreset)
    fun setShowDiagnostics(on: Boolean)
    fun showLicenses()
}

/**
 * Settings (the iPad SettingsSheet): input, picture, sound, display. Per-card values are saved
 * in that card's preset. Full screen on phones; a centred column on tablets and desktop windows.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(state: SettingsState, actions: SettingsActions, onDone: () -> Unit) {
    Dialog(onDismissRequest = onDone, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Scaffold(
            modifier = Modifier.fillMaxSize().testTag("settings"),
            topBar = {
                TopAppBar(title = { Text("Settings") }, actions = { TextButton(onClick = onDone) { Text("Done") } })
            },
        ) { padding ->
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.TopCenter) {
                LazyColumn(Modifier.widthIn(max = 640.dp).fillMaxWidth().padding(horizontal = 16.dp)) {
                    settingsContent(state, actions)
                }
            }
        }
    }
}

private fun LazyListScope.settingsContent(state: SettingsState, actions: SettingsActions) {
    val p = state.preset
    section("Input")
    item {
        Choice(
            "Capture card",
            state.cards.firstOrNull { it.first == state.currentCardKey }?.second ?: "None connected",
            state.cards.map { it.second },
        ) { actions.selectCard(state.cards[it].first) }
    }
    item {
        val formats = listOf<FormatPreference>(FormatPreference.Auto) + state.formats
        Choice("Format", formatTitle(p.format), formats.map(::formatTitle)) { actions.setFormat(formats[it]) }
    }
    footer("Automatic picks the smoothest 16:9 mode the card and this USB port can carry.")

    section("Picture")
    item { Choice("Scale", scaleTitle(p.scale), ScaleMode.entries.map(::scaleTitle)) { actions.update(p.copy(scale = ScaleMode.entries[it])) } }
    item { SwitchRow("Pixel-sharp scaling", p.filter == FilterMode.Nearest) { actions.update(p.copy(filter = if (it) FilterMode.Nearest else FilterMode.Linear)) } }
    item { Choice("Color range", rangeTitle(p.colorRange), ColorRange.entries.map(::rangeTitle)) { actions.update(p.copy(colorRange = ColorRange.entries[it])) } }

    section("Sound")
    item { SwitchRow("Mute", p.muted) { actions.update(p.copy(muted = it)) } }
    item { SliderRow("Volume", p.volume, 0f..1f, "${(p.volume * 100).roundToInt()} %") { actions.update(p.copy(volume = it)) } }
    item {
        SliderRow("Audio delay", p.audioDelayMs.toFloat(), 0f..500f, "${p.audioDelayMs} ms", steps = 49) {
            actions.update(p.copy(audioDelayMs = it.roundToInt()))
        }
    }
    item { Choice("Channels", channelTitle(p.channelMode), ChannelMode.entries.map(::channelTitle)) { actions.update(p.copy(channelMode = ChannelMode.entries[it])) } }
    footer("Delay the sound to line it up with a slower TV or monitor.")

    section("Display")
    item { SwitchRow("Show diagnostics", state.showDiagnostics, actions::setShowDiagnostics) }

    section("About")
    item { Row(Modifier.fillMaxWidth().padding(vertical = 12.dp)) { Text("Version", Modifier.weight(1f)); Text(state.appVersion) } }
    item { Text("Open-source licenses", Modifier.fillMaxWidth().clickable(onClick = actions::showLicenses).padding(vertical = 12.dp)) }
    item { Box(Modifier.padding(bottom = 32.dp)) }
}

private fun LazyListScope.section(title: String) = item {
    Text(
        title,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = 24.dp, bottom = 4.dp).semantics { heading() },
    )
}

private fun LazyListScope.footer(text: String) = item {
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 4.dp))
}

@Composable
private fun Choice(label: String, value: String, options: List<String>, onSelect: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        Row(
            Modifier.fillMaxWidth().clickable(enabled = options.isNotEmpty()) { open = true }.padding(vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(label, Modifier.weight(1f))
            Text(value, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            options.forEachIndexed { index, option ->
                DropdownMenuItem(
                    text = { Text(option) },
                    leadingIcon = { if (option == value) Icon(Icons.Outlined.Check, null) },
                    onClick = { open = false; onSelect(index) },
                )
            }
        }
    }
}

@Composable
private fun SwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().clickable { onChange(!checked) }.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

@Composable
private fun SliderRow(label: String, value: Float, range: ClosedFloatingPointRange<Float>, valueText: String, steps: Int = 0, onChange: (Float) -> Unit) {
    Column(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(0.dp)) {
        Row { Text(label, Modifier.weight(1f)); Text(valueText, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        Slider(value = value, onValueChange = onChange, valueRange = range, steps = steps)
    }
}

// Same names as the Apple settings (Symbols.swift).
fun scaleTitle(mode: ScaleMode) = when (mode) {
    ScaleMode.Fit -> "Fit"
    ScaleMode.Fill -> "Fill (crop)"
    ScaleMode.Stretch -> "Stretch"
    ScaleMode.Integer -> "Integer (pixel-perfect)"
}

fun rangeTitle(range: ColorRange) = when (range) {
    ColorRange.Auto -> "Automatic"
    ColorRange.Limited -> "Limited (16–235)"
    ColorRange.Full -> "Full (0–255)"
}

fun channelTitle(mode: ChannelMode) = when (mode) {
    ChannelMode.Auto -> "Automatic"
    ChannelMode.Normal -> "Stereo"
    ChannelMode.Swap -> "Swap left and right"
    ChannelMode.LeftToBoth -> "Left to both"
    ChannelMode.RightToBoth -> "Right to both"
    ChannelMode.Mono -> "Mono mix"
}

fun formatTitle(format: FormatPreference) = when (format) {
    FormatPreference.Auto -> "Automatic"
    is FormatPreference.Pinned -> "${format.width}×${format.height}" + (format.frameRate?.let { " @ ${formatRate(it)}" } ?: "")
}

private fun formatRate(rate: Double) = if (rate % 1.0 == 0.0) "${rate.toInt()} fps" else "%.2f fps".format(rate)
