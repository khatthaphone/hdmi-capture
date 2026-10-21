package com.example.hdmicaptain.model

/**
 * A colour lookup table parsed from an Adobe/Resolve `.cube` file (port of CaptureCore's `CubeLUT`).
 * Always held as 3D (1D tables are baked into a 33³ grid), red varying fastest, as in the file format.
 * [entries] holds size³ RGB triples flattened: r0 g0 b0 r1 g1 b1 …
 */
class CubeLUT(
    val title: String,
    val size: Int,
    val domainMin: FloatArray,
    val domainMax: FloatArray,
    val entries: FloatArray,
) {
    class ParseException(message: String) : Exception(message)

    companion object {
        fun identity(size: Int = 17): CubeLUT {
            val out = FloatArray(size * size * size * 3)
            val scale = (size - 1).toFloat()
            var i = 0
            for (b in 0 until size) for (g in 0 until size) for (r in 0 until size) {
                out[i++] = r / scale; out[i++] = g / scale; out[i++] = b / scale
            }
            return CubeLUT("Identity", size, floatArrayOf(0f, 0f, 0f), floatArrayOf(1f, 1f, 1f), out)
        }

        fun parse(text: String, fallbackTitle: String = "LUT"): CubeLUT {
            var title = fallbackTitle
            var size3D: Int? = null
            var size1D: Int? = null
            var domainMin = floatArrayOf(0f, 0f, 0f)
            var domainMax = floatArrayOf(1f, 1f, 1f)
            val values = ArrayList<Float>()
            var count = 0

            text.lineSequence().forEachIndexed { index, raw ->
                val line = raw.trim()
                if (line.isEmpty() || line.startsWith("#")) return@forEachIndexed
                val parts = line.split(' ', '\t').filter { it.isNotEmpty() }
                fun bad(): Nothing = throw ParseException("Can't read line ${index + 1}.")
                when (parts[0].uppercase()) {
                    "TITLE" -> title = line.drop(5).trim().trim('"')
                    "LUT_3D_SIZE" -> size3D = parts.getOrNull(1)?.toIntOrNull()?.takeIf { parts.size == 2 } ?: bad()
                    "LUT_1D_SIZE" -> size1D = parts.getOrNull(1)?.toIntOrNull()?.takeIf { parts.size == 2 } ?: bad()
                    "DOMAIN_MIN" -> domainMin = triple(parts.drop(1)) ?: bad()
                    "DOMAIN_MAX" -> domainMax = triple(parts.drop(1)) ?: bad()
                    "LUT_3D_INPUT_RANGE", "LUT_1D_INPUT_RANGE" -> {
                        val lo = parts.getOrNull(1)?.toFloatOrNull()
                        val hi = parts.getOrNull(2)?.toFloatOrNull()
                        if (parts.size != 3 || lo == null || hi == null) bad()
                        domainMin = floatArrayOf(lo, lo, lo)
                        domainMax = floatArrayOf(hi, hi, hi)
                    }
                    else -> {
                        val v = triple(parts) ?: bad()
                        values += v[0]; values += v[1]; values += v[2]
                        count++
                    }
                }
            }

            size3D?.let { n ->
                if (n !in 2..65) throw ParseException("Unsupported LUT size $n.")
                if (count != n * n * n) throw ParseException("Expected ${n * n * n} entries, found $count.")
                return CubeLUT(title, n, domainMin, domainMax, values.toFloatArray())
            }
            size1D?.let { n ->
                if (n !in 2..65_536) throw ParseException("Unsupported LUT size $n.")
                if (count != n) throw ParseException("Expected $n entries, found $count.")
                return bake1D(values.toFloatArray(), title, domainMin, domainMax)
            }
            throw ParseException("Not a .cube LUT (no LUT_3D_SIZE or LUT_1D_SIZE).")
        }

        /** Applies a per-channel 1D curve on a 33³ grid. */
        private fun bake1D(curve: FloatArray, title: String, domainMin: FloatArray, domainMax: FloatArray): CubeLUT {
            val size = 33
            val n = curve.size / 3
            fun lookup(x: Float, channel: Int): Float {
                val position = x * (n - 1)
                val i0 = position.toInt()
                val i1 = minOf(i0 + 1, n - 1)
                val t = position - i0
                return curve[i0 * 3 + channel] * (1 - t) + curve[i1 * 3 + channel] * t
            }
            val out = FloatArray(size * size * size * 3)
            var i = 0
            val scale = (size - 1).toFloat()
            for (b in 0 until size) for (g in 0 until size) for (r in 0 until size) {
                out[i++] = lookup(r / scale, 0); out[i++] = lookup(g / scale, 1); out[i++] = lookup(b / scale, 2)
            }
            return CubeLUT(title, size, domainMin, domainMax, out)
        }

        private fun triple(parts: List<String>): FloatArray? {
            if (parts.size != 3) return null
            val numbers = parts.mapNotNull { it.toFloatOrNull() }
            return if (numbers.size == 3) numbers.toFloatArray() else null
        }
    }
}
