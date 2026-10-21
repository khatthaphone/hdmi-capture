package com.example.hdmicaptain.model

/** How the two captured channels map to the output (CaptureCore's ChannelMode; ordinals go to native). */
enum class ChannelMode { Auto, Normal, Swap, LeftToBoth, RightToBoth, Mono }

/** Card audio settings, as in CaptureCore's AudioPipeline.Settings. */
data class AudioSettings(
    /** 0…500 ms, to line sound up with a slower display. */
    val delayMs: Int = 0,
    val channelMode: ChannelMode = ChannelMode.Auto,
    val volume: Float = 1f,
    val muted: Boolean = false,
)
