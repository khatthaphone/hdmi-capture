package com.example.hdmicaptain.audio

/** Card audio → speakers via Oboe, with delay, channel fixes and meters (task 075). */
interface AudioPipeline {
    var volume: Float
    var muted: Boolean
}
