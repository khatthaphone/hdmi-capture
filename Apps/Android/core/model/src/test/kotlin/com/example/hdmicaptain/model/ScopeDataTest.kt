package com.example.hdmicaptain.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ScopeDataTest {
    private fun grid(columns: Int, rows: Int, ycc: (Int, Int) -> Triple<Int, Int, Int>): ByteArray {
        val out = ByteArray(columns * rows * 3)
        var i = 0
        for (r in 0 until rows) for (c in 0 until columns) {
            val (y, cb, cr) = ycc(c, r)
            out[i++] = y.toByte(); out[i++] = cb.toByte(); out[i++] = cr.toByte()
        }
        return out
    }

    @Test fun flatGrayFillsOneHistogramBinAndTheVectorscopeCentre() {
        val data = ScopeData.analyze(grid(320, 180) { _, _ -> Triple(126, 128, 128) }, 320, 180)
        assertEquals(320 * 180, data.histogram[126])
        assertEquals(320 * 180, data.histogram.sum())
        val centre = (127 * ScopeData.VECTOR_SIZE / 256) * ScopeData.VECTOR_SIZE + 128 * ScopeData.VECTOR_SIZE / 256
        assertEquals(320 * 180, data.vectorscope[centre])
    }

    @Test fun rampSpreadsAcrossTheWaveform() {
        val data = ScopeData.analyze(grid(256, 10) { c, _ -> Triple(c, 128, 128) }, 256, 10)
        // Column c holds luma c: the trace is a diagonal.
        for (c in listOf(0, 100, 255)) assertEquals(10, data.waveform[c * ScopeData.WAVEFORM_COLUMNS + c])
    }

    @Test fun barTargetsAreInsideTheScope() {
        assertEquals(6, ScopeData.barTargets.size)
        ScopeData.barTargets.forEach { (_, p) -> assertTrue(p.first in 0.0..1.0 && p.second in 0.0..1.0) }
    }

    @Test fun seventyFivePercentYellowLandsOnItsTarget() {
        // 75 % yellow, BT.709 limited (TestPattern.ycbcr): Y 168, Cb 44, Cr 136.
        val data = ScopeData.analyze(grid(4, 4) { _, _ -> Triple(168, 44, 136) }, 4, 4)
        val index = data.vectorscope.indexOfFirst { it > 0 }
        val x = (index % ScopeData.VECTOR_SIZE + 0.5) / ScopeData.VECTOR_SIZE
        val y = (index / ScopeData.VECTOR_SIZE + 0.5) / ScopeData.VECTOR_SIZE
        val (tx, ty) = ScopeData.barTargets.first { it.first == "Yl" }.second
        assertEquals(tx, x, 0.01)
        assertEquals(ty, y, 0.01)
    }

    @Test fun imagesHaveTheRightSize() {
        val data = ScopeData.analyze(grid(16, 9) { c, r -> Triple(16 + c * 10, 128 + r, 128 - r) }, 16, 9)
        assertEquals(256 * 256, data.waveformPixels().size)
        assertEquals(128 * 128, data.vectorscopePixels().size)
        assertEquals(256 * 100, data.histogramPixels().size)
    }
}
