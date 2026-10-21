package com.example.hdmicaptain.model

import kotlinx.serialization.json.double
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test

class RenderGeometryTest {
    @Test fun sharedFixture() {
        for (case in Fixtures.load("render-geometry.json")["cases"]!!.jsonArray.map { it.jsonObject }) {
            fun nums(key: String) = case[key]!!.jsonArray.map { it.jsonPrimitive.double }
            val (sw, sh) = nums("source")
            val (dw, dh) = nums("drawable")
            val mode = ScaleMode.entries.first { it.name.equals(case["mode"]!!.jsonPrimitive.content, ignoreCase = true) }
            val g = RenderGeometry.compute(sw, sh, dw, dh, mode)
            val name = case["name"]!!.jsonPrimitive.content
            assertEquals(name, nums("viewport"), listOf(g.viewport.x, g.viewport.y, g.viewport.width, g.viewport.height))
            nums("sourceRect").zip(listOf(g.sourceRect.x, g.sourceRect.y, g.sourceRect.width, g.sourceRect.height)).forEach { (want, got) ->
                assertEquals(name, want, got, 1e-9)
            }
        }
    }

    @Test fun maskLetterboxes() {
        val r = GuideSettings.maskRect(GuideSettings.Mask.Scope, Rect(0.0, 0.0, 1920.0, 1080.0))
        assertEquals(1920 / 2.39, r.height, 1e-9)
        assertEquals((1080 - 1920 / 2.39) / 2, r.y, 1e-9)
    }
}
