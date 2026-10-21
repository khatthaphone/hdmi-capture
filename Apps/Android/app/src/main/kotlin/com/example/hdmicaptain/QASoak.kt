package com.example.hdmicaptain

import android.content.Context
import android.os.Debug
import android.os.PowerManager
import com.example.hdmicaptain.render.VideoRenderer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.File

/**
 * DEBUG `--ei qaSoakMinutes 30`: one line a minute to files/qa-soak.csv, the same columns as the
 * Apple QASoak (plus Android's thermal headroom and render latency). Pull with `make android-soak`.
 */
class QASoak(
    private val context: Context,
    private val renderer: VideoRenderer,
    private val capture: CaptureCoordinator,
) {
    fun start(scope: CoroutineScope, minutes: Int): Job {
        val csv = File(context.filesDir, "qa-soak.csv")
        csv.writeText("minute,state,device,mode,in_fps,shown_fps,dropped_total,dropped_per_min,memory_mb,thermal,thermal_headroom,audio_buffered_ms,audio_underruns,latency_ms\n")
        val power = context.getSystemService(PowerManager::class.java)
        return scope.launch {
            var last = renderer.stats()
            var lastDropped = 0L
            for (minute in 1..minutes) {
                delay(60_000)
                val now = renderer.stats()
                val inFps = (now.framesIn - last.framesIn) / 60.0
                val shownFps = (now.framesDrawn - last.framesDrawn) / 60.0
                val uvc = capture.uvcStats
                // Frames the card sent that never reached the screen: short USB frames + decoder drops/failures.
                val dropped = (uvc?.shortFrames ?: 0) + (uvc?.decodeDropped ?: 0) + (uvc?.decodeFailed ?: 0)
                val memory = Debug.MemoryInfo().also { Debug.getMemoryInfo(it) }.totalPss / 1024.0
                val headroom = if (android.os.Build.VERSION.SDK_INT >= 30) power.getThermalHeadroom(10) else Float.NaN
                val audio = capture.audioLevels
                val line = listOf(
                    minute, if (capture.live.value) "live" else "not-live", "\"${capture.status.value.substringBefore(" · ")}\"",
                    "${now.width}x${now.height}", "%.2f".format(inFps), "%.2f".format(shownFps), dropped, dropped - lastDropped,
                    "%.1f".format(memory), power.currentThermalStatus, "%.2f".format(headroom),
                    "%.1f".format(audio?.bufferedMs ?: 0f), audio?.underruns ?: 0, "%.1f".format(now.latencyNanos / 1e6),
                ).joinToString(",")
                csv.appendText(line + "\n")
                last = now
                lastDropped = dropped
            }
        }
    }
}
