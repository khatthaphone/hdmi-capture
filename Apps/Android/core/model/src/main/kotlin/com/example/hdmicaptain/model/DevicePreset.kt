package com.example.hdmicaptain.model

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Picture look, as CaptureCore's ImageAdjustments; identity = (0, 1, 1, 1, 0). */
@Serializable
data class ImageAdjustments(
    val brightness: Double = 0.0,
    val contrast: Double = 1.0,
    val saturation: Double = 1.0,
    val gamma: Double = 1.0,
    val sharpness: Double = 0.0,
) {
    val isIdentity: Boolean get() = this == ImageAdjustments()
}

/**
 * Per-card settings (CaptureCore's DevicePreset), remembered by the card's stable key so a card
 * gets its own format, picture and sound back whenever it is plugged in.
 */
@Serializable
data class DevicePreset(
    val format: FormatPreference = FormatPreference.Auto,
    val volume: Float = 1f,
    val muted: Boolean = false,
    val scale: ScaleMode = ScaleMode.Fit,
    val filter: FilterMode = FilterMode.Linear,
    val colorRange: ColorRange = ColorRange.Auto,
    val audioDelayMs: Int = 0,
    val channelMode: ChannelMode = ChannelMode.Auto,
    val adjustments: ImageAdjustments = ImageAdjustments(),
    /** content:// URI of a .cube file the user picked (persisted permission). */
    val lutUri: String? = null,
    val lutName: String? = null,
    val lutIntensity: Double = 1.0,
) {
    val renderOptions: RenderOptions get() = RenderOptions(scale, filter, colorRange)
    val audioSettings: AudioSettings get() = AudioSettings(audioDelayMs, channelMode, volume, muted)

    fun with(options: RenderOptions) = copy(scale = options.scale, filter = options.filter, colorRange = options.colorRange)
    fun with(audio: AudioSettings) =
        copy(audioDelayMs = audio.delayMs, channelMode = audio.channelMode, volume = audio.volume, muted = audio.muted)

    fun encode(): String = json.encodeToString(serializer(), this)

    companion object {
        // Tolerant like the Swift decoder: unknown keys from newer versions and missing keys from
        // older ones fall back to defaults, so saved presets survive upgrades both ways.
        private val json = Json { ignoreUnknownKeys = true; encodeDefaults = false; coerceInputValues = true }

        fun decode(text: String?): DevicePreset =
            text?.let { runCatching { json.decodeFromString(serializer(), it) }.getOrNull() } ?: DevicePreset()
    }
}
