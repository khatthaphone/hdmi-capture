package com.example.hdmicaptain.viewer

import android.content.Context
import com.example.hdmicaptain.audio.CardAudioPort
import com.example.hdmicaptain.capture.Card
import com.example.hdmicaptain.capture.CaptureController
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.model.AudioSettings
import com.example.hdmicaptain.model.FilterMode
import com.example.hdmicaptain.model.RenderOptions
import com.example.hdmicaptain.model.ScaleMode
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
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
) {
    val phase: StateFlow<ViewerPhase> get() = controller.phase

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

    private var hideJob: Job? = null
    private var feedbackJob: Job? = null

    init {
        renderer.setOptions(_options.value)
        pokeControls()
    }

    fun setScale(mode: ScaleMode) = updateOptions { it.copy(scale = mode) }

    fun setSharpPixels(on: Boolean) = updateOptions { it.copy(filter = if (on) FilterMode.Nearest else FilterMode.Linear) }

    fun setMuted(muted: Boolean) = updateAudio { it.copy(muted = muted) }

    fun setVolume(volume: Float) = updateAudio { it.copy(volume = volume.coerceIn(0f, 1f), muted = false) }

    fun setShowDiagnostics(on: Boolean) {
        _showDiagnostics.value = on
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
        _options.update(change)
        renderer.setOptions(_options.value)
        pokeControls()
    }

    private fun updateAudio(change: (AudioSettings) -> AudioSettings) {
        _audioSettings.update(change)
        audio.settings = _audioSettings.value
        pokeControls()
    }
}
