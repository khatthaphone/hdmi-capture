package com.example.hdmicaptain.viewer

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.getBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.capture.Card
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.model.AudioSettings
import com.example.hdmicaptain.model.GuideSettings
import com.example.hdmicaptain.model.TestPattern
import com.example.hdmicaptain.model.PauseReason
import com.example.hdmicaptain.model.RenderOptions
import com.example.hdmicaptain.model.ScaleMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** Every viewer state has a screen with the agreed words (spec §12.7, docs/copy.md). */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [36], qualifiers = "w360dp-h740dp")
class ViewerScreensTest {
    @get:Rule val compose = createComposeRule()

    private class Recorder : StatusActions, ControlBarActions {
        val calls = mutableListOf<String>()
        override fun continueFromWelcome() { calls += "welcome" }
        override fun openAppSettings() { calls += "settings" }
        override fun retry() { calls += "retry" }
        override fun showHelp() { calls += "help" }
        override fun selectInput(key: String) { calls += "input:$key" }
        override fun setMuted(muted: Boolean) { calls += "muted:$muted" }
        override fun setVolume(volume: Float) = Unit
        override fun screenshot() { calls += "screenshot" }
        override fun record() { calls += "record" }
        override fun saveRecent() { calls += "recent" }
        override fun setScale(mode: ScaleMode) { calls += "scale:$mode" }
        override fun setSharpPixels(on: Boolean) = Unit
        override fun setShowDiagnostics(on: Boolean) = Unit
        override fun setTestPattern(pattern: TestPattern?) = Unit
        override fun setGuides(guides: GuideSettings) = Unit
        override fun pictureInPicture() = Unit
        override fun openSettings() = Unit
        override fun poke() = Unit
    }

    private val actions = Recorder()

    private fun status(phase: ViewerPhase, title: String) {
        compose.setContent { HdmiCaptainTheme { StatusOverlay(phase, actions) } }
        compose.onNodeWithText(title).assertIsDisplayed()
    }

    @Test fun scanning() {
        status(ViewerPhase.Scanning, "Connect a capture card")
        compose.onNodeWithText("Connection Help…").performClick()
        assertEquals(listOf("help"), actions.calls)
    }

    @Test fun usbPermissionAskAndDenied() = status(ViewerPhase.UsbPermission("UGREEN-25854", denied = false), "Allow USB access")

    @Test fun usbPermissionDeniedOffersTryAgain() {
        status(ViewerPhase.UsbPermission("UGREEN-25854", denied = true), "USB access needed")
        compose.onNodeWithText("Try Again").performClick()
        assertEquals(listOf("retry"), actions.calls)
    }

    @Test fun waiting() = status(ViewerPhase.Waiting("UGREEN-25854"), "UGREEN-25854 is disconnected")

    @Test fun connecting() = status(ViewerPhase.Connecting("UGREEN-25854"), "Connecting to UGREEN-25854…")

    @Test fun connectingSlowOffersReconnect() {
        status(ViewerPhase.Connecting("UGREEN-25854", slow = true), "This is taking longer than usual.")
        compose.onNodeWithText("Reconnect").assertIsDisplayed()
    }

    @Test fun noSignal() = status(ViewerPhase.Recovering("No signal"), "No HDMI signal")

    @Test fun noFramesExplainsPrivacySwitchAndPower() = status(ViewerPhase.Recovering("No frames"), "No picture from the capture card")

    @Test fun reconnecting() = status(ViewerPhase.Recovering("LIBUSB_ERROR_IO"), "Reconnecting…")

    @Test fun pausedInBackground() = status(ViewerPhase.Paused(PauseReason.Background), "HDMI Captain is in the background")

    @Test fun failed() {
        status(ViewerPhase.Failed("uvc_start_streaming: I/O"), "The capture card stopped responding")
        compose.onNodeWithText("Reconnect").performClick()
        assertEquals(listOf("retry"), actions.calls)
    }

    @Test fun welcomeThenDenied() {
        compose.setContent { HdmiCaptainTheme { PermissionScreen(PermissionPhase.Welcome, actions) } }
        compose.onNodeWithText("Welcome to HDMI Captain").assertIsDisplayed()
        compose.onNodeWithText("Continue").performClick()
        assertEquals(listOf("welcome"), actions.calls)
    }

    @Test fun permissionDenied() {
        compose.setContent { HdmiCaptainTheme { PermissionScreen(PermissionPhase.Denied, actions) } }
        compose.onNodeWithText("Camera access required").assertIsDisplayed()
        compose.onNodeWithText("Open Settings").performClick()
        assertEquals(listOf("settings"), actions.calls)
    }

    @Test fun hdcpBlackPicture() {
        compose.setContent { HdmiCaptainTheme { NoSignalOverlay(black = true, actions = actions) } }
        compose.onNodeWithText("No HDMI signal").assertIsDisplayed()
    }

    @Test fun controlBarFitsA360dpPhone() {
        compose.setContent {
            HdmiCaptainTheme {
                Box(Modifier.width(360.dp)) {
                    ControlBar(emptyList<Card>(), null, RenderOptions(), AudioSettings(), false, null, GuideSettings(), isPro = false, isRecording = false, actions = actions)
                }
            }
        }
        val bar = compose.onNodeWithTag("control-bar").getBoundsInRoot()
        assertTrue("bar ${bar.right} wide", bar.right <= 360.dp)
        compose.onNodeWithContentDescription("More").assertIsDisplayed()
        compose.onNodeWithContentDescription("Record").performClick()
        compose.onNodeWithContentDescription("Screenshot").performClick()
        assertEquals(listOf("record", "screenshot"), actions.calls)
    }

    @Test fun controlBarShowsVolumeSliderWhenThereIsRoom() {
        compose.setContent {
            HdmiCaptainTheme {
                Box(Modifier.width(700.dp)) {
                    ControlBar(emptyList<Card>(), null, RenderOptions(), AudioSettings(), false, null, GuideSettings(), isPro = true, isRecording = false, actions = actions)
                }
            }
        }
        compose.onNodeWithTag("volume").assertIsDisplayed()
    }
}
