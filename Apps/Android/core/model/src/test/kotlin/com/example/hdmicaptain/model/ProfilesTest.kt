package com.example.hdmicaptain.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ProfilesTest {
    @Test fun looksMatchApple() {
        assertEquals(LookPreset.Standard, LookPreset.of(ImageAdjustments()))
        assertEquals(LookPreset.Vivid, LookPreset.of(ImageAdjustments(contrast = 1.15, saturation = 1.35, sharpness = 0.2)))
        assertNull(LookPreset.of(ImageAdjustments(brightness = 0.3)))
    }

    @Test fun retroProfileAppliesIntegerNearest() {
        val retro = Profile.builtIns.first { it.name == "Retro" }
        val applied = retro.settings.applyTo(DevicePreset(volume = 0.4f))
        assertEquals(ScaleMode.Integer, applied.scale)
        assertEquals(FilterMode.Nearest, applied.filter)
        assertEquals(0.4f, applied.volume)  // sound level isn't part of a profile
    }

    @Test fun userProfilesRoundTripWithoutBuiltIns() {
        val mine = Profile("u1", "PS2", ProfileSettings(DevicePreset(scale = ScaleMode.Fill, audioDelayMs = 80)))
        val decoded = Profile.decode(Profile.encode(Profile.builtIns + mine))
        assertEquals(listOf(mine), decoded)
        assertEquals(emptyList<Profile>(), Profile.decode("garbage"))
    }
}
