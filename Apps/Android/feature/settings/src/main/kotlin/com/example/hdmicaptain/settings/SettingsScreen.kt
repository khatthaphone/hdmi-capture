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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.OutlinedTextField
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
import com.example.hdmicaptain.model.ImageAdjustments
import com.example.hdmicaptain.model.LookPreset
import com.example.hdmicaptain.model.Profile
import com.example.hdmicaptain.model.ProfileSettings
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
    val isPro: Boolean = false,
    val recentEnabled: Boolean = false,
    val recentMinutes: Int = 15,
    val profiles: List<Profile> = Profile.builtIns,
)

interface SettingsActions {
    fun selectCard(key: String)
    fun setFormat(format: FormatPreference)
    fun update(preset: DevicePreset)
    fun setShowDiagnostics(on: Boolean)
    fun showLicenses()
    /** Opens the system file picker for a .cube file. */
    fun chooseLut()
    fun setRecent(enabled: Boolean, minutes: Int)
    fun applyProfile(profile: Profile)
    fun saveProfile(name: String)
    fun deleteProfile(profile: Profile)
    fun showUpgrade()
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
    item { ProfileRow(state, actions) }

    section("Picture")
    item { Choice("Scale", scaleTitle(p.scale), ScaleMode.entries.map(::scaleTitle)) { actions.update(p.copy(scale = ScaleMode.entries[it])) } }
    item { SwitchRow("Pixel-sharp scaling", p.filter == FilterMode.Nearest) { actions.update(p.copy(filter = if (it) FilterMode.Nearest else FilterMode.Linear)) } }
    item { Choice("Color range", rangeTitle(p.colorRange), ColorRange.entries.map(::rangeTitle)) { actions.update(p.copy(colorRange = ColorRange.entries[it])) } }

    section("Look")
    val a = p.adjustments
    item {
        val looks = LookPreset.entries
        Choice("Preset", LookPreset.of(a)?.title ?: "Custom", looks.map { it.title }) { actions.update(p.copy(adjustments = looks[it].values)) }
    }
    item { SliderRow("Brightness", a.brightness.toFloat(), -0.5f..0.5f, signed(a.brightness)) { actions.update(p.copy(adjustments = a.copy(brightness = it.toDouble()))) } }
    item { SliderRow("Contrast", a.contrast.toFloat(), 0.5f..1.5f, "%.2f".format(a.contrast)) { actions.update(p.copy(adjustments = a.copy(contrast = it.toDouble()))) } }
    item { SliderRow("Saturation", a.saturation.toFloat(), 0f..2f, "%.2f".format(a.saturation)) { actions.update(p.copy(adjustments = a.copy(saturation = it.toDouble()))) } }
    item { SliderRow("Gamma", a.gamma.toFloat(), 0.5f..2f, "%.2f".format(a.gamma)) { actions.update(p.copy(adjustments = a.copy(gamma = it.toDouble()))) } }
    item { SliderRow("Sharpness", a.sharpness.toFloat(), 0f..1f, "%.2f".format(a.sharpness)) { actions.update(p.copy(adjustments = a.copy(sharpness = it.toDouble()))) } }
    if (!a.isIdentity) item { TextButton(onClick = { actions.update(p.copy(adjustments = ImageAdjustments())) }) { Text("Reset Look") } }

    section("LUT")
    item {
        Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(p.lutName ?: "None", Modifier.weight(1f))
            TextButton(onClick = actions::chooseLut) { Text("Choose .cube File…") }
            if (p.lutUri != null) TextButton(onClick = { actions.update(p.copy(lutUri = null, lutName = null)) }) { Text("Remove") }
        }
    }
    if (p.lutUri != null) {
        item { SliderRow("Intensity", p.lutIntensity.toFloat(), 0f..1f, "${(p.lutIntensity * 100).roundToInt()} %") { actions.update(p.copy(lutIntensity = it.toDouble())) } }
    }
    footer("A .cube colour lookup table from Resolve, Premiere or a camera maker, applied on screen.")

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

    section("Pro")
    if (state.isPro) {
        item { SwitchRow("Keep Recent Footage", state.recentEnabled) { actions.setRecent(it, state.recentMinutes) } }
        item {
            val choices = listOf(5, 15, 30, 60)
            Choice("Keep", "${state.recentMinutes} min", choices.map { "$it min" }) { actions.setRecent(state.recentEnabled, choices[it]) }
        }
        footer("Keeps the last minutes on this device so you can save a moment after it happened (about 1 GB per 15 minutes at 1080p60).")
    } else {
        item {
            Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text("Recording and Save Recent", Modifier.weight(1f))
                TextButton(onClick = actions::showUpgrade) { Text("Upgrade…") }
            }
        }
    }

    section("About")
    item { Row(Modifier.fillMaxWidth().padding(vertical = 12.dp)) { Text("Version", Modifier.weight(1f)); Text(state.appVersion) } }
    item { Text("Open-source licenses", Modifier.fillMaxWidth().clickable(onClick = actions::showLicenses).padding(vertical = 12.dp)) }
    item { Box(Modifier.padding(bottom = 32.dp)) }
}

/** Profiles: apply one, or save the current settings as a new one (Apple ProfilesSettingsView). */
@Composable
private fun ProfileRow(state: SettingsState, actions: SettingsActions) {
    var naming by remember { mutableStateOf(false) }
    var name by remember { mutableStateOf("") }
    val current = state.profiles.firstOrNull { ProfileSettings(state.preset) == it.settings }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.weight(1f)) {
            Choice("Profile", current?.name ?: "Custom", state.profiles.map { it.name }) { actions.applyProfile(state.profiles[it]) }
        }
        TextButton(onClick = { name = ""; naming = true }) { Text("Save as…") }
        if (current != null && !current.isBuiltIn) TextButton(onClick = { actions.deleteProfile(current) }) { Text("Delete") }
    }
    if (naming) {
        AlertDialog(
            onDismissRequest = { naming = false },
            title = { Text("Save as Profile") },
            text = { OutlinedTextField(name, { name = it }, label = { Text("Name") }, singleLine = true) },
            confirmButton = {
                TextButton(onClick = { actions.saveProfile(name.trim()); naming = false }, enabled = name.isNotBlank()) { Text("Save") }
            },
            dismissButton = { TextButton(onClick = { naming = false }) { Text("Cancel") } },
        )
    }
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

private fun signed(v: Double) = if (v > 0) "+%.2f".format(v) else "%.2f".format(v)

fun formatTitle(format: FormatPreference) = when (format) {
    FormatPreference.Auto -> "Automatic"
    is FormatPreference.Pinned -> "${format.width}×${format.height}" + (format.frameRate?.let { " @ ${formatRate(it)}" } ?: "")
}

private fun formatRate(rate: Double) = if (rate % 1.0 == 0.0) "${rate.toInt()} fps" else "%.2f fps".format(rate)
