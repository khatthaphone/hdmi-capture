package com.example.hdmicaptain.model

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class CubeLUTTest {
    @Test fun parses3D() {
        val text = """
            # comment
            TITLE "Tiny"
            LUT_3D_SIZE 2
            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
        """.trimIndent()
        val lut = CubeLUT.parse(text)
        assertEquals("Tiny", lut.title)
        assertEquals(2, lut.size)
        assertEquals(24, lut.entries.size)
        assertEquals(CubeLUT.identity(2).entries.toList(), lut.entries.toList())
    }

    @Test fun bakes1DInto33Cube() {
        val lut = CubeLUT.parse("LUT_1D_SIZE 2\n0 0 0\n1 1 1\n")
        assertEquals(33, lut.size)
        assertEquals(1f, lut.entries[(32 * 3)], 1e-6f) // r = 1 at the first row's last entry
    }

    @Test fun errors() {
        assertThrows(CubeLUT.ParseException::class.java) { CubeLUT.parse("0 0 0\n") }
        assertThrows(CubeLUT.ParseException::class.java) { CubeLUT.parse("LUT_3D_SIZE 2\n0 0 0\n") }
        assertThrows(CubeLUT.ParseException::class.java) { CubeLUT.parse("LUT_3D_SIZE 99\n") }
        assertThrows(CubeLUT.ParseException::class.java) { CubeLUT.parse("LUT_3D_SIZE 2\nfoo bar\n") }
    }

    @Test fun deviceKey() {
        assertEquals("usb:1DE1:F105|name:UGREEN-25854", DeviceKey.of(0x1DE1, 0xF105, null, "UGREEN-25854"))
        assertEquals("usb:345F:2131|serial:20230424", DeviceKey.of(0x345F, 0x2131, "20230424", "UGREEN HDMI Capture"))
    }
}
