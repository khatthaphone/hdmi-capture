package com.example.hdmicaptain.viewer

import android.content.Context
import com.example.hdmicaptain.audio.CardAudioPort
import com.example.hdmicaptain.capture.Card
import com.example.hdmicaptain.capture.CaptureController
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.model.AudioSettings
import com.example.hdmicaptain.model.CubeLUT
import com.example.hdmicaptain.model.GuideSettings
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

    private val _guides = MutableStateFlow(GuideSettings())
    val guides: StateFlow<GuideSettings> = _guides.asStateFlow()

    /** Source size of the picture on screen (for the guides overlay). */
    private val _sourceSize = MutableStateFlow(0 to 0)
    val sourceSize: StateFlow<Pair<Int, Int>> = _sourceSize.asStateFlow()

    private var lut: CubeLUT? = null
    private var lutUri: String? = null

    private var hideJob: Job? = null
    private var feedbackJob: Job? = null

    init {
        renderer.setOptions(_options.value)
        pokeControls()
        scope.launch { _showDiagnostics.value = prefs.appPrefs().showDiagnostics }
        scope.launch { watchSignal() }
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

    /** Show a generated pattern instead of the card (null = back to the live input). */
    fun setTestPattern(pattern: TestPattern?) {
        _testPattern.value = pattern
        if (pattern != null) renderer.startTestPattern(pattern) else renderer.stopSource()
        pokeControls()
    }

    fun setGuides(next: GuideSettings) {
        _guides.value = next
        pokeControls()
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
