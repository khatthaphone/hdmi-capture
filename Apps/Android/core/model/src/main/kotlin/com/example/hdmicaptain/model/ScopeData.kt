package com.example.hdmicaptain.model

import kotlin.math.ln
import kotlin.math.min
import kotlin.math.sqrt

/**
 * Luma histogram, luma waveform and CbCr vectorscope (port of CaptureCore's ScopeData) from a
 * sample grid of interleaved Y, Cb, Cr bytes, as the native renderer provides it.
 */
class ScopeData(
    /** Counts per luma code value (0…255). */
    val histogram: IntArray,
    /** [WAVEFORM_COLUMNS] columns × 256 rows of counts (row = luma code value). */
    val waveform: IntArray,
    /** [VECTOR_SIZE]² counts over (Cb, Cr), y down = high Cr at the top. */
    val vectorscope: IntArray,
) {
    companion object {
        const val LEVELS = 256
        const val WAVEFORM_COLUMNS = 256
        const val VECTOR_SIZE = 128

        fun analyze(grid: ByteArray, columns: Int, rows: Int): ScopeData {
            val histogram = IntArray(LEVELS)
            val waveform = IntArray(WAVEFORM_COLUMNS * LEVELS)
            val vectorscope = IntArray(VECTOR_SIZE * VECTOR_SIZE)
            var i = 0
            for (row in 0 until rows) {
                for (col in 0 until columns) {
                    val y = grid[i].toInt() and 0xFF
                    val cb = grid[i + 1].toInt() and 0xFF
                    val cr = grid[i + 2].toInt() and 0xFF
                    i += 3
                    histogram[y]++
                    waveform[y * WAVEFORM_COLUMNS + col * WAVEFORM_COLUMNS / columns]++
                    vectorscope[((255 - cr) * VECTOR_SIZE / 256) * VECTOR_SIZE + cb * VECTOR_SIZE / 256]++
                }
            }
            return ScopeData(histogram, waveform, vectorscope)
        }

        /** Where 75 % colour bars land on the vectorscope (normalised 0…1, y down), for target boxes. */
        val barTargets: List<Pair<String, Pair<Double, Double>>> by lazy {
            val bars = listOf("Yl" to Triple(1.0, 1.0, 0.0), "Cy" to Triple(0.0, 1.0, 1.0), "G" to Triple(0.0, 1.0, 0.0),
                "Mg" to Triple(1.0, 0.0, 1.0), "R" to Triple(1.0, 0.0, 0.0), "B" to Triple(0.0, 0.0, 1.0))
            bars.map { (name, rgb) ->
                val (r, g, b) = rgb
                val luma = 0.2126 * r * 0.75 + 0.7152 * g * 0.75 + 0.0722 * b * 0.75
                val cb = Math.round(128 + 224 * ((b * 0.75 - luma) / 1.8556)).toDouble()
                val cr = Math.round(128 + 224 * ((r * 0.75 - luma) / 1.5748)).toDouble()
                name to (cb / 256 to (255 - cr) / 256)
            }
        }
    }

    /** Waveform as ARGB pixels, 256 × 256, top row = luma 255. */
    fun waveformPixels(): IntArray = pixels(WAVEFORM_COLUMNS, LEVELS, waveform, flipRows = true, tint = 0xFF59FF73.toInt())

    fun vectorscopePixels(): IntArray = pixels(VECTOR_SIZE, VECTOR_SIZE, vectorscope, flipRows = false, tint = 0xFF73FF8C.toInt())

    /** Histogram bars, 256 × [height] (square-root scale, as on Apple). */
    fun histogramPixels(height: Int = 100): IntArray {
        val peak = (histogram.maxOrNull() ?: 1).coerceAtLeast(1).toDouble()
        val counts = IntArray(LEVELS * height)
        histogram.forEachIndexed { level, count ->
            val bar = (sqrt(count / peak) * height).toInt()
            for (row in 0 until bar) counts[(height - 1 - row) * LEVELS + level] = 1
        }
        return pixels(LEVELS, height, counts, flipRows = false, tint = 0xFFD9D9D9.toInt(), binary = true)
    }

    private fun pixels(width: Int, height: Int, counts: IntArray, flipRows: Boolean, tint: Int, binary: Boolean = false): IntArray {
        val peak = (counts.maxOrNull() ?: 1).coerceAtLeast(1).toDouble()
        val out = IntArray(width * height)
        val tr = (tint shr 16) and 0xFF
        val tg = (tint shr 8) and 0xFF
        val tb = tint and 0xFF
        for (row in 0 until height) {
            val source = if (flipRows) height - 1 - row else row
            for (col in 0 until width) {
                val count = counts[source * width + col]
                if (count == 0) continue
                // Log scale so faint traces stay visible next to dense ones.
                val v = if (binary) 1.0 else min(1.0, 0.25 + 0.75 * ln(1.0 + count) / ln(1.0 + peak))
                out[row * width + col] = (0xFF shl 24) or ((tr * v).toInt() shl 16) or ((tg * v).toInt() shl 8) or (tb * v).toInt()
            }
        }
        return out
    }
}
