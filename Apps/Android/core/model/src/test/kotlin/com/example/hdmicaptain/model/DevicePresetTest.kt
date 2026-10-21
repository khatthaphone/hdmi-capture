package com.example.hdmicaptain.model

import org.junit.Assert.assertEquals
import org.junit.Test

class DevicePresetTest {
    @Test fun roundTrip() {
        val preset = DevicePreset(
            format = FormatPreference.Pinned(1920, 1080, 60.0), volume = 0.5f, muted = true, scale = ScaleMode.Integer,
            filter = FilterMode.Nearest, audioDelayMs = 120, channelMode = ChannelMode.Swap,
            adjustments = ImageAdjustments(brightness = 0.1), lutName = "Rec709",
        )
        assertEquals(preset, DevicePreset.decode(preset.encode()))
    }

    @Test fun defaultsEncodeSmall() = assertEquals("{}", DevicePreset().encode())

    @Test fun toleratesOldNewAndBrokenData() {
        assertEquals(DevicePreset(volume = 0.3f), DevicePreset.decode("""{"volume":0.3,"someFutureField":true}"""))
        assertEquals(DevicePreset(), DevicePreset.decode("""{"scale":"NoSuchMode"}"""))
        assertEquals(DevicePreset(), DevicePreset.decode("not json"))
        assertEquals(DevicePreset(), DevicePreset.decode(null))
    }

    @Test fun splitsIntoRenderAndAudio() {
        val preset = DevicePreset(scale = ScaleMode.Fill, audioDelayMs = 40)
        assertEquals(RenderOptions(scale = ScaleMode.Fill), preset.renderOptions)
        assertEquals(preset, DevicePreset().with(preset.renderOptions).with(preset.audioSettings))
    }
}
