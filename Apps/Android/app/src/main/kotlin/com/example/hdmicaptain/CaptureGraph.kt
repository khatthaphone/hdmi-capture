package com.example.hdmicaptain

import android.content.Context
import com.example.hdmicaptain.audio.CardAudio
import com.example.hdmicaptain.audio.CardAudioPort
import com.example.hdmicaptain.capture.AndroidCaptureDriver
import com.example.hdmicaptain.capture.AudioPort
import com.example.hdmicaptain.capture.Camera2Card
import com.example.hdmicaptain.capture.Card
import com.example.hdmicaptain.capture.CaptureController
import com.example.hdmicaptain.capture.UsbCardCatalog
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import java.io.File

/** Wires renderer, card catalog, capture driver, audio and controller for one viewer. */
@OptIn(ExperimentalCoroutinesApi::class)
class CaptureGraph(private val context: Context, scope: CoroutineScope) {
    val renderer = VideoRenderer()
    val catalog = UsbCardCatalog(context)
    val audio = CardAudioPort(context)
    private val driver = AndroidCaptureDriver(context, catalog, renderer::ringHandle) { json ->
        if (BuildConfig.DEBUG) File(context.filesDir, "qa-descriptors.json").writeText(json)
    }

    val controller = CaptureController(
        scope = scope,
        // USB, libuvc and Oboe calls block: one dedicated thread, like CaptureEngine's session queue.
        control = Dispatchers.IO.limitedParallelism(1),
        catalog = catalog,
        driver = driver,
        audio = object : AudioPort {
            override fun start(card: Card) {
                if (card !is Camera2Card) audio.start(card.name)
            }

            override fun stop() = audio.stop()
        },
    )

    val audioLevels: CardAudio.Levels? get() = audio.levels

    fun release() {
        controller.stop()
        audio.stop()
        renderer.release()
    }
}
