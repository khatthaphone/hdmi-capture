package com.example.hdmicaptain.model

import com.example.hdmicaptain.model.Fixtures.dbl
import com.example.hdmicaptain.model.Fixtures.str
import kotlin.math.abs
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SignalAnalyzerTest {
    @Test fun sharedClassifyFixtures() {
        for (case in Fixtures.load("signal-classify.json")["cases"]!!.jsonArray.map { it.jsonObject }) {
            val name = case.str("name")
            val s = case["stats"]!!.jsonObject
            val stats = SignalAnalyzer.Stats(
                meanY = s.dbl("meanY"), meanCb = s.dbl("meanCb"), meanCr = s.dbl("meanCr"),
                rangeY = s.dbl("rangeY"), rangeChroma = s.dbl("rangeChroma"),
            )
            val got = SignalAnalyzer.classify(stats, case["fullRange"]!!.jsonPrimitive.boolean)
            when (val want = Fixtures.content(case["expect"]!!)) {
                is SignalContent.Solid -> {
                    assertTrue("$name: $got", got is SignalContent.Solid)
                    got as SignalContent.Solid
                    assertTrue("$name: $got", abs(got.red - want.red) <= 1 && abs(got.green - want.green) <= 1 && abs(got.blue - want.blue) <= 1)
                }
                else -> assertEquals(name, want, got)
            }
        }
    }

    @Test fun sharedMonitorFixtures() {
        for (case in Fixtures.load("signal-monitor.json")["cases"]!!.jsonArray.map { it.jsonObject }) {
            val debouncer = SignalDebouncer(case.dbl("persistence"))
            for (step in case["steps"]!!.jsonArray.map { it.jsonObject }) {
                debouncer.observe(Fixtures.content(step["content"]!!), step.dbl("t"))
                assertEquals("${case.str("name")} @ ${step.dbl("t")}", Fixtures.content(step["expect"]!!).isNoSignal, debouncer.current.isNoSignal)
            }
        }
    }

    @Test fun statsFromSamples() {
        val flat = IntArray(100) { 16 }
        val neutral = IntArray(100) { 128 }
        val stats = SignalAnalyzer.stats(flat, neutral, neutral)
        assertEquals(16.0, stats.meanY, 1e-9)
        assertEquals(0.0, stats.rangeY, 1e-9)
        assertEquals(SignalContent.Black, SignalAnalyzer.classify(stats, fullRange = false))
        val text = IntArray(100) { if (it % 10 == 0) 200 else 16 }
        assertEquals(SignalContent.Picture, SignalAnalyzer.classify(SignalAnalyzer.stats(text, neutral, neutral), fullRange = false))
    }
}
