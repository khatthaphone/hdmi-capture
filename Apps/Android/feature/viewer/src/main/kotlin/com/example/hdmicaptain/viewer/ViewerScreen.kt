package com.example.hdmicaptain.viewer

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.focusable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.isCtrlPressed
import androidx.compose.ui.input.key.isShiftPressed
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.input.pointer.PointerEventType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.model.GuideSettings
import com.example.hdmicaptain.model.ScaleMode
import com.example.hdmicaptain.model.SignalContent
import com.example.hdmicaptain.model.TestPattern
import kotlinx.coroutines.delay

/** App-level actions the viewer can't do itself (Pro sheet, settings, PiP, system settings). */
interface ViewerHost : StatusActions {
    fun record()
    fun saveRecent(seconds: Int?)
    fun pictureInPicture()
    fun openSettings()
}

/**
 * The viewer (spec §12.7): video, state screens, control bar, diagnostics and feedback.
 * The video is always composed, so its surface (and EGL) survive every state change.
 */
@Composable
fun ViewerScreen(
    model: ViewerModel,
    host: ViewerHost,
    /** Before capture runs: the camera/microphone permission screens. */
    permission: PermissionPhase?,
    inPictureInPicture: Boolean,
    /** HDMI Captain Pro unlocked (lock icons on Record and Save Recent otherwise). */
    isPro: Boolean,
    modifier: Modifier = Modifier,
) {
    val phase by model.phase.collectAsState()
    val options by model.options.collectAsState()
    val audio by model.audioSettings.collectAsState()
    val diagnostics by model.showDiagnostics.collectAsState()
    val controlsVisible by model.controlsVisible.collectAsState()
    val cards by model.cards.collectAsState()
    val feedback by model.feedback.collectAsState()
    val pattern by model.testPattern.collectAsState()
    val signal by model.signal.collectAsState()
    val guides by model.guides.collectAsState()
    val sourceSize by model.sourceSize.collectAsState()
    val testPattern = pattern != null
    val recordingSince by model.recordingSince.collectAsState()
    var showHelp by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }

    val actions = remember(model, host) {
        object : ControlBarActions {
            override fun selectInput(key: String) { model.controller.select(key) }
            override fun setMuted(muted: Boolean) = model.setMuted(muted)
            override fun setVolume(volume: Float) = model.setVolume(volume)
            override fun screenshot() = model.saveScreenshot()
            override fun record() = host.record()
            override fun saveRecent(seconds: Int?) = host.saveRecent(seconds)
            override fun setScale(mode: ScaleMode) = model.setScale(mode)
            override fun setSharpPixels(on: Boolean) = model.setSharpPixels(on)
            override fun setShowDiagnostics(on: Boolean) = model.setShowDiagnostics(on)
            override fun setTestPattern(pattern: TestPattern?) = model.setTestPattern(pattern)
            override fun setGuides(guides: GuideSettings) = model.setGuides(guides)
            override fun pictureInPicture() = host.pictureInPicture()
            override fun openSettings() = host.openSettings()
            override fun showHelp() { showHelp = true }
            override fun poke() = model.pokeControls()
        }
    }
    val statusActions = remember(host) {
        object : StatusActions by host {
            override fun showHelp() { showHelp = true }
        }
    }

    Box(
        modifier
            .fillMaxSize()
            .background(Color.Black)
            .focusRequester(focus)
            .focusable()
            .onPreviewKeyEvent { event -> event.type == KeyEventType.KeyDown && shortcut(event.key, event.isCtrlPressed, event.isShiftPressed, model, host) }
            .pointerInput(model) { detectTapGestures(onTap = { model.toggleControls() }) }
            .pointerInput(model) {
                // Mouse or trackpad (DeX, ChromeOS, desktop windows): reveal the bar near the bottom edge.
                awaitPointerEventScope {
                    while (true) {
                        val event = awaitPointerEvent()
                        if (event.type == PointerEventType.Move && event.changes.any { it.position.y > size.height - 140.dp.toPx() }) {
                            model.pokeControls()
                        }
                    }
                }
            },
    ) {
        VideoSurface(model.renderer, Modifier.fillMaxSize())

        val live = phase is ViewerPhase.Live || testPattern
        if (live && !inPictureInPicture) GuidesOverlay(guides, sourceSize.first, sourceSize.second, options.scale)
        when {
            permission != null -> PermissionScreen(permission, statusActions)
            testPattern -> Unit
            phase is ViewerPhase.Live && signal.isNoSignal -> NoSignalOverlay(black = signal == SignalContent.Black, actions = statusActions)
            else -> StatusOverlay(phase, statusActions)
        }

        if (diagnostics && !inPictureInPicture) {
            Diagnostics(model, phase, Modifier.align(Alignment.TopStart).safeDrawingPadding().padding(12.dp))
        }

        recordingSince?.let { since ->
            RecordingPill(since, Modifier.align(Alignment.TopEnd).safeDrawingPadding().padding(12.dp))
        }

        feedback?.let {
            Text(
                it.text,
                color = Color.White,
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .safeDrawingPadding()
                    .padding(top = 16.dp)
                    .background(if (it.isError) Color(0xE6B3261E) else Color(0xE6161B2B), RoundedCornerShape(16.dp))
                    .padding(horizontal = 16.dp, vertical = 8.dp)
                    .testTag("feedback"),
            )
        }

        AnimatedVisibility(
            visible = permission == null && !inPictureInPicture && (controlsVisible || !live),
            enter = fadeIn(),
            exit = fadeOut(),
            modifier = Modifier.align(Alignment.BottomCenter).safeDrawingPadding(),
        ) {
            ControlBar(
                cards = cards,
                currentCard = model.controller.preferredKey,
                options = options,
                audio = audio,
                showDiagnostics = diagnostics,
                testPattern = pattern,
                guides = guides,
                isPro = isPro,
                isRecording = recordingSince != null,
                actions = actions,
            )
        }
    }
    if (showHelp) HelpSheet(onDismiss = { showHelp = false })
}

/** REC dot + elapsed time while recording (UI only; never in the recording). */
@Composable
private fun RecordingPill(sinceNanos: Long, modifier: Modifier = Modifier) {
    var seconds by remember { mutableStateOf(0L) }
    LaunchedEffect(sinceNanos) {
        while (true) {
            seconds = (System.nanoTime() - sinceNanos) / 1_000_000_000
            delay(500)
        }
    }
    Row(
        modifier.background(Color.Black.copy(alpha = 0.6f), RoundedCornerShape(12.dp)).padding(horizontal = 10.dp, vertical = 4.dp).testTag("rec"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(10.dp).background(Color(0xFFFF453A), CircleShape))
        Text("  REC %d:%02d".format(seconds / 60, seconds % 60), color = Color.White, style = MaterialTheme.typography.labelMedium)
    }
}

/** Keyboard shortcuts, the Mac set with Ctrl for ⌘ (spec §5.6, §12.7). */
private fun shortcut(key: Key, ctrl: Boolean, shift: Boolean, model: ViewerModel, host: ViewerHost): Boolean {
    if (!ctrl) return false
    when {
        shift && key == Key.S -> model.saveScreenshot()
        shift && key == Key.M -> model.setMuted(!model.audioSettings.value.muted)
        key == Key.One -> model.setScale(ScaleMode.Fit)
        key == Key.Two -> model.setScale(ScaleMode.Fill)
        key == Key.Three -> model.setScale(ScaleMode.Stretch)
        key == Key.Four -> model.setScale(ScaleMode.Integer)
        key == Key.I -> model.setShowDiagnostics(!model.showDiagnostics.value)
        key == Key.R -> host.record()
        key == Key.Comma -> host.openSettings()
        else -> return false
    }
    return true
}

/** Diagnostics, sampled twice a second (never per frame). */
@Composable
private fun Diagnostics(model: ViewerModel, phase: ViewerPhase, modifier: Modifier = Modifier) {
    var text by remember { mutableStateOf("") }
    LaunchedEffect(model, phase) {
        var last = model.renderer.stats()
        while (true) {
            delay(500)
            val now = model.renderer.stats()
            val fpsIn = (now.framesIn - last.framesIn) * 2
            val fpsOut = (now.framesDrawn - last.framesDrawn) * 2
            val mode = (phase as? ViewerPhase.Live)?.let { "${it.info.fourCC} · ${it.speed}" } ?: ""
            text = "${now.width}×${now.height} $mode\nin $fpsIn fps · shown $fpsOut fps · ${"%.1f".format(now.latencyNanos / 1e6)} ms"
            last = now
        }
    }
    Text(
        text,
        modifier.background(Color.Black.copy(alpha = 0.6f), RoundedCornerShape(8.dp)).padding(8.dp).testTag("diagnostics"),
        color = Color.White,
        style = MaterialTheme.typography.labelMedium,
    )
}
