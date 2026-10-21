package com.example.hdmicaptain.capture

import com.example.hdmicaptain.model.CaptureAction
import com.example.hdmicaptain.model.CaptureEvent
import com.example.hdmicaptain.model.CaptureState
import com.example.hdmicaptain.model.FormatChoice
import com.example.hdmicaptain.model.FormatDescriptor
import com.example.hdmicaptain.model.FormatPreference
import com.example.hdmicaptain.model.FormatSelector
import com.example.hdmicaptain.model.LiveInfo
import com.example.hdmicaptain.model.PauseReason
import com.example.hdmicaptain.model.RecoveryMachine
import com.example.hdmicaptain.model.RecoveryReason
import com.example.hdmicaptain.model.UsbLink
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** A capture device the controller can choose: a USB card, or a Camera2 external camera. */
interface Card {
    /** Stable identity (DeviceKey for USB, "camera2:<id>" for Camera2). */
    val key: String
    val name: String
}

/** Lists cards and handles USB permission. Real one: UsbCardCatalog (+ Camera2 externals). */
interface CardCatalog {
    val cards: StateFlow<List<Card>>
    fun start()
    fun stop()
    fun hasPermission(card: Card): Boolean
    suspend fun requestPermission(card: Card): Boolean
}

/** Opens a card and streams it into the renderer. Calls block; they run on the control thread. */
interface CaptureDriver {
    data class Opened(val formats: List<FormatDescriptor>, val link: UsbLink?, val speed: String)

    /** Frames delivered to the renderer so far, and when the last one arrived (monotonic ns). */
    data class Progress(val frames: Long, val lastFrameNanos: Long)

    fun open(card: Card): Opened
    fun start(choice: FormatChoice)
    fun progress(): Progress
    fun close()
}

/** The card's sound. Real one: CardAudio. Failures only cost sound, never the picture. */
interface AudioPort {
    fun start(card: Card)
    fun stop()
}

/** What the viewer shows (spec §12.7): one screen per phase, like ViewerModel.Phase on Apple. */
sealed interface ViewerPhase {
    data object Scanning : ViewerPhase
    data class UsbPermission(val cardName: String, val denied: Boolean) : ViewerPhase
    /** The remembered card was unplugged: waiting for it to come back. */
    data class Waiting(val cardName: String) : ViewerPhase
    data class Connecting(val cardName: String, val slow: Boolean = false) : ViewerPhase
    data class Live(val info: LiveInfo, val speed: String) : ViewerPhase
    data class Recovering(val reason: String) : ViewerPhase
    data class Paused(val reason: PauseReason) : ViewerPhase
    data class Failed(val message: String) : ViewerPhase
}

/**
 * The Android CaptureEngine (spec §12.2, §12.5): chooses a card, asks for USB permission, opens
 * and streams it, and keeps it live — every failure retries with backoff (RecoveryMachine),
 * unplug waits for the same card, background pauses and returns. All state is confined to
 * [control] (a single-thread dispatcher); public methods only post onto it.
 */
class CaptureController(
    private val scope: CoroutineScope,
    private val control: CoroutineDispatcher,
    private val catalog: CardCatalog,
    private val driver: CaptureDriver,
    private val audio: AudioPort?,
    private val nanoTime: () -> Long = System::nanoTime,
    /** The card's saved format (its DevicePreset), asked for before each open. */
    private val formatFor: suspend (Card) -> FormatPreference = { FormatPreference.Auto },
) {
    private val _phase = MutableStateFlow<ViewerPhase>(ViewerPhase.Scanning)
    val phase: StateFlow<ViewerPhase> = _phase.asStateFlow()

    /** Last card used (stable key), so it is chosen again; persisted by the app. */
    var preferredKey: String? = null
    /** Settings → Format; Auto by default. */
    var formatPreference: FormatPreference = FormatPreference.Auto

    private val _currentCard = MutableStateFlow<Card?>(null)
    /** The card being shown (or waited for). */
    val currentCard: StateFlow<Card?> = _currentCard.asStateFlow()

    private val _formatOptions = MutableStateFlow<List<FormatPreference.Pinned>>(emptyList())
    /** What the open card can do over this USB link, best first (Settings → Format). */
    val formatOptions: StateFlow<List<FormatPreference.Pinned>> = _formatOptions.asStateFlow()

    private val machine = RecoveryMachine()
    private var card: Card? = null
    private var opened: CaptureDriver.Opened? = null
    private var pendingInfo: LiveInfo? = null
    private var openedAt = 0L
    private var framesAtOpen = 0L
    private var retry: Job? = null
    private var watchdog: Job? = null
    private var visible = true

    fun start() = scope.launch(control) {
        catalog.start()
        scope.launch(control) { catalog.cards.collect { onCards(it) } }
        watchdog = scope.launch(control) {
            while (isActive) {
                delay(WATCHDOG_MS)
                checkProgress()
            }
        }
    }

    fun stop() = scope.launch(control) {
        watchdog?.cancel()
        perform(machine.handle(CaptureEvent.Stop))
        catalog.stop()
    }

    /** App visible again (Lifecycle ON_START): resume where we left off. */
    fun onVisible() = scope.launch(control) {
        visible = true
        perform(machine.handle(CaptureEvent.InterruptionEnded))
        publish()
    }

    /** App no longer visible (ON_STOP): Android forbids background camera use; release the card. */
    fun onHidden() = scope.launch(control) {
        visible = false
        perform(machine.handle(CaptureEvent.Interrupted(PauseReason.Background)))
        publish()
    }

    /** User picked a card (Settings / input menu). */
    fun select(key: String) = scope.launch(control) {
        catalog.cards.value.firstOrNull { it.key == key }?.let { choose(it) }
    }

    /** Reconnect button: start over on the current card. */
    fun retry() = scope.launch(control) {
        val current = card ?: catalog.cards.value.lastOrNull() ?: return@launch
        choose(current)
    }

    /** Reopen with a new format preference. */
    fun applyFormat(preference: FormatPreference) = scope.launch(control) {
        formatPreference = preference
        card?.let { choose(it) }
    }

    private suspend fun onCards(cards: List<Card>) {
        val current = card
        when {
            current == null -> pick(cards)?.let { choose(it) }
            cards.none { it.key == current.key } -> {
                perform(machine.handle(CaptureEvent.Detached(current.key)))
                // A different card is still plugged in: switch to it rather than wait.
                cards.lastOrNull()?.let { choose(it) }
            }
            else -> perform(machine.handle(CaptureEvent.Attached(current.key)))
        }
        if (cards.isEmpty() && card == null) _phase.value = ViewerPhase.Scanning
        publish()
    }

    /** Remembered card if present, else the most recently attached (spec §12.2). */
    private fun pick(cards: List<Card>): Card? = cards.firstOrNull { it.key == preferredKey } ?: cards.lastOrNull()

    private suspend fun choose(next: Card) {
        if (card != null && card?.key != next.key) perform(machine.handle(CaptureEvent.Stop))
        card = next
        _currentCard.value = next
        preferredKey = next.key
        formatPreference = formatFor(next)
        if (!catalog.hasPermission(next)) {
            _phase.value = ViewerPhase.UsbPermission(next.name, denied = false)
            if (!catalog.requestPermission(next)) {
                _phase.value = ViewerPhase.UsbPermission(next.name, denied = true)
                return
            }
        }
        perform(machine.handle(CaptureEvent.Start(next.key, next.name)))
        publish()
    }

    private fun perform(action: CaptureAction) {
        when (action) {
            CaptureAction.None -> Unit
            CaptureAction.Open -> open()
            CaptureAction.Close -> close()
            is CaptureAction.RetryAfter -> {
                close()
                retry?.cancel()
                retry = scope.launch(control) {
                    delay((action.delaySeconds * 1000).toLong())
                    perform(machine.handle(CaptureEvent.RetryDue(action.generation)))
                    publish()
                }
            }
        }
    }

    private fun open() {
        val target = card ?: return
        close()
        try {
            val result = driver.open(target)
            val choice = FormatSelector.select(result.formats, formatPreference, result.link)
                ?: throw IllegalStateException("The card offers no usable video format")
            driver.start(choice)
            opened = result
            _formatOptions.value = FormatSelector.options(result.formats, result.link)
            pendingInfo = LiveInfo(target.name, choice.format.width, choice.format.height, choice.frameRate, choice.format.fourCC)
            openedAt = nanoTime()
            framesAtOpen = driver.progress().frames
            runCatching { audio?.start(target) }
        } catch (e: Exception) {
            perform(machine.handle(CaptureEvent.StreamError(e.message ?: "Couldn't open the capture card")))
        }
    }

    private fun close() {
        if (opened == null) return
        runCatching { audio?.stop() }
        runCatching { driver.close() }
        opened = null
        pendingInfo = null
    }

    /** First frame → live; silence while live → "No signal" restart (the 3 s stall rule from CaptureEngine). */
    private fun checkProgress() {
        if (opened == null) return
        val p = driver.progress()
        val now = nanoTime()
        when (machine.state) {
            is CaptureState.Connecting -> when {
                p.frames > framesAtOpen -> pendingInfo?.let { perform(machine.handle(CaptureEvent.FirstFrame(it))) }
                now - openedAt > NO_FRAMES_NS -> perform(machine.handle(CaptureEvent.StreamError(RecoveryReason.NO_FRAMES)))
            }
            is CaptureState.Live -> if (now - p.lastFrameNanos > STALL_NS) perform(machine.handle(CaptureEvent.Stalled))
            else -> Unit
        }
        publish()
    }

    private fun publish() {
        val name = card?.name ?: ""
        val connectingSlow = opened != null && nanoTime() - openedAt > SLOW_NS
        _phase.value = when (val s = machine.state) {
            CaptureState.Idle -> if (_phase.value is ViewerPhase.UsbPermission) _phase.value else ViewerPhase.Scanning
            is CaptureState.Connecting -> ViewerPhase.Connecting(s.deviceName, slow = connectingSlow)
            is CaptureState.Live -> ViewerPhase.Live(s.info, opened?.speed ?: "")
            is CaptureState.Recovering -> if (s.reason == RecoveryReason.DISCONNECTED) ViewerPhase.Waiting(name) else ViewerPhase.Recovering(s.reason)
            is CaptureState.Paused -> ViewerPhase.Paused(s.reason)
            is CaptureState.Failed -> ViewerPhase.Failed(s.message)
        }
    }

    companion object {
        const val WATCHDOG_MS = 250L
        const val STALL_NS = 3_000_000_000L
        /** Opened, streaming started, still nothing: often the global Camera privacy switch. */
        const val NO_FRAMES_NS = 5_000_000_000L
        const val SLOW_NS = 8_000_000_000L
    }
}
