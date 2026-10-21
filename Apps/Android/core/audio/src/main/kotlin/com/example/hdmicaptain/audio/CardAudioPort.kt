package com.example.hdmicaptain.audio

import android.content.Context
import android.util.Log
import com.example.hdmicaptain.model.AudioSettings

/** Starts card audio for the card the controller just opened, and stops it on close. */
class CardAudioPort(private val context: Context) {
    private var audio: CardAudio? = null
    var settings = AudioSettings()
        set(value) {
            field = value
            audio?.apply(value)
        }

    val levels: CardAudio.Levels? get() = audio?.levels()

    fun start(cardName: String) {
        stop()
        val cardAudio = CardAudio(context)
        val input = cardAudio.findInput(cardName)
        if (input == null) {
            Log.w(TAG, "no USB audio input for $cardName")
            cardAudio.release()
            return
        }
        try {
            cardAudio.start(input)
            cardAudio.apply(settings)
            audio = cardAudio
            Log.i(TAG, "audio from ${input.productName} (id ${input.id}, rates ${input.sampleRates.toList()}, channels ${input.channelCounts.toList()})")
        } catch (e: Exception) {
            Log.w(TAG, "audio failed", e)
            cardAudio.release()
        }
    }

    fun stop() {
        audio?.stop()
        audio?.release()
        audio = null
    }

    private companion object {
        const val TAG = "HDMICaptain"
    }
}
