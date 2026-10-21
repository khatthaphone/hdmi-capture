package com.example.hdmicaptain.model

import com.example.hdmicaptain.model.Fixtures.dbl
import com.example.hdmicaptain.model.Fixtures.int
import com.example.hdmicaptain.model.Fixtures.str
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class FormatSelectorTest {
    private fun run(file: String) {
        val root = Fixtures.load(file)
        val sets = root["formatSets"]?.jsonObject.orEmpty()
        for (case in root["cases"]!!.jsonArray.map { it.jsonObject }) {
            val name = case.str("name")
            val formats = (case["formats"] ?: sets[case.str("formatSet")])!!.jsonArray.map { Fixtures.format(it.jsonObject) }
            val link = case["link"]?.takeIf { it != JsonNull }?.jsonPrimitive?.content?.let { if (it == "usb2") UsbLink.Usb2 else UsbLink.Usb3 }
            val choice = FormatSelector.select(formats, Fixtures.preference(case["preference"]), link)
            val expect = case["expect"]
            if (expect == null || expect == JsonNull) {
                assertNull(name, choice)
                continue
            }
            val e = expect as JsonObject
            requireNotNull(choice) { name }
            assertEquals(name, "${e.int("w")}x${e.int("h")} ${e.str("cc")}", "${choice.format.width}x${choice.format.height} ${choice.format.fourCC}")
            assertEquals(name, e.dbl("fps"), choice.frameRate, 1e-6)
        }
    }

    @Test fun sharedSelectionFixtures() = run("format-selection.json")

    @Test fun androidBandwidthFixtures() = run("format-bandwidth.json")

    @Test fun optionsAreUniqueAndBestFirst() {
        val ms2131 = Fixtures.load("format-selection.json")["formatSets"]!!.jsonObject["ms2131"]!!.jsonArray.map { Fixtures.format(it.jsonObject) }
        val options = FormatSelector.options(ms2131)
        assertEquals(FormatPreference.Pinned(1920, 1080, 30.0), options.first())
        assertEquals(options.size, options.toSet().size)
    }

    @Test fun usb2OptionsHideRatesThatDontFit() {
        val yuy2 = FormatDescriptor(1920, 1080, "YUY2", listOf(60.0, 30.0, 5.0).map { FrameRateRange(it, it) })
        assertEquals(listOf(FormatPreference.Pinned(1920, 1080, 5.0)), FormatSelector.options(listOf(yuy2), UsbLink.Usb2))
        assertTrue(FormatSelector.options(listOf(yuy2), UsbLink.Usb3).first().frameRate == 60.0)
    }
}
