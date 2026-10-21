package com.example.hdmicaptain.record

import kotlin.concurrent.thread

/**
 * One native audio tap, several encoders (recording + Save Recent): a reader thread drains the
 * tap and every consumer gets its own copy, so neither steals the other's samples.
 */
class AudioFanOut(private val source: (FloatArray) -> Int) {
    private class Queue {
        val chunks = ArrayDeque<FloatArray>()
        var frames = 0
    }

    private val queues = mutableListOf<Queue>()
    @Volatile private var running = false
    private var reader: Thread? = null

    /** A reader for one encoder (same contract as the tap: fills interleaved stereo, returns frames). */
    fun open(): (FloatArray) -> Int {
        val queue = Queue()
        synchronized(queues) {
            queues += queue
            if (!running) start()
        }
        return { out -> take(queue, out) }
    }

    fun close(reader: (FloatArray) -> Int) = Unit  // queues are dropped with stop()

    fun stop() {
        running = false
        reader?.join(500)
        reader = null
        synchronized(queues) { queues.clear() }
    }

    private fun start() {
        running = true
        reader = thread(name = "audio-fanout") {
            val buffer = FloatArray(4096 * 2)
            while (running) {
                val got = source(buffer)
                if (got == 0) {
                    Thread.sleep(5)
                    continue
                }
                val chunk = buffer.copyOf(got * 2)
                synchronized(queues) {
                    for (q in queues) {
                        q.chunks.addLast(chunk)
                        q.frames += got
                        // A stalled consumer keeps at most ~2 s.
                        while (q.frames > MAX_FRAMES) q.frames -= q.chunks.removeFirst().size / 2
                    }
                }
            }
        }
    }

    private fun take(queue: Queue, out: FloatArray): Int = synchronized(queues) {
        var written = 0
        while (queue.chunks.isNotEmpty()) {
            val chunk = queue.chunks.first()
            if (written + chunk.size > out.size) break
            chunk.copyInto(out, written)
            written += chunk.size
            queue.frames -= chunk.size / 2
            queue.chunks.removeFirst()
        }
        written / 2
    }

    private companion object {
        const val MAX_FRAMES = 96_000
    }
}
