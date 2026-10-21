package com.example.hdmicaptain.model

/** What the capture card is delivering right now. */
data class LiveInfo(val deviceName: String, val width: Int, val height: Int, val frameRate: Double, val fourCC: String)

/** Port of CaptureCore's `CaptureState`; the UI renders one screen per case. */
sealed interface CaptureState {
    data object Idle : CaptureState
    data class Connecting(val deviceName: String) : CaptureState
    data class Live(val info: LiveInfo) : CaptureState
    /** Lost the stream; the controller is retrying on its own. */
    data class Recovering(val reason: String) : CaptureState
    /** Android took the camera away for now; capture resumes by itself when the reason goes away. */
    data class Paused(val reason: PauseReason) : CaptureState
    data class Failed(val message: String) : CaptureState
}

/** Why capture is paused. Mirrors CaptureCore's `PauseReason`, with Android's own causes. */
enum class PauseReason {
    /** The app isn't visible; Android forbids background camera use without a foreground service. */
    Background,
    /** Another app took camera priority (multi-window). */
    InUseByAnotherApp,
    /** The global Camera or Microphone privacy switch is on: the card would show black. */
    PrivacyToggle,
    TooHot,
    Other,
}

/** Recovery reasons shown to the user; same strings as the Apple engine (see StatusCopy). */
object RecoveryReason {
    const val NO_SIGNAL = "No signal"
    const val DISCONNECTED = "Device disconnected"
    const val WAITING = "Waiting for device"
    const val STOPPED = "Capture stopped"
}

/** Restart delay for the n-th consecutive attempt (1-based): 0.25 s doubling, capped at 4 s. */
fun restartDelaySeconds(attempt: Int): Double = minOf(0.25 * Math.pow(2.0, (attempt - 1).toDouble()), 4.0)
