package net.oxog.phonebox.deskpanel

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.Build
import android.os.Process
import android.os.SystemClock
import java.util.concurrent.atomic.AtomicBoolean

internal object ScreenAudioWire {
    const val OPCODE_AUDIO_FRAME = 0x03
    const val OPCODE_AUDIO_META = 0x04
    const val CODEC_PCM16 = 0

    data class AudioMeta(
        val codec: Int,
        val sampleRate: Int,
        val channels: Int,
        val frameSamples: Int,
        val active: Boolean,
        val isVirtual: Boolean,
    )

    fun parseMeta(data: ByteArray, length: Int): AudioMeta? {
        if (length < 10 || (data[0].toInt() and 0xFF) != OPCODE_AUDIO_META) return null
        val codec = data[1].toInt() and 0xFF
        val sampleRate = (data[2].toInt() and 0xFF) or
            ((data[3].toInt() and 0xFF) shl 8) or
            ((data[4].toInt() and 0xFF) shl 16) or
            ((data[5].toInt() and 0xFF) shl 24)
        val channels = data[6].toInt() and 0xFF
        val frameSamples = (data[7].toInt() and 0xFF) or ((data[8].toInt() and 0xFF) shl 8)
        val flags = data[9].toInt() and 0xFF
        val active = (flags and 1) != 0
        val isVirtual = (flags and 2) != 0
        if (active && (sampleRate < 8000 || sampleRate > 192000 || channels !in 1..2 || frameSamples <= 0)) {
            return null
        }
        return AudioMeta(codec, sampleRate, channels, frameSamples, active, isVirtual)
    }

    fun parseFramePayloadLength(data: ByteArray, length: Int): Int {
        if (length < 15 || (data[0].toInt() and 0xFF) != OPCODE_AUDIO_FRAME) return -1
        val payloadLen = (data[1].toInt() and 0xFF) or ((data[2].toInt() and 0xFF) shl 8)
        if (payloadLen <= 0 || 15 + payloadLen > length) return -1
        return payloadLen
    }

    fun applyFadeInPcm16Le(buf: ByteArray, offset: Int, length: Int, channels: Int, fadeFrames: Int) {
        val ch = channels.coerceAtLeast(1)
        val bytesPerFrame = ch * 2
        if (fadeFrames <= 0 || length < bytesPerFrame) return
        val totalFrames = length / bytesPerFrame
        val framesToFade = minOf(fadeFrames, totalFrames)
        for (f in 0 until framesToFade) {
            val gain = f.toFloat() / framesToFade.toFloat()
            for (c in 0 until ch) {
                val p = offset + f * bytesPerFrame + c * 2
                val raw = ((buf[p].toInt() and 0xFF) or (buf[p + 1].toInt() shl 8)).toShort()
                val scaled = (raw.toFloat() * gain).toInt().coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
                buf[p] = (scaled and 0xFF).toByte()
                buf[p + 1] = ((scaled ushr 8) and 0xFF).toByte()
            }
        }
    }
}

internal class AudioPacketQueue(private val maxPackets: Int = 5) {
    private val lock = Object()
    private val queue = java.util.ArrayDeque<ByteArray>(maxPackets.coerceAtLeast(1) + 1)

    fun offer(pkt: ByteArray) {
        val cap = maxPackets.coerceAtLeast(1)
        synchronized(lock) {
            while (queue.size >= cap) {
                queue.removeFirst()
            }
            queue.addLast(pkt)
            lock.notifyAll()
        }
    }

    fun take(timeoutMs: Long): ByteArray? {
        val deadline = System.currentTimeMillis() + timeoutMs.coerceAtLeast(0L)
        synchronized(lock) {
            while (queue.isEmpty()) {
                val rem = deadline - System.currentTimeMillis()
                if (rem <= 0L) return null
                lock.wait(rem)
            }
            return queue.removeFirst()
        }
    }

    fun size(): Int = synchronized(lock) { queue.size }

    fun clear() {
        synchronized(lock) {
            queue.clear()
            lock.notifyAll()
        }
    }
}

internal class ScreenMirrorAudioPlayer {
    private val gate = Any()
    private val stopped = AtomicBoolean(true)
    private var queue = AudioPacketQueue(5)
    private var track: AudioTrack? = null
    private var playThread: Thread? = null

    fun configure(sampleRate: Int, channels: Int, frameSamples: Int, isWifi: Boolean) {
        stop()

        val chMask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val minBuf = AudioTrack.getMinBufferSize(sampleRate, chMask, AudioFormat.ENCODING_PCM_16BIT)
        val frameBytes = (frameSamples * channels * 2).coerceAtLeast(64)
        val trackBufBytes = maxOf(if (minBuf > 0) minBuf else frameBytes * 2, frameBytes * 3)

        val audioTrack = createAudioTrack(sampleRate, chMask, trackBufBytes) ?: return
        if (audioTrack.state != AudioTrack.STATE_INITIALIZED) {
            try { audioTrack.release() } catch (_: Throwable) {}
            return
        }
        val pktQueue = AudioPacketQueue(if (isWifi) 6 else 4)
        val activeFlag = AtomicBoolean(false)
        val zeroFrame = ByteArray(frameBytes)
        val prebuffer = if (isWifi) 2 else 1
        val fadeFrames = (sampleRate / 500).coerceAtLeast(1) // ~2ms ramp

        synchronized(gate) {
            stopped.set(false)
            queue = pktQueue
            track = audioTrack
            val t = Thread({
                Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
                var playing = false
                try {
                    while (!stopped.get() && !activeFlag.get()) {
                        if (!playing) {
                            val first = try {
                                pktQueue.take(250L)
                            } catch (_: InterruptedException) {
                                break
                            } ?: continue
                            if (stopped.get()) break

                            if (prebuffer > 1 && pktQueue.size() < prebuffer - 1) {
                                val waitEnd = SystemClock.elapsedRealtime() + 25L
                                while (!stopped.get() && pktQueue.size() < prebuffer - 1) {
                                    val rem = waitEnd - SystemClock.elapsedRealtime()
                                    if (rem <= 0L) break
                                    try {
                                        Thread.sleep(minOf(rem, 5L))
                                    } catch (_: InterruptedException) {
                                        break
                                    }
                                }
                            }
                            if (stopped.get()) break

                            ScreenAudioWire.applyFadeInPcm16Le(first, 0, first.size, channels, fadeFrames)
                            try {
                                audioTrack.play()
                            } catch (_: Throwable) {
                                break
                            }
                            playing = true
                            if (!writePcm(audioTrack, first)) break
                        } else {
                            val next = try {
                                pktQueue.take(120L)
                            } catch (_: InterruptedException) {
                                break
                            }
                            if (stopped.get()) break
                            if (next != null) {
                                if (!writePcm(audioTrack, next)) break
                            } else {
                                writePcm(audioTrack, zeroFrame)
                                try {
                                    audioTrack.pause()
                                    audioTrack.flush()
                                } catch (_: Throwable) {}
                                playing = false
                            }
                        }
                    }
                } finally {
                    activeFlag.set(true)
                }
            }, "pb-audio-play").apply { isDaemon = true }
            playThread = t
            t.start()
        }
    }

    fun enqueue(data: ByteArray, offset: Int, length: Int) {
        if (stopped.get() || length <= 0 || offset < 0 || offset + length > data.size) return
        val copy = data.copyOfRange(offset, offset + length)
        queue.offer(copy)
    }

    fun stop() {
        val t: Thread?
        val at: AudioTrack?
        val q: AudioPacketQueue
        synchronized(gate) {
            stopped.set(true)
            q = queue
            t = playThread
            playThread = null
            at = track
            track = null
        }
        q.clear()
        t?.interrupt()
        if (at != null) {
            try { at.pause() } catch (_: Throwable) {}
            try { at.flush() } catch (_: Throwable) {}
        }
        if (t != null && t !== Thread.currentThread()) {
            try {
                t.join(300L)
            } catch (_: InterruptedException) {}
        }
        if (at != null) {
            try { at.stop() } catch (_: Throwable) {}
            try { at.release() } catch (_: Throwable) {}
        }
    }

    private fun writePcm(audioTrack: AudioTrack, data: ByteArray): Boolean {
        var off = 0
        while (off < data.size && !stopped.get()) {
            val written = try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    audioTrack.write(data, off, data.size - off, AudioTrack.WRITE_BLOCKING)
                } else {
                    audioTrack.write(data, off, data.size - off)
                }
            } catch (_: Throwable) {
                return false
            }
            if (written <= 0) return false
            off += written
        }
        return true
    }

    @Suppress("DEPRECATION")
    private fun createAudioTrack(sampleRate: Int, chMask: Int, bufBytes: Int): AudioTrack? {
        val attrs = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_UNKNOWN)
            .build()
        val fmt = AudioFormat.Builder()
            .setSampleRate(sampleRate)
            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
            .setChannelMask(chMask)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val lowLat = try {
                AudioTrack.Builder()
                    .setAudioAttributes(attrs)
                    .setAudioFormat(fmt)
                    .setBufferSizeInBytes(bufBytes)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build()
            } catch (_: Throwable) {
                null
            }
            if (lowLat != null) {
                if (lowLat.state == AudioTrack.STATE_INITIALIZED) return lowLat
                try { lowLat.release() } catch (_: Throwable) {}
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            return try {
                AudioTrack.Builder()
                    .setAudioAttributes(attrs)
                    .setAudioFormat(fmt)
                    .setBufferSizeInBytes(bufBytes)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .build()
            } catch (_: Throwable) {
                null
            }
        }
        return try {
            AudioTrack(
                attrs,
                fmt,
                bufBytes,
                AudioTrack.MODE_STREAM,
                AudioManager.AUDIO_SESSION_ID_GENERATE,
            )
        } catch (_: Throwable) {
            null
        }
    }
}
