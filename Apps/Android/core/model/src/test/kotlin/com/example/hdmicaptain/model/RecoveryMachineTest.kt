package com.example.hdmicaptain.model

import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class RecoveryMachineTest {
    private val card = CaptureEvent.Start("usb:1DE1:F105|name:UGREEN-25854", "UGREEN-25854")
    private val info = LiveInfo("UGREEN-25854", 1920, 1080, 60.0, "NV12")

    @Test fun sharedBackoffFixture() {
        for (pair in Fixtures.load("recovery-backoff.json")["delays"]!!.jsonArray) {
            val (attempt, delay) = pair.jsonArray.let { it[0].jsonPrimitive.int to it[1].jsonPrimitive.double }
            assertEquals("attempt $attempt", delay, restartDelaySeconds(attempt), 1e-9)
        }
    }

    @Test fun startOpensAndFirstFrameGoesLive() {
        val m = RecoveryMachine()
        assertEquals(CaptureAction.Open, m.handle(card))
        assertEquals(CaptureState.Connecting("UGREEN-25854"), m.state)
        m.handle(CaptureEvent.FirstFrame(info))
        assertEquals(CaptureState.Live(info), m.state)
    }

    @Test fun errorsBackOffAndResetWhenLive() {
        val m = RecoveryMachine()
        m.handle(card)
        val delays = (1..6).map { (m.handle(CaptureEvent.StreamError("I/O error")) as CaptureAction.RetryAfter).delaySeconds }
        assertEquals(listOf(0.25, 0.5, 1.0, 2.0, 4.0, 4.0), delays)
        assertEquals(CaptureState.Recovering("I/O error"), m.state)
        m.handle(CaptureEvent.FirstFrame(info))
        assertEquals(0.25, (m.handle(CaptureEvent.Stalled) as CaptureAction.RetryAfter).delaySeconds, 1e-9)
        assertEquals(CaptureState.Recovering(RecoveryReason.NO_SIGNAL), m.state)
    }

    @Test fun staleRetryIsIgnored() {
        val m = RecoveryMachine()
        m.handle(card)
        val first = m.handle(CaptureEvent.StreamError("a")) as CaptureAction.RetryAfter
        val second = m.handle(CaptureEvent.StreamError("b")) as CaptureAction.RetryAfter
        assertEquals(CaptureAction.None, m.handle(CaptureEvent.RetryDue(first.generation)))
        assertEquals(CaptureAction.Open, m.handle(CaptureEvent.RetryDue(second.generation)))
    }

    @Test fun unplugWaitsForTheSameCard() {
        val m = RecoveryMachine()
        m.handle(card)
        m.handle(CaptureEvent.FirstFrame(info))
        assertEquals(CaptureAction.None, m.handle(CaptureEvent.Detached("usb:345F:2131|name:other")))
        assertEquals(CaptureAction.Close, m.handle(CaptureEvent.Detached(card.deviceKey)))
        assertEquals(CaptureState.Recovering(RecoveryReason.DISCONNECTED), m.state)
        assertEquals(CaptureAction.None, m.handle(CaptureEvent.Attached("usb:345F:2131|name:other")))
        assertEquals(CaptureAction.Open, m.handle(CaptureEvent.Attached(card.deviceKey)))
        assertEquals(CaptureState.Connecting("UGREEN-25854"), m.state)
    }

    @Test fun interruptionPausesWithoutRetrying() {
        val m = RecoveryMachine()
        m.handle(card)
        m.handle(CaptureEvent.FirstFrame(info))
        assertEquals(CaptureAction.Close, m.handle(CaptureEvent.Interrupted(PauseReason.Background)))
        assertEquals(CaptureState.Paused(PauseReason.Background), m.state)
        assertEquals(CaptureAction.None, m.handle(CaptureEvent.StreamError("late error")))
        assertEquals(CaptureAction.None, m.handle(CaptureEvent.Attached(card.deviceKey)))
        assertEquals(CaptureState.Paused(PauseReason.Background), m.state)
        assertEquals(CaptureAction.Open, m.handle(CaptureEvent.InterruptionEnded))
        assertEquals(CaptureState.Connecting("UGREEN-25854"), m.state)
    }

    @Test fun stopAndFatal() {
        val m = RecoveryMachine()
        m.handle(card)
        assertEquals(CaptureAction.Close, m.handle(CaptureEvent.Fatal("USB permission denied")))
        assertEquals(CaptureState.Failed("USB permission denied"), m.state)
        assertEquals(CaptureAction.Open, m.handle(card))
        assertEquals(CaptureAction.Close, m.handle(CaptureEvent.Stop))
        assertEquals(CaptureState.Idle, m.state)
        assertTrue(m.handle(CaptureEvent.StreamError("after stop")) is CaptureAction.None)
    }
}
