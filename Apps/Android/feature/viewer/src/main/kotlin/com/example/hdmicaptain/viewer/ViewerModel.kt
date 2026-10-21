package com.example.hdmicaptain.viewer

import android.content.Context
import com.example.hdmicaptain.audio.CardAudioPort
import com.example.hdmicaptain.capture.Card
import com.example.hdmicaptain.capture.CaptureController
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.model.AudioSettings
import com.example.hdmicaptain.model.CubeLUT
import com.example.hdmicaptain.model.FormatPreference
import com.example.hdmicaptain.model.GuideSettings
import com.example.hdmicaptain.model.LiveInfo
import com.example.hdmicaptain.record.AudioFanOut
import com.example.hdmicaptain.record.FileRecorder
import com.example.hdmicaptain.record.RollingRecorder
import com.example.hdmicaptain.model.SignalAnalyzer
import com.example.hdmicaptain.model.SignalContent
import com.example.hdmicaptain.model.SignalDebouncer
import com.example.hdmicaptain.model.TestPattern
import com.example.hdmicaptain.model.DevicePreset
import com.example.hdmicaptain.model.FilterMode
import com.example.hdmicaptain.model.RenderOptions
import com.example.hdmicaptain.model.ScaleMode
import com.example.hdmicaptain.prefs.PrefsStore
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** A short message over the video ("Screenshot saved"), like Feedback.Kind on Apple. */
data class Feedback(val text: String, val isError: Boolean = false)

/**
 * The viewer's UI state and actions (the Android ViewerModel): picture options, sound, control
 * bar visibility, feedback. Capture itself lives in [CaptureController].
 */
class ViewerModel(
    private val context: Context,
    private val scope: CoroutineScope,
    val renderer: VideoRenderer,
    val controller: CaptureController,
    private val audio: CardAudioPort,
    /** Cards for the input menu. */
    val cards: StateFlow<List<Card>>,
    private val prefs: PrefsStore,
) {
    val phase: StateFlow<ViewerPhase> get() = controller.phase

    private val _preset = MutableStateFlow(DevicePreset())
    /** The current card's settings (saved per card, spec §12.2). */
    val preset: StateFlow<DevicePreset> = _preset.asStateFlow()
    private var presetCardKey: String? = null

    private val _options = MutableStateFlow(RenderOptions())
    val options: StateFlow<RenderOptions> = _options.asStateFlow()

    private val _audioSettings = MutableStateFlow(AudioSettings())
    val audioSettings: StateFlow<AudioSettings> = _audioSettings.asStateFlow()

    private val _showDiagnostics = MutableStateFlow(false)
    val showDiagnostics: StateFlow<Boolean> = _showDiagnostics.asStateFlow()

    private val _controlsVisible = MutableStateFlow(true)
    val controlsVisible: StateFlow<Boolean> = _controlsVisible.asStateFlow()

    /** Keeps the bar up (QA screenshots, -qaPinControls on Apple). */
    var pinControls = false

    private val _feedback = MutableStateFlow<Feedback?>(null)
    val feedback: StateFlow<Feedback?> = _feedback.asStateFlow()

    private val _signal = MutableStateFlow<SignalContent>(SignalContent.Picture)
    /** What the live card shows: a picture, or its no-signal screen / black (HDCP). */
    val signal: StateFlow<SignalContent> = _signal.asStateFlow()

    private val _testPattern = MutableStateFlow<TestPattern?>(null)
    val testPattern: StateFlow<TestPattern?> = _testPattern.asStateFlow()

    private val _showScopes = MutableStateFlow(false)
    val showScopes: StateFlow<Boolean> = _showScopes.asStateFlow()

    private val _guides = MutableStateFlow(GuideSettings())
    val guides: StateFlow<GuideSettings> = _guides.asStateFlow()

    /** Source size of the picture on screen (for the guides overlay). */
    private val _sourceSize = MutableStateFlow(0 to 0)
    val sourceSize: StateFlow<Pair<Int, Int>> = _sourceSize.asStateFlow()

    private val _recordingSince = MutableStateFlow<Long?>(null)
    /** System.nanoTime() when recording started; null when not recording. */
    val recordingSince: StateFlow<Long?> = _recordingSince.asStateFlow()

    private val _recent = MutableStateFlow(false to 15)
    /** Save Recent setting: (on, minutes). */
    val recent: StateFlow<Pair<Boolean, Int>> = _recent.asStateFlow()

    /** Pro unlocked (the app sets it from ProStore); Save Recent only runs for Pro. */
    var isPro = false
        set(value) {
            field = value
            scope.launch { updateRolling() }
        }

    private val fanOut = AudioFanOut(audio::readTap)
    private var recorder: FileRecorder? = null
    private var rolling: RollingRecorder? = null
    private var rollingMode: LiveInfo? = null
    private val io = Dispatchers.IO.limitedParallelism(1)

    /** Stepped down to ≤ 30 fps because the device got hot (not saved; undone when it cools). */
    private var thermalStepDown = false

    private var lut: CubeLUT? = null
    private var lutUri: String? = null

    private var hideJob: Job? = null
    private var feedbackJob: Job? = null

    init {
        renderer.setOptions(_options.value)
        pokeControls()
        scope.launch { _showDiagnostics.value = prefs.appPrefs().showDiagnostics }
        scope.launch { watchSignal() }
        watchThermal()
        scope.launch {
            prefs.appPrefs().let {
                _recent.value = it.recentEnabled to it.recentMinutes
                _guides.value = it.guides
            }
            // Outputs follow the picture: a lost card ends the recording cleanly; Save Recent follows the mode.
            phase.collect { p ->
                if (p !is ViewerPhase.Live && recorder != null) {
                    stopRecording("Recording saved: the capture card was disconnected")
                }
                updateRolling()
            }
        }
        // A card came (back): restore its picture and sound settings.
        scope.launch {
            controller.currentCard.collect { card ->
                if (card == null || card.key == presetCardKey) return@collect
                presetCardKey = card.key
                prefs.setLastCard(card.key)
                apply(prefs.preset(card.key), save = false)
            }
        }
    }

    /** Settings changed something in the preset. */
    fun updatePreset(next: DevicePreset) {
        val formatChanged = next.format != _preset.value.format
        apply(next, save = true)
        if (formatChanged) controller.applyFormat(next.format)
    }

    /**
     * Before Android throttles: at SEVERE heat, drop an automatic format to ≤ 30 fps (half the decode
     * and GPU work) and say so; back to the card's own format once it is LIGHT or cooler (spec §12.5).
     */
    private fun watchThermal() {
        val power = context.getSystemService(android.os.PowerManager::class.java) ?: return
        power.addThermalStatusListener(context.mainExecutor) { status ->
            val hot = status >= android.os.PowerManager.THERMAL_STATUS_SEVERE
            val cool = status <= android.os.PowerManager.THERMAL_STATUS_LIGHT
            val format = _preset.value.format
            if (hot && !thermalStepDown && format == FormatPreference.Auto) {
                val cooler = controller.formatOptions.value.firstOrNull { (it.frameRate ?: 0.0) <= 30.5 } ?: return@addThermalStatusListener
                thermalStepDown = true
                controller.applyFormat(cooler)
                show(Feedback("This device is hot, so the picture drops to ${cooler.frameRate?.toInt() ?: 30} fps until it cools", isError = true))
            } else if (cool && thermalStepDown) {
                thermalStepDown = false
                controller.applyFormat(format)
                show(Feedback("Back to full frame rate"))
            }
        }
    }

    /** Record / Stop Recording (the app checks Pro first). */
    fun toggleRecording() {
        if (recorder != null) {
            scope.launch { stopRecording("Recording saved to Photos") }
            return
        }
        val live = phase.value as? ViewerPhase.Live
        when {
            _testPattern.value != null -> show(Feedback("Switch to the live input to record"))
            live == null -> show(Feedback("Connect a capture card to record"))
            else -> scope.launch {
                val started = withContext(io) {
                    runCatching {
                        audio.tapEnabled = true
                        FileRecorder(context, live.info.width, live.info.height, live.info.frameRate.toInt(), fanOut.open()) { renderer.setEncoder(RECORD_ID, it) }
                    }
                }
                started.onSuccess {
                    recorder = it
                    _recordingSince.value = it.startedAtNanos
                }.onFailure { show(Feedback("Couldn't start recording: ${it.message}", isError = true)) }
            }
        }
    }

    private suspend fun stopRecording(message: String) {
        val active = recorder ?: return
        recorder = null
        _recordingSince.value = null
        val uri = withContext(io) { runCatching { active.stop() }.getOrNull() }
        releaseTapIfIdle()
        show(if (uri != null) Feedback(message) else Feedback("Couldn't save the recording", isError = true))
    }

    /** Settings › Keep Recent Footage. */
    fun setRecent(enabled: Boolean, minutes: Int) {
        _recent.value = enabled to minutes
        scope.launch {
            prefs.setRecent(enabled, minutes)
            updateRolling()
        }
    }

    /** Saves the last [seconds] (null = everything kept). */
    fun saveRecent(seconds: Int?) {
        val active = rolling ?: return show(Feedback("Turn on Keep Recent Footage in Settings first"))
        show(Feedback("Saving recent footage…"))
        scope.launch {
            val uri = withContext(io) { runCatching { active.save(seconds) }.getOrNull() }
            show(if (uri != null) Feedback("Recent footage saved to Photos") else Feedback("Couldn't save recent footage", isError = true))
        }
    }

    /** Seconds Save Recent holds right now. */
    val recentBufferedSeconds: Double get() = rolling?.bufferedSeconds ?: 0.0

    /** Runs the rolling recorder while it's on, Pro and live, restarting it when the mode changes. */
    private suspend fun updateRolling() {
        val live = (phase.value as? ViewerPhase.Live)?.info
        val (enabled, minutes) = _recent.value
        val want = if (enabled && isPro && live != null && _testPattern.value == null) live else null
        if (want == rollingMode && rolling?.let { it.keepSeconds == minutes * 60 } != false) return
        val old = rolling
        rolling = null
        rollingMode = null
        withContext(io) { old?.stop() }
        if (want != null) {
            rolling = withContext(io) {
                runCatching {
                    audio.tapEnabled = true
                    RollingRecorder(context, java.io.File(context.cacheDir, "recent"), want.width, want.height, want.frameRate.toInt(),
                        fanOut.open(), { renderer.setEncoder(RECENT_ID, it) }, keepSeconds = minutes * 60)
                }.getOrNull()
            }
            rollingMode = want
        }
        releaseTapIfIdle()
    }

    private fun releaseTapIfIdle() {
        if (recorder == null && rolling == null) {
            fanOut.stop()
            audio.tapEnabled = false
        }
    }

    /** Show a generated pattern instead of the card (null = back to the live input). */
    fun setTestPattern(pattern: TestPattern?) {
        _testPattern.value = pattern
        if (pattern != null) renderer.startTestPattern(pattern) else renderer.stopSource()
        scope.launch { updateRolling() }
        pokeControls()
    }

    fun setGuides(next: GuideSettings) {
        _guides.value = next
        scope.launch { prefs.setGuides(next) }
        pokeControls()
    }

    fun setShowScopes(on: Boolean) {
        _showScopes.value = on
        pokeControls()
    }

    /** Copy Frame: the clean frame as a PNG on the clipboard (a content:// URI apps can paste). */
    fun copyFrame() {
        scope.launch {
            val uri = withContext(Dispatchers.IO) {
                renderer.screenshot()?.let { bitmap ->
                    val dir = java.io.File(context.cacheDir, "clips").apply { mkdirs() }
                    val file = java.io.File(dir, "HDMI Captain frame.png")
                    file.outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
                    androidx.core.content.FileProvider.getUriForFile(context, "${context.packageName}.files", file)
                }
            }
            if (uri == null) return@launch show(Feedback("Couldn't copy the frame: no picture yet", isError = true))
            val clipboard = context.getSystemService(android.content.ClipboardManager::class.java)
            clipboard.setPrimaryClip(android.content.ClipData.newUri(context.contentResolver, "HDMI Captain frame", uri))
            show(Feedback("Frame copied"))
        }
    }

    /** Settings › LUT: a .cube file the user picked (content:// URI with a persisted grant). */
    fun setLut(uri: String?, name: String?) = updatePreset(_preset.value.copy(lutUri = uri, lutName = name))

    /**
     * No-signal detection (SignalAnalyzer + the 1.5 s debounce), sampled 4× a second from the
     * renderer's grid; loss shows only after it persists, recovery at once.
     */
    private suspend fun watchSignal() {
        val debouncer = SignalDebouncer()
        var lastCount = -1L
        while (true) {
            delay(250)
            val stats = renderer.stats()
            _sourceSize.value = stats.width to stats.height
            if (phase.value !is ViewerPhase.Live || _testPattern.value != null) {
                debouncer.reset()
                _signal.value = SignalContent.Picture
                continue
            }
            val sample = renderer.signal()
            if (sample.count == lastCount) continue
            lastCount = sample.count
            debouncer.observe(SignalAnalyzer.classify(sample.stats, sample.fullRange), System.nanoTime() / 1e9)
            _signal.value = debouncer.current
        }
    }

    private fun apply(next: DevicePreset, save: Boolean) {
        _preset.value = next
        applyLook(next)
        _options.value = next.renderOptions
        renderer.setOptions(next.renderOptions)
        _audioSettings.value = next.audioSettings
        audio.settings = next.audioSettings
        val key = presetCardKey
        if (save && key != null) scope.launch { prefs.save(key, next) }
    }

    fun setScale(mode: ScaleMode) = updateOptions { it.copy(scale = mode) }

    fun setSharpPixels(on: Boolean) = updateOptions { it.copy(filter = if (on) FilterMode.Nearest else FilterMode.Linear) }

    fun setMuted(muted: Boolean) = updateAudio { it.copy(muted = muted) }

    fun setVolume(volume: Float) = updateAudio { it.copy(volume = volume.coerceIn(0f, 1f), muted = false) }

    fun setShowDiagnostics(on: Boolean) {
        _showDiagnostics.value = on
        scope.launch { prefs.setShowDiagnostics(on) }
    }

    /** Tap on the video: show or hide the bar. */
    fun toggleControls() {
        if (_controlsVisible.value && !pinControls) {
            _controlsVisible.value = false
            hideJob?.cancel()
        } else {
            pokeControls()
        }
    }

    /** Show the bar and restart its 3 s auto-hide (any interaction with it). */
    fun pokeControls() {
        _controlsVisible.value = true
        hideJob?.cancel()
        hideJob = scope.launch {
            delay(3_000)
            if (!pinControls && phase.value is ViewerPhase.Live) _controlsVisible.value = false
        }
    }

    /** Adjustments now; the LUT file is read off the main thread and applied when ready. */
    private fun applyLook(next: DevicePreset) {
        if (next.lutUri != lutUri) {
            lutUri = next.lutUri
            lut = null
            val uri = next.lutUri
            if (uri != null) {
                scope.launch {
                    val parsed = withContext(Dispatchers.IO) {
                        runCatching {
                            context.contentResolver.openInputStream(android.net.Uri.parse(uri))?.bufferedReader()?.use { it.readText() }
                                ?.let { CubeLUT.parse(it, next.lutName ?: "LUT") }
                        }
                    }
                    parsed.onFailure { show(Feedback("Couldn't read the LUT: ${it.message}", isError = true)) }
                    if (lutUri == uri) {
                        lut = parsed.getOrNull()
                        renderer.setLook(_preset.value.adjustments, lut, _preset.value.lutIntensity)
                    }
                }
            }
        }
        renderer.setLook(next.adjustments, lut, next.lutIntensity)
    }

    private companion object {
        const val RECORD_ID = 1
        const val RECENT_ID = 2
    }

    /** Saves the clean frame (no UI) to Pictures/HDMI Captain. */
    fun saveScreenshot() {
        scope.launch {
            val saved = withContext(Dispatchers.IO) {
                renderer.screenshot()?.let { MediaLibrary.saveScreenshot(context, it) }
            }
            show(if (saved != null) Feedback("Screenshot saved to Photos") else Feedback("Couldn't save the screenshot: no picture yet", isError = true))
        }
    }

    fun show(feedback: Feedback) {
        _feedback.value = feedback
        feedbackJob?.cancel()
        feedbackJob = scope.launch {
            delay(if (feedback.isError) 5_000 else 2_500)
            _feedback.value = null
        }
    }

    private fun updateOptions(change: (RenderOptions) -> RenderOptions) {
        apply(_preset.value.with(change(_options.value)), save = true)
        pokeControls()
    }

    private fun updateAudio(change: (AudioSettings) -> AudioSettings) {
        apply(_preset.value.with(change(_audioSettings.value)), save = true)
        pokeControls()
    }
}
