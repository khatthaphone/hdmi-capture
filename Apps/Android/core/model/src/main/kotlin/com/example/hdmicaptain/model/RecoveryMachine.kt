package com.example.hdmicaptain.model

/** Inputs to the capture state machine, from the USB/Camera2 source and the app lifecycle. */
sealed interface CaptureEvent {
    /** The user (or auto-select) wants this device live. */
    data class Start(val deviceKey: String, val deviceName: String) : CaptureEvent
    data object Stop : CaptureEvent
    /** The source delivered its first frame. */
    data class FirstFrame(val info: LiveInfo) : CaptureEvent
    /** Open failed or the stream broke; worth retrying. */
    data class StreamError(val reason: String) : CaptureEvent
    /** No frames for the stall timeout while live: usually the HDMI source changing mode. */
    data object Stalled : CaptureEvent
    data class Detached(val deviceKey: String) : CaptureEvent
    data class Attached(val deviceKey: String) : CaptureEvent
    data class Interrupted(val reason: PauseReason) : CaptureEvent
    data object InterruptionEnded : CaptureEvent
    /** Retrying can't help (e.g. USB permission denied). */
    data class Fatal(val message: String) : CaptureEvent
    /** A scheduled restart came due; stale ones (older generation) are ignored. */
    data class RetryDue(val generation: Int) : CaptureEvent
}

/** What the controller must do after an event. */
sealed interface CaptureAction {
    data object None : CaptureAction
    /** Open the source now. */
    data object Open : CaptureAction
    /** Close the source (and don't reopen until told). */
    data object Close : CaptureAction
    /** Close, then post [CaptureEvent.RetryDue] with [generation] after [delaySeconds]. */
    data class RetryAfter(val delaySeconds: Double, val generation: Int) : CaptureAction
}

/**
 * Pure port of `CaptureEngine`'s always-on rules (spec §5.3, §12.5): every failure retries with
 * backoff, unplug waits for the same card to come back, interruptions pause without retrying.
 * Not thread-safe: the controller feeds it from its single control thread.
 */
class RecoveryMachine {
    var state: CaptureState = CaptureState.Idle
        private set

    private var desired: CaptureEvent.Start? = null
    private var attempt = 0
    private var generation = 0
    private var interrupted: PauseReason? = null

    fun handle(event: CaptureEvent): CaptureAction = when (event) {
        is CaptureEvent.Start -> {
            desired = event
            attempt = 0
            generation++
            interrupted = null
            state = CaptureState.Connecting(event.deviceName)
            CaptureAction.Open
        }
        CaptureEvent.Stop -> {
            desired = null
            generation++
            state = CaptureState.Idle
            CaptureAction.Close
        }
        is CaptureEvent.FirstFrame -> {
            if (desired == null || interrupted != null) CaptureAction.None
            else {
                attempt = 0
                state = CaptureState.Live(event.info)
                CaptureAction.None
            }
        }
        is CaptureEvent.StreamError -> scheduleRestart(event.reason)
        CaptureEvent.Stalled ->
            if (state is CaptureState.Live || state is CaptureState.Connecting) scheduleRestart(RecoveryReason.NO_SIGNAL)
            else CaptureAction.None
        is CaptureEvent.Detached -> {
            if (desired?.deviceKey != event.deviceKey) CaptureAction.None
            else {
                generation++
                state = CaptureState.Recovering(RecoveryReason.DISCONNECTED)
                CaptureAction.Close
            }
        }
        is CaptureEvent.Attached -> {
            val want = desired
            if (want == null || want.deviceKey != event.deviceKey || state is CaptureState.Live || interrupted != null) CaptureAction.None
            else {
                attempt = 0
                generation++
                state = CaptureState.Connecting(want.deviceName)
                CaptureAction.Open
            }
        }
        is CaptureEvent.Interrupted -> {
            if (desired == null) CaptureAction.None
            else {
                interrupted = event.reason
                generation++
                state = CaptureState.Paused(event.reason)
                CaptureAction.Close
            }
        }
        CaptureEvent.InterruptionEnded -> {
            val want = desired
            if (interrupted == null || want == null) CaptureAction.None
            else {
                interrupted = null
                attempt = 0
                generation++
                state = CaptureState.Connecting(want.deviceName)
                CaptureAction.Open
            }
        }
        is CaptureEvent.Fatal -> {
            generation++
            state = CaptureState.Failed(event.message)
            CaptureAction.Close
        }
        is CaptureEvent.RetryDue -> {
            val want = desired
            if (event.generation != generation || want == null || interrupted != null) CaptureAction.None
            else {
                state = CaptureState.Connecting(want.deviceName)
                CaptureAction.Open
            }
        }
    }

    private fun scheduleRestart(reason: String): CaptureAction {
        if (desired == null || interrupted != null) return CaptureAction.None
        attempt++
        generation++
        state = CaptureState.Recovering(reason)
        return CaptureAction.RetryAfter(restartDelaySeconds(attempt), generation)
    }
}
