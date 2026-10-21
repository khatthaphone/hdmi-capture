package com.example.hdmicaptain.capture

import com.example.hdmicaptain.model.FormatChoice
import com.example.hdmicaptain.model.FormatDescriptor
import com.example.hdmicaptain.model.FrameRateRange
import com.example.hdmicaptain.model.PauseReason
import com.example.hdmicaptain.model.UsbLink
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class CaptureControllerTest {
    private data class FakeCard(override val key: String, override val name: String) : Card

    private class FakeCatalog : CardCatalog {
        override val cards = MutableStateFlow<List<Card>>(emptyList())
        var granted = true
        val permitted = mutableSetOf<String>()
        override fun start() = Unit
        override fun stop() = Unit
        override fun hasPermission(card: Card) = card.key in permitted
        override suspend fun requestPermission(card: Card): Boolean = granted.also { if (it) permitted += card.key }
    }

    /** Frames arrive while [streaming]; [failOpens] makes the next opens throw. */
    private class FakeDriver(private val clock: () -> Long) : CaptureDriver {
        var streaming = true
        var failOpens = 0
        var opens = 0
        var closes = 0
        var started: FormatChoice? = null
        private var frames = 0L
        private var last = 0L
        val formats = listOf(
            FormatDescriptor(1920, 1080, "YUY2", listOf(60.0, 30.0, 5.0).map { FrameRateRange(it, it) }),
            FormatDescriptor(1920, 1080, "MJPG", listOf(60.0, 30.0).map { FrameRateRange(it, it) }),
        )

        override fun open(card: Card): CaptureDriver.Opened {
            opens++
            if (failOpens > 0) {
                failOpens--
                throw IllegalStateException("LIBUSB_ERROR_IO")
            }
            return CaptureDriver.Opened(formats, UsbLink.Usb2, "usb2")
        }

        override fun start(choice: FormatChoice) {
            started = choice
        }

        override fun progress(): CaptureDriver.Progress {
            if (streaming && started != null) {
                frames++
                last = clock()
            }
            return CaptureDriver.Progress(frames, last)
        }

        override fun close() {
            closes++
            started = null
        }
    }

    private class FakeAudio : AudioPort {
        var running = false
        override fun start(card: Card) {
            running = true
        }

        override fun stop() {
            running = false
        }
    }

    private val dispatcher = StandardTestDispatcher()
    private val scope = TestScope(dispatcher)
    private val catalog = FakeCatalog()
    private val driver = FakeDriver { dispatcher.scheduler.currentTime * 1_000_000 }
    private val audio = FakeAudio()
    private val controller = CaptureController(scope, dispatcher, catalog, driver, audio, nanoTime = { dispatcher.scheduler.currentTime * 1_000_000 })
    private val ugreen = FakeCard("usb:1DE1:F105|name:UGREEN-25854", "UGREEN-25854")
    private val ms2131 = FakeCard("usb:345F:2131|name:UGREEN HDMI Capture", "UGREEN HDMI Capture")

    private fun advance(ms: Long) {
        scope.advanceTimeBy(ms)
        scope.runCurrent()
    }

    private fun plug(vararg cards: Card) {
        catalog.cards.value = cards.toList()
        advance(1)
    }

    @Test fun scanningUntilACardArrivesThenLiveWithBandwidthAwareFormat() {
        controller.start()
        advance(1)
        assertEquals(ViewerPhase.Scanning, controller.phase.value)
        plug(ugreen)
        advance(300)
        val live = controller.phase.value as ViewerPhase.Live
        // USB 2.0: 1080p60 MJPEG beats YUY2 capped at 5 fps.
        assertEquals("MJPG", live.info.fourCC)
        assertEquals(60.0, live.info.frameRate, 0.0)
        assertTrue(audio.running)
        assertEquals(ugreen.key, controller.preferredKey)
    }

    @Test fun usbPermissionDeniedShowsTheAskAgain() {
        catalog.granted = false
        controller.start()
        plug(ugreen)
        assertEquals(ViewerPhase.UsbPermission("UGREEN-25854", denied = true), controller.phase.value)
        catalog.granted = true
        controller.retry()
        advance(300)
        assertTrue(controller.phase.value is ViewerPhase.Live)
    }

    @Test fun openErrorsRetryWithBackoff() {
        driver.failOpens = 2
        controller.start()
        plug(ugreen)
        assertEquals(ViewerPhase.Recovering("LIBUSB_ERROR_IO"), controller.phase.value)
        advance(250)  // first retry after 0.25 s fails again
        assertEquals(2, driver.opens)
        advance(500)  // second after 0.5 s succeeds
        advance(300)
        assertEquals(3, driver.opens)
        assertTrue(controller.phase.value is ViewerPhase.Live)
    }

    @Test fun silenceWhileLiveRestartsAsNoSignal() {
        controller.start()
        plug(ugreen)
        advance(300)
        driver.streaming = false
        advance(3_300)
        assertEquals(ViewerPhase.Recovering("No signal"), controller.phase.value)
        driver.streaming = true
        advance(1_000)
        assertTrue(controller.phase.value is ViewerPhase.Live)
    }

    @Test fun noFramesAfterOpenIsReported() {
        driver.streaming = false
        controller.start()
        plug(ugreen)
        advance(5_300)
        assertEquals(ViewerPhase.Recovering("No frames"), controller.phase.value)
    }

    @Test fun unplugWaitsAndReplugResumes() {
        controller.start()
        plug(ugreen)
        advance(300)
        plug()
        assertEquals(ViewerPhase.Waiting("UGREEN-25854"), controller.phase.value)
        assertTrue(!audio.running)
        plug(ugreen)
        advance(300)
        assertTrue(controller.phase.value is ViewerPhase.Live)
    }

    @Test fun unplugWithAnotherCardSwitchesToIt() {
        controller.start()
        plug(ms2131, ugreen)
        advance(300)
        assertEquals("UGREEN-25854", (controller.phase.value as ViewerPhase.Live).info.deviceName)
        plug(ms2131)
        advance(300)
        assertEquals("UGREEN HDMI Capture", (controller.phase.value as ViewerPhase.Live).info.deviceName)
    }

    @Test fun preferredCardWinsOverMostRecent() {
        controller.preferredKey = ms2131.key
        controller.start()
        plug(ms2131, ugreen)
        advance(300)
        assertEquals("UGREEN HDMI Capture", (controller.phase.value as ViewerPhase.Live).info.deviceName)
    }

    @Test fun hiddenPausesAndVisibleResumes() {
        controller.start()
        plug(ugreen)
        advance(300)
        val closesBefore = driver.closes
        controller.onHidden()
        advance(1)
        assertEquals(ViewerPhase.Paused(PauseReason.Background), controller.phase.value)
        assertEquals(closesBefore + 1, driver.closes)
        advance(10_000)  // no restarts while hidden
        assertEquals(ViewerPhase.Paused(PauseReason.Background), controller.phase.value)
        controller.onVisible()
        advance(300)
        assertTrue(controller.phase.value is ViewerPhase.Live)
    }
}
