package com.example.hdmicaptain.model

import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.Json

/** Picture looks (CaptureCore's ImageAdjustments.Preset), same values as on Apple. */
enum class LookPreset(val title: String, val values: ImageAdjustments) {
    Standard("Standard", ImageAdjustments()),
    Game("Game", ImageAdjustments(contrast = 1.08, saturation = 1.15, sharpness = 0.35)),
    Cinema("Cinema", ImageAdjustments(brightness = -0.02, contrast = 1.05, saturation = 0.92, gamma = 0.95)),
    Vivid("Vivid", ImageAdjustments(contrast = 1.15, saturation = 1.35, sharpness = 0.2));

    companion object {
        /** The look these values match, if any. */
        fun of(adjustments: ImageAdjustments): LookPreset? = entries.firstOrNull { it.values == adjustments }
    }
}

/** The parts of a card's settings a profile captures (CaptureCore's ProfileSettings). */
@Serializable
data class ProfileSettings(
    val format: FormatPreference = FormatPreference.Auto,
    val scale: ScaleMode = ScaleMode.Fit,
    val filter: FilterMode = FilterMode.Linear,
    val colorRange: ColorRange = ColorRange.Auto,
    val adjustments: ImageAdjustments = ImageAdjustments(),
    val audioDelayMs: Int = 0,
    val channelMode: ChannelMode = ChannelMode.Auto,
) {
    constructor(preset: DevicePreset) : this(
        preset.format, preset.scale, preset.filter, preset.colorRange, preset.adjustments, preset.audioDelayMs, preset.channelMode,
    )

    fun applyTo(preset: DevicePreset) = preset.copy(
        format = format, scale = scale, filter = filter, colorRange = colorRange,
        adjustments = adjustments, audioDelayMs = audioDelayMs, channelMode = channelMode,
    )
}

/** A named bundle of settings, e.g. "Retro" for an old console (CaptureCore's Profile). */
@Serializable
data class Profile(
    val id: String,
    val name: String,
    val settings: ProfileSettings,
    val isBuiltIn: Boolean = false,
) {
    companion object {
        /** Same ids and settings as the Apple built-ins. */
        val builtIns = listOf(
            Profile("00000000-0000-0000-0000-00000000B001", "Modern Console", ProfileSettings(), isBuiltIn = true),
            Profile("00000000-0000-0000-0000-00000000B002", "Retro", ProfileSettings(scale = ScaleMode.Integer, filter = FilterMode.Nearest), isBuiltIn = true),
            Profile("00000000-0000-0000-0000-00000000B003", "Camera", ProfileSettings(adjustments = LookPreset.Cinema.values), isBuiltIn = true),
            Profile(
                "00000000-0000-0000-0000-00000000B004", "Desktop",
                ProfileSettings(format = FormatPreference.Pinned(1920, 1080, null), adjustments = ImageAdjustments(sharpness = 0.25)),
                isBuiltIn = true,
            ),
        )

        private val json = Json { ignoreUnknownKeys = true; coerceInputValues = true }
        private val list = ListSerializer(serializer())

        fun encode(profiles: List<Profile>): String = json.encodeToString(list, profiles.filter { !it.isBuiltIn })

        /** Tolerant: a damaged list gives no user profiles rather than a crash. */
        fun decode(text: String?): List<Profile> =
            text?.let { runCatching { json.decodeFromString(list, it) }.getOrNull() }.orEmpty().map { it.copy(isBuiltIn = false) }
    }
}
