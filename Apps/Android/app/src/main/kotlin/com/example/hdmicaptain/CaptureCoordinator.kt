package com.example.hdmicaptain

import android.content.Context
import android.util.Log
import com.example.hdmicaptain.capture.UsbCard
import com.example.hdmicaptain.capture.UsbCardCatalog
import com.example.hdmicaptain.capture.UvcSource
import com.example.hdmicaptain.model.FormatSelector
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Spike-level capture flow (task 072): first UVC card → USB permission → open → pick a format
 * (FormatSelector + link speed) → stream into the renderer. Unplug closes; replug reopens.
 * Task 077 replaces this with CaptureController + RecoveryMachine.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class CaptureCoordinator(
    private val context: Context,
    private val scope: CoroutineScope,
    private val renderer: VideoRenderer,
) {
    /** One line for the status card / diagnostics. */
    private val _status = MutableStateFlow("Connect a capture card")
    val status: StateFlow<String> = _status.asStateFlow()
    private val _live = MutableStateFlow(false)
    val live: StateFlow<Boolean> = _live.asStateFlow()

    val catalog = UsbCardCatalog(context)
    /** USB and libuvc calls block; they run here, one at a time. */
    private val control = Dispatchers.IO.limitedParallelism(1)
    private var source: UvcSource? = null
    private var current: UsbCard? = null

    fun start() {
        catalog.start()
        scope.launch {
            catalog.cards.collect { cards ->
                val card = cards.lastOrNull()
                if (card?.device?.deviceName != current?.device?.deviceName) switchTo(card)
            }
        }
    }

    fun stop() {
        catalog.stop()
        scope.launch(control) { closeSource() }
    }

    val uvcStats: UvcSource.Stats? get() = source?.stats()

    private suspend fun switchTo(card: UsbCard?) {
        withContext(control) { closeSource() }
        current = card
        if (card == null) {
            _status.value = "Connect a capture card"
            return
        }
        _status.value = "Connecting to ${card.name}…"
        if (!catalog.requestPermission(card)) {
            _status.value = "USB access to ${card.name} was not allowed"
            return
        }
        withContext(control) { openAndStream(card) }
    }

    private fun openAndStream(card: UsbCard) {
        val connection = catalog.open(card) ?: run {
            _status.value = "Couldn't open ${card.name}"
            return
        }
        val uvc = UvcSource()
        try {
            uvc.open(connection)
            val description = uvc.describe()
            File(context.filesDir, "qa-descriptors.json").writeText(description.json)
            Log.i(TAG, "${card.name} ${card.key} link=${description.speed} formats=${description.formats.size}")
            val choice = FormatSelector.select(description.formats, link = description.link)
                ?: throw UvcSource.UvcException("no usable format")
            uvc.start(choice, renderer.ringHandle)
            source = uvc
            _live.value = true
            _status.value = "${card.name} · ${choice.format.width}×${choice.format.height} ${choice.format.fourCC} @ ${choice.frameRate} · ${description.speed}"
            Log.i(TAG, "streaming: ${_status.value}")
        } catch (e: Exception) {
            Log.w(TAG, "open failed", e)
            uvc.release()
            _status.value = "${card.name}: ${e.message}"
        }
    }

    private fun closeSource() {
        source?.release()
        source = null
        _live.value = false
    }

    private companion object {
        const val TAG = "HDMICaptain"
    }
}
