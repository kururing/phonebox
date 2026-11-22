package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Process
import android.os.SystemClock
import android.util.Log
import android.text.Editable
import android.text.TextWatcher
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.inputmethod.InputConnectionWrapper
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.FrameLayout
import java.net.URLEncoder
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.locks.ReentrantReadWriteLock
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

internal object CodecProbe {
    data class Result(
        val h264Decoder: String?,
        val h264LowLat: Boolean,
        val hevcDecoder: String?,
        val hevcLowLat: Boolean,
        val hasHevc: Boolean,
        val h264SafeWidth: Int = 1280,
        val h264SafeHeight: Int = 720,
        val h264Adaptive: Boolean = false,
        val hevcSafeWidth: Int = 1280,
        val hevcSafeHeight: Int = 720,
        val hevcAdaptive: Boolean = false,
    ) {
        fun toJson(): JSONObject = JSONObject()
            .put("h264Decoder", h264Decoder ?: JSONObject.NULL)
            .put("h264LowLatency", h264LowLat)
            .put("h264SafeWidth", h264SafeWidth)
            .put("h264SafeHeight", h264SafeHeight)
            .put("h264Adaptive", h264Adaptive)
            .put("hevcDecoder", hevcDecoder ?: JSONObject.NULL)
            .put("hevcLowLatency", hevcLowLat)
            .put("hevcSafeWidth", hevcSafeWidth)
            .put("hevcSafeHeight", hevcSafeHeight)
            .put("hevcAdaptive", hevcAdaptive)
            .put("hasHevc", hasHevc)
    }

    private data class Pick(
        var name: String? = null,
        var lowLat: Boolean = false,
        var hw: Boolean = false,
        var safeW: Int = 1280,
        var safeH: Int = 720,
        var adaptive: Boolean = false,
    )

    fun probe(aspectW: Int = 16, aspectH: Int = 9): Result {
        val infos = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
        val h264 = pickBest(infos, "video/avc", aspectW, aspectH)
        val hevc = pickBest(infos, "video/hevc", aspectW, aspectH)
        return Result(
            h264Decoder = h264.name,
            h264LowLat = h264.lowLat,
            hevcDecoder = hevc.name,
            hevcLowLat = hevc.lowLat,
            hasHevc = hevc.name != null && hevc.hw,
            h264SafeWidth = h264.safeW,
            h264SafeHeight = h264.safeH,
            h264Adaptive = h264.adaptive,
            hevcSafeWidth = hevc.safeW,
            hevcSafeHeight = hevc.safeH,
            hevcAdaptive = hevc.adaptive,
        )
    }

    private fun pickBest(infos: Array<MediaCodecInfo>, mime: String, aspectW: Int, aspectH: Int): Pick {
        val best = Pick()
        var bestScore = -1
        for (info in infos) {
            if (info.isEncoder) continue
            var caps: MediaCodecInfo.CodecCapabilities? = null
            try {
                for (type in info.supportedTypes) {
                    if (type.equals(mime, ignoreCase = true)) {
                        caps = info.getCapabilitiesForType(type)
                        break
                    }
                }
            } catch (_: IllegalArgumentException) {
                continue
            }
            if (caps == null) continue

            val hw = isHardware(info)
            var lowLat = false
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                try {
                    lowLat = caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency)
                } catch (_: Throwable) {}
            }
            var adaptive = false
            try {
                adaptive = caps.isFeatureSupported(MediaCodecInfo.CodecCapabilities.FEATURE_AdaptivePlayback)
            } catch (_: Throwable) {}

            val name = info.name
            val score = (if (hw) 10 else 0) +
                (if (lowLat) 5 else 0) +
                (if (name.contains("low_latency", ignoreCase = true)) 2 else 0) +
                (if (isAlias(info)) 0 else 1)
            if (score > bestScore) {
                bestScore = score
                best.name = name
                best.lowLat = lowLat
                best.hw = hw
                best.adaptive = adaptive
                val safe = computeSafe60FpsSize(caps, aspectW, aspectH)
                best.safeW = safe.first
                best.safeH = safe.second
            }
        }
        return best
    }

    private fun computeSafe60FpsSize(
        caps: MediaCodecInfo.CodecCapabilities,
        aspectW: Int,
        aspectH: Int,
    ): Pair<Int, Int> {
        val vc = try { caps.videoCapabilities } catch (_: Throwable) { null } ?: return 1280 to 720
        val shortBase = min(aspectW.coerceAtLeast(1), aspectH.coerceAtLeast(1))
        val longBase = max(aspectW.coerceAtLeast(1), aspectH.coerceAtLeast(1))
        val longRatio = longBase.toDouble() / shortBase.toDouble()
        val candidateShorts = intArrayOf(2160, 1440, 1080, 900, 720, 540, 480)
        for (shortSide in candidateShorts) {
            val w = (((shortSide * longRatio).roundToInt() / 2) * 2).coerceAtLeast(64)
            val h = ((shortSide / 2) * 2).coerceAtLeast(64)
            if (supportsRate60(vc, w, h)) return w to h
        }
        val minW = (((480 * longRatio).roundToInt() / 2) * 2).coerceAtLeast(64)
        return minW to 480
    }

    private fun supportsRate60(vc: MediaCodecInfo.VideoCapabilities, w: Int, h: Int): Boolean {
        val pairs = arrayOf(w to h, h to w)
        for ((tw, th) in pairs) {
            val supported = try { vc.areSizeAndRateSupported(tw, th, 60.0) } catch (_: Throwable) { false }
            if (!supported) continue
            val achievable = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                try { vc.getAchievableFrameRatesFor(tw, th) } catch (_: Throwable) { null }
            } else null
            if (achievable == null || achievable.lower >= 69.0) return true
        }
        return false
    }

    private fun isHardware(info: MediaCodecInfo): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            return !info.isSoftwareOnly && info.isHardwareAccelerated
        }
        val n = info.name
        return !n.startsWith("OMX.google.") && !n.startsWith("c2.android.")
    }

    private fun isAlias(info: MediaCodecInfo): Boolean =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) info.isAlias else false
}

internal class InFlightTracker(
    private val maxInFlight: Int = 2,
    private val warmupMax: Int = 4,
) {
    private val lock = Object()
    private var inFlight = 0
    private var primed = false

    fun awaitSlot(isIdr: Boolean, waitMs: Long): Boolean {
        val limit = when {
            !primed -> warmupMax
            isIdr -> maxInFlight + 1
            else -> maxInFlight
        }
        val deadline = System.currentTimeMillis() + waitMs.coerceAtLeast(0L)
        synchronized(lock) {
            while (inFlight >= limit) {
                val rem = deadline - System.currentTimeMillis()
                if (rem <= 0L) return false
                try {
                    lock.wait(rem)
                } catch (_: InterruptedException) {
                    Thread.currentThread().interrupt()
                    return false
                }
            }
            inFlight++
            return true
        }
    }

    fun cancelReserved() {
        synchronized(lock) {
            if (inFlight > 0) inFlight--
            lock.notifyAll()
        }
    }

    fun onOutputReleased(count: Int) {
        if (count <= 0) return
        synchronized(lock) {
            inFlight = (inFlight - count).coerceAtLeast(0)
            primed = true
            lock.notifyAll()
        }
    }

    fun current(): Int = synchronized(lock) { inFlight }

    fun reset() {
        synchronized(lock) {
            inFlight = 0
            primed = false
            lock.notifyAll()
        }
    }
}

internal class DecodeTelemetryWindow(
    private val windowMs: Long = 500L,
    startMs: Long = 0L,
) {
    private val lock = Object()
    private val samples = ArrayList<Double>(64)
    private var dropped = 0
    private var maxInFlight = 0
    private var windowStartMs = startMs

    fun recordDecoded(decodeMs: Double, inFlightNow: Int) {
        synchronized(lock) {
            samples.add(decodeMs.coerceAtLeast(0.0))
            if (inFlightNow > maxInFlight) maxInFlight = inFlightNow
        }
    }

    fun recordDropped() {
        synchronized(lock) {
            dropped++
        }
    }

    fun pollPacket(nowMs: Long): ByteArray? {
        val snapshotSamples: DoubleArray
        val dropCount: Int
        val peakInFlight: Int
        val elapsedMs: Long
        synchronized(lock) {
            if (windowStartMs == 0L) windowStartMs = nowMs
            elapsedMs = nowMs - windowStartMs
            if (elapsedMs < windowMs) return null
            if (samples.isEmpty() && dropped == 0) {
                windowStartMs = nowMs
                return null
            }
            snapshotSamples = DoubleArray(samples.size) { samples[it] }
            dropCount = dropped
            peakInFlight = maxInFlight
            samples.clear()
            dropped = 0
            maxInFlight = 0
            windowStartMs = nowMs
        }

        snapshotSamples.sort()
        val p50 = percentile(snapshotSamples, 50.0).roundToInt().coerceIn(0, 65535)
        val p95 = percentile(snapshotSamples, 95.0).roundToInt().coerceIn(0, 65535)
        val fpsX10 = if (elapsedMs > 0L) {
            ((snapshotSamples.size * 10000.0) / elapsedMs).roundToInt().coerceIn(0, 65535)
        } else 0
        val dropClamped = dropCount.coerceIn(0, 65535)
        val inflightClamped = peakInFlight.coerceIn(0, 65535)

        val pkt = ByteArray(11)
        pkt[0] = 0x23
        writeU16Le(pkt, 1, p50)
        writeU16Le(pkt, 3, p95)
        writeU16Le(pkt, 5, fpsX10)
        writeU16Le(pkt, 7, dropClamped)
        writeU16Le(pkt, 9, inflightClamped)
        return pkt
    }

    private fun percentile(sorted: DoubleArray, p: Double): Double {
        if (sorted.isEmpty()) return 0.0
        val idx = (((p / 100.0) * sorted.size).let { kotlin.math.ceil(it).toInt() } - 1)
            .coerceIn(0, sorted.size - 1)
        return sorted[idx]
    }

    private fun writeU16Le(dst: ByteArray, off: Int, value: Int) {
        dst[off] = (value and 0xFF).toByte()
        dst[off + 1] = ((value ushr 8) and 0xFF).toByte()
    }
}

/**
 * Coordinates concurrent input/output use of a native resource (MediaCodec) with single-shot stop/release,
 * ensuring surface destruction or teardown cannot race with active buffer calls.
 */
internal class CodecGuard<T>(
    private val resource: T,
    private val onStop: (T) -> Unit,
    private val onRelease: (T) -> Unit,
) {
    private val rw = ReentrantReadWriteLock()
    private val stopping = AtomicBoolean(false)
    @Volatile private var released = false

    fun requestStop() {
        stopping.set(true)
    }

    fun isStopping(): Boolean = stopping.get() || released

    fun <R> withAccess(fallback: R, block: (T) -> R): R {
        if (isStopping()) return fallback
        val read = rw.readLock()
        read.lock()
        return try {
            if (isStopping()) fallback else block(resource)
        } finally {
            read.unlock()
        }
    }

    fun releaseSafely(maxWaitMs: Long = Long.MAX_VALUE): Boolean {
        stopping.set(true)
        val write = rw.writeLock()
        val acquired = if (maxWaitMs == Long.MAX_VALUE) {
            write.lock()
            true
        } else {
            try {
                write.tryLock(maxWaitMs, TimeUnit.MILLISECONDS)
            } catch (_: InterruptedException) {
                Thread.currentThread().interrupt()
                false
            }
        }
        if (!acquired) return false
        return try {
            if (!released) {
                released = true
                try { onStop(resource) } catch (_: Throwable) {}
                try { onRelease(resource) } catch (_: Throwable) {}
            }
            true
        } finally {
            write.unlock()
        }
    }
}

internal object ScreenTouchCodec {
    const val OPCODE_TOUCH: Byte = 0x30
    const val STATE_DOWN = 0
    const val STATE_MOVE = 1
    const val STATE_UP = 2
    const val STATE_CANCEL = 3
    const val MAX_POINTERS = 10

    fun encodeTouchFrame(
        ids: IntArray,
        states: IntArray,
        xs: FloatArray,
        ys: FloatArray,
        pressures: FloatArray,
        count: Int,
        viewW: Int,
        viewH: Int,
    ): ByteArray {
        val n = count.coerceIn(0, MAX_POINTERS)
        if (n == 0 || viewW <= 0 || viewH <= 0) return encodeCancelAll()
        val out = ByteArray(2 + n * 8)
        out[0] = OPCODE_TOUCH
        out[1] = n.toByte()
        val wDenom = if (viewW > 1) (viewW - 1).toDouble() else 1.0
        val hDenom = if (viewH > 1) (viewH - 1).toDouble() else 1.0
        for (i in 0 until n) {
            val o = 2 + i * 8
            out[o] = (ids[i] and 0xFF).toByte()
            out[o + 1] = states[i].coerceIn(STATE_DOWN, STATE_CANCEL).toByte()
            val xNorm = if (viewW > 1) ((xs[i] / wDenom) * 65535.0).roundToInt().coerceIn(0, 65535) else 0
            val yNorm = if (viewH > 1) ((ys[i] / hDenom) * 65535.0).roundToInt().coerceIn(0, 65535) else 0
            val p = (pressures[i] * 1024f).roundToInt().coerceIn(1, 1024)
            out[o + 2] = (xNorm and 0xFF).toByte()
            out[o + 3] = ((xNorm ushr 8) and 0xFF).toByte()
            out[o + 4] = (yNorm and 0xFF).toByte()
            out[o + 5] = ((yNorm ushr 8) and 0xFF).toByte()
            out[o + 6] = (p and 0xFF).toByte()
            out[o + 7] = ((p ushr 8) and 0xFF).toByte()
        }
        return out
    }

    fun encodeCancelAll(): ByteArray = byteArrayOf(OPCODE_TOUCH, 0x00)

    fun isMoveOnly(packet: ByteArray): Boolean {
        if (packet.size < 2 || packet[0] != OPCODE_TOUCH) return false
        val n = packet[1].toInt() and 0xFF
        if (n <= 0 || packet.size < 2 + n * 8) return false
        for (i in 0 until n) {
            if ((packet[2 + i * 8 + 1].toInt() and 0xFF) != STATE_MOVE) return false
        }
        return true
    }
}

internal class TouchPacketQueue(private val maxSize: Int = 64) {
    private val lock = Object()
    private val queue = java.util.ArrayDeque<ByteArray>()

    fun offer(pkt: ByteArray) {
        synchronized(lock) {
            if (ScreenTouchCodec.isMoveOnly(pkt) && !queue.isEmpty() && ScreenTouchCodec.isMoveOnly(queue.peekLast()!!)) {
                queue.removeLast()
                queue.addLast(pkt)
                lock.notifyAll()
                return
            }
            while (queue.size >= maxSize) {
                var removed = false
                val it = queue.iterator()
                while (it.hasNext()) {
                    if (ScreenTouchCodec.isMoveOnly(it.next())) {
                        it.remove()
                        removed = true
                        break
                    }
                }
                if (!removed) queue.removeFirst()
            }
            queue.addLast(pkt)
            lock.notifyAll()
        }
    }

    fun take(timeoutMs: Long): ByteArray? {
        val deadline = System.currentTimeMillis() + timeoutMs
        synchronized(lock) {
            while (queue.isEmpty()) {
                val rem = deadline - System.currentTimeMillis()
                if (rem <= 0L) return null
                lock.wait(rem)
            }
            return queue.removeFirst()
        }
    }

    fun clear() {
        synchronized(lock) {
            queue.clear()
            lock.notifyAll()
        }
    }
}

class ScreenMirrorPlayer(
    private val activity: Activity,
    private val container: FrameLayout,
    private val link: AgentLink,
    private val onEvent: (event: String, detail: String) -> Unit,
) : SurfaceHolder.Callback {

    private val gate = Any()
    @Volatile private var running = false
    @Volatile private var generation = 0
    @Volatile private var activeWs: ScreenMirrorWsClient? = null
    @Volatile private var activeCodec: CodecGuard<MediaCodec>? = null
    @Volatile private var activeAudio: ScreenMirrorAudioPlayer? = null
    @Volatile private var surface: Surface? = null
    @Volatile private var surfaceLatch = CountDownLatch(1)
    @Volatile private var wifiLock: WifiManager.WifiLock? = null
    private val wifiLockGate = Any()
    private val touchQueue = TouchPacketQueue()
    private val touchIds = IntArray(ScreenTouchCodec.MAX_POINTERS)
    private val touchStates = IntArray(ScreenTouchCodec.MAX_POINTERS)
    private val touchXs = FloatArray(ScreenTouchCodec.MAX_POINTERS)
    private val touchYs = FloatArray(ScreenTouchCodec.MAX_POINTERS)
    private val touchPressures = FloatArray(ScreenTouchCodec.MAX_POINTERS)
    private var worker: Thread? = null

    private var lastLeft = -1
    private var lastTop = -1
    private var lastW = -1
    private var lastH = -1

    @SuppressLint("ClickableViewAccessibility")
    private val surfaceView = SurfaceView(activity).apply {
        visibility = View.GONE
        isClickable = true
        isFocusable = false
        setZOrderMediaOverlay(true)
        holder.addCallback(this@ScreenMirrorPlayer)
        setOnTouchListener { v, event -> handleSurfaceTouch(v, event) }
    }

    private fun sendRemoteKey(text: String? = null, key: String? = null) {
        if (!running) return
        val params = ArrayList<String>(2)
        if (!text.isNullOrEmpty()) params.add("text=${URLEncoder.encode(text, "UTF-8")}")
        if (!key.isNullOrEmpty()) params.add("key=${URLEncoder.encode(key, "UTF-8")}")
        if (params.isEmpty()) return
        val path = "/api/screen/key?${params.joinToString("&")}"
        Thread({ try { link.request("POST", path) } catch (_: Throwable) {} }, "pb-key").start()
    }

    private var kbActive = false
    private val kbInput = object : EditText(activity) {
        override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection? {
            val base = super.onCreateInputConnection(outAttrs) ?: return null
            return object : InputConnectionWrapper(base, true) {
                override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
                    val s = text?.toString().orEmpty()
                    if (s.isNotEmpty()) sendRemoteKey(text = s)
                    return true
                }
                override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                    repeat(beforeLength.coerceAtLeast(1)) { sendRemoteKey(key = "backspace") }
                    return true
                }
                override fun sendKeyEvent(event: KeyEvent): Boolean {
                    if (event.action == KeyEvent.ACTION_DOWN) {
                        when (event.keyCode) {
                            KeyEvent.KEYCODE_DEL -> { sendRemoteKey(key = "backspace"); return true }
                            KeyEvent.KEYCODE_ENTER -> { sendRemoteKey(key = "enter"); return true }
                            KeyEvent.KEYCODE_TAB -> { sendRemoteKey(key = "tab"); return true }
                            KeyEvent.KEYCODE_ESCAPE -> { sendRemoteKey(key = "esc"); return true }
                        }
                    }
                    return super.sendKeyEvent(event)
                }
            }
        }
    }.apply {
        alpha = 0f
        isFocusable = true
        isFocusableInTouchMode = true
        isSingleLine = true
        imeOptions = EditorInfo.IME_ACTION_NONE or EditorInfo.IME_FLAG_NO_EXTRACT_UI
        inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) {
                if (s != null && s.isNotEmpty()) {
                    sendRemoteKey(text = s.toString())
                    s.clear()
                }
            }
        })
    }

    fun dismissKeyboardIfOpen(): Boolean {
        if (!kbActive) return false
        setKeyboardVisible(false)
        return true
    }

    private fun setKeyboardVisible(show: Boolean) {
        activity.runOnUiThread {
            val imm = activity.getSystemService(Context.INPUT_METHOD_SERVICE) as? InputMethodManager
            if (show && running && surfaceView.visibility == View.VISIBLE) {
                if (!kbActive) {
                    kbActive = true
                    kbInput.requestFocusFromTouch()
                    kbInput.postDelayed({
                        if (kbActive && running) {
                            val shown = imm?.showSoftInput(kbInput, InputMethodManager.SHOW_IMPLICIT) ?: false
                            if (!shown) {
                                @Suppress("DEPRECATION")
                                imm?.toggleSoftInput(InputMethodManager.SHOW_FORCED, 0)
                            }
                        }
                    }, 60L)
                }
            } else if (kbActive) {
                kbActive = false
                kbInput.clearFocus()
                imm?.hideSoftInputFromWindow(kbInput.windowToken, 0)
            }
        }
    }

    init {
        container.addView(surfaceView, FrameLayout.LayoutParams(1, 1))
        container.addView(kbInput, FrameLayout.LayoutParams(1, 1))
    }

    fun capabilitiesJson(): String = CodecProbe.probe().toJson().toString()

    @Volatile private var fitMode: String = "fill"
    @Volatile private var targetFps: Int = 60
    @Volatile private var videoW: Int = 0
    @Volatile private var videoH: Int = 0

    fun start(
        leftPx: Int,
        topPx: Int,
        widthPx: Int,
        heightPx: Int,
        preferHevc: Boolean,
        bitrate: Int = 8_000_000,
        audioMode: String = "off",
        audioDevice: String = "",
        audioAutoSwitch: Boolean = false,
        fitMode: String = "fill",
        targetFps: Int = 60,
    ) {
        this.fitMode = fitMode
        this.targetFps = targetFps.coerceIn(15, 60)
        this.videoW = 0
        this.videoH = 0
        val w = widthPx.coerceAtLeast(16)
        val h = heightPx.coerceAtLeast(16)
        val l = leftPx.coerceAtLeast(0)
        val t = topPx.coerceAtLeast(0)
        val gen: Int
        val prevWs: ScreenMirrorWsClient?
        val prevCodec: CodecGuard<MediaCodec>?
        val prevAudio: ScreenMirrorAudioPlayer?
        val prevWorker: Thread?

        synchronized(gate) {
            prevWs = activeWs
            prevCodec = activeCodec
            prevAudio = activeAudio
            prevWorker = worker
            activeWs = null
            activeCodec = null
            activeAudio = null
            running = true
            gen = ++generation
            lastLeft = l; lastTop = t; lastW = w; lastH = h
            val tWorker = Thread(
                { runLoop(gen, prevWorker, preferHevc, bitrate, audioMode, audioDevice, audioAutoSwitch) },
                "pb-screen-ws"
            ).apply { isDaemon = true }
            worker = tWorker
        }

        prevCodec?.requestStop()
        prevAudio?.stop()
        try { prevWs?.close() } catch (_: Exception) {}
        prevCodec?.releaseSafely(300L)
        prevWorker?.interrupt()

        activity.runOnUiThread {
            if (!running || gen != generation) return@runOnUiThread
            applyLayoutLocked(l, t, w, h, visible = true)
        }
        synchronized(gate) {
            if (running && gen == generation) worker?.start()
        }
    }

    fun updateRect(leftPx: Int, topPx: Int, widthPx: Int, heightPx: Int) {
        val visible = widthPx >= 16 && heightPx >= 16
        val l = leftPx.coerceAtLeast(0)
        val t = topPx.coerceAtLeast(0)
        val w = widthPx.coerceAtLeast(1)
        val h = heightPx.coerceAtLeast(1)
        activity.runOnUiThread {
            if (!running) {
                if (surfaceView.visibility != View.GONE) surfaceView.visibility = View.GONE
                return@runOnUiThread
            }
            if (!visible) {
                if (surfaceView.visibility != View.GONE) surfaceView.visibility = View.GONE
                return@runOnUiThread
            }
            if (surfaceView.visibility != View.VISIBLE || l != lastLeft || t != lastTop || w != lastW || h != lastH) {
                lastLeft = l; lastTop = t; lastW = w; lastH = h
                applyLayoutLocked(l, t, w, h, visible = true)
            }
        }
    }

    fun stop() {
        val ws: ScreenMirrorWsClient?
        val ac: CodecGuard<MediaCodec>?
        val au: ScreenMirrorAudioPlayer?
        val tWorker: Thread?
        synchronized(gate) {
            running = false
            ++generation
            ws = activeWs
            activeWs = null
            ac = activeCodec
            activeCodec = null
            au = activeAudio
            activeAudio = null
            tWorker = worker
            worker = null
        }
        ac?.requestStop()
        au?.stop()
        touchQueue.clear()
        try { ws?.close() } catch (_: Exception) {}
        tWorker?.interrupt()
        ac?.releaseSafely(300L)
        releaseWifiLock()
        activity.runOnUiThread {
            if (!running) {
                surfaceView.visibility = View.GONE
                setKeyboardVisible(false)
            }
        }
    }

    private fun handleSurfaceTouch(v: View, event: MotionEvent): Boolean {
        if (!running) return false
        val action = event.actionMasked
        if (action == MotionEvent.ACTION_DOWN) {
            v.parent?.requestDisallowInterceptTouchEvent(true)
        }
        if (action == MotionEvent.ACTION_CANCEL) {
            touchQueue.offer(ScreenTouchCodec.encodeCancelAll())
            return true
        }
        val viewW = v.width
        val viewH = v.height
        if (viewW <= 0 || viewH <= 0) return true
        val count = minOf(event.pointerCount, ScreenTouchCodec.MAX_POINTERS)
        if (count <= 0) return true
        val actionIdx = event.actionIndex

        val ids = touchIds
        val states = touchStates
        val xs = touchXs
        val ys = touchYs
        val pressures = touchPressures

        for (i in 0 until count) {
            ids[i] = event.getPointerId(i)
            xs[i] = event.getX(i)
            ys[i] = event.getY(i)
            pressures[i] = event.getPressure(i)
            states[i] = when (action) {
                MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN ->
                    if (i == actionIdx) ScreenTouchCodec.STATE_DOWN else ScreenTouchCodec.STATE_MOVE
                MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP ->
                    if (i == actionIdx) ScreenTouchCodec.STATE_UP else ScreenTouchCodec.STATE_MOVE
                else -> ScreenTouchCodec.STATE_MOVE
            }
        }

        if (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN ||
            action == MotionEvent.ACTION_MOVE || action == MotionEvent.ACTION_UP ||
            action == MotionEvent.ACTION_POINTER_UP
        ) {
            touchQueue.offer(ScreenTouchCodec.encodeTouchFrame(ids, states, xs, ys, pressures, count, viewW, viewH))
        }
        return true
    }

    private fun applyLayoutLocked(l: Int, t: Int, w: Int, h: Int, visible: Boolean) {
        var rl = l
        var rt = t
        var rw = w
        var rh = h
        val vw = videoW
        val vh = videoH
        if (vw > 0 && vh > 0 && w > 0 && h > 0 && fitMode != "fill") {
            val scale = if (fitMode == "cover") {
                max(w.toDouble() / vw.toDouble(), h.toDouble() / vh.toDouble())
            } else {
                min(w.toDouble() / vw.toDouble(), h.toDouble() / vh.toDouble())
            }
            rw = (vw * scale).roundToInt().coerceAtLeast(16)
            rh = (vh * scale).roundToInt().coerceAtLeast(16)
            rl = l + (w - rw) / 2
            rt = t + (h - rh) / 2
        }
        val lp = (surfaceView.layoutParams as? FrameLayout.LayoutParams) ?: FrameLayout.LayoutParams(rw, rh)
        if (lp.width != rw || lp.height != rh || lp.leftMargin != rl || lp.topMargin != rt) {
            lp.width = rw
            lp.height = rh
            lp.leftMargin = rl
            lp.topMargin = rt
            surfaceView.layoutParams = lp
        }
        val targetVis = if (visible) View.VISIBLE else View.GONE
        if (surfaceView.visibility != targetVis) surfaceView.visibility = targetVis
        if (!visible && kbActive) setKeyboardVisible(false)
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        surface = holder.surface
        surfaceLatch.countDown()
        val ws = activeWs
        if (running && ws != null) {
            Thread({ try { ws.sendBinary(byteArrayOf(0x10)) } catch (_: Throwable) {} }, "pb-idr").start()
        }
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        surface = holder.surface
        surfaceLatch.countDown()
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surface = null
        surfaceLatch = CountDownLatch(1)
        val ws: ScreenMirrorWsClient?
        val ac: CodecGuard<MediaCodec>?
        val au: ScreenMirrorAudioPlayer?
        synchronized(gate) {
            ws = activeWs
            ac = activeCodec
            if (activeCodec === ac) activeCodec = null
            au = activeAudio
            if (activeAudio === au) activeAudio = null
        }
        ac?.requestStop()
        au?.stop()
        touchQueue.clear()
        try { ws?.close() } catch (_: Throwable) {}
        ac?.releaseSafely(300L)
    }

    private fun isAlive(gen: Int): Boolean = running && gen == generation

    private fun emit(gen: Int, event: String, detail: String) {
        if (isAlive(gen)) onEvent(event, detail)
    }

    private fun emitForGen(gen: Int, event: String, detail: String) {
        if (gen == generation) onEvent(event, detail)
    }

    private fun waitForPreviousWorker(prevWorker: Thread?, gen: Int) {
        if (prevWorker == null || prevWorker === Thread.currentThread()) return
        val deadline = SystemClock.elapsedRealtime() + 2000L
        while (prevWorker.isAlive && isAlive(gen)) {
            val remaining = deadline - SystemClock.elapsedRealtime()
            if (remaining <= 0L) break
            try {
                prevWorker.join(remaining)
            } catch (_: InterruptedException) {
                if (!isAlive(gen)) return
            }
        }
    }

    private fun awaitValidSurface(gen: Int): Surface? {
        while (isAlive(gen)) {
            val s = surface
            if (s != null && s.isValid) return s
            try {
                surfaceLatch.await(300, TimeUnit.MILLISECONDS)
            } catch (_: InterruptedException) {
                if (!isAlive(gen)) return null
            }
        }
        return null
    }

    private fun runLoop(
        gen: Int,
        prevWorker: Thread?,
        preferHevc: Boolean,
        bitrate: Int,
        audioMode: String,
        audioDevice: String,
        audioAutoSwitch: Boolean,
    ) {
        val alive = { isAlive(gen) }
        waitForPreviousWorker(prevWorker, gen)
        if (!alive()) {
            hideSurfaceUi(gen)
            return
        }

        var backoffMs = 250L
        var allowHevc = preferHevc
        while (alive()) {
            val renderSurface = awaitValidSurface(gen) ?: break
            var ws: ScreenMirrorWsClient? = null
            var ac: CodecGuard<MediaCodec>? = null
            var audioPlayer: ScreenMirrorAudioPlayer? = null
            var outThread: Thread? = null
            var touchThread: Thread? = null
            var pingThread: Thread? = null
            var inFlightRef: InFlightTracker? = null
            var surfaceGone = false
            var negotiatedHevc = false
            val renderedFirst = AtomicBoolean(false)

            try {
                val dm = activity.resources.displayMetrics
                val screenW = dm.widthPixels
                val screenH = dm.heightPixels
                val cap = CodecProbe.probe(screenW, screenH)
                val client = ScreenMirrorWsClient()
                ws = client
                val attached = synchronized(gate) {
                    if (!alive()) {
                        false
                    } else {
                        activeWs = client
                        true
                    }
                }
                if (!attached) break

                val route = client.connect(link, "/api/screen/ws")
                if (!alive()) break
                if (route == AgentLink.Route.WIFI) acquireWifiLock() else releaseWifiLock()

                val effectiveBitrate = if (route == AgentLink.Route.USB) maxOf(bitrate, 18_000_000) else bitrate
                val effectiveMaxBitrate = if (route == AgentLink.Route.USB) maxOf(effectiveBitrate, 28_000_000) else maxOf(effectiveBitrate, 14_000_000)
                val expectHevc = allowHevc && cap.hasHevc
                val adaptiveCap = if (expectHevc) cap.hevcAdaptive else cap.h264Adaptive

                val hs = JSONObject()
                    .put("codecs", JSONArray().apply { put("h264"); if (cap.hasHevc) put("hevc") })
                    .put("h264LowLatency", cap.h264LowLat)
                    .put("hevcLowLatency", cap.hevcLowLat)
                    .put("preferHevc", allowHevc)
                    .put("fps", targetFps)
                    .put("bitrate", effectiveBitrate)
                    .put("maxBitrate", effectiveMaxBitrate)
                    .put("clientModel", "${Build.MANUFACTURER} ${Build.MODEL}".trim())
                    .put("screenWidth", screenW)
                    .put("screenHeight", screenH)
                    .put("h264SafeWidth", cap.h264SafeWidth)
                    .put("h264SafeHeight", cap.h264SafeHeight)
                    .put("hevcSafeWidth", cap.hevcSafeWidth)
                    .put("hevcSafeHeight", cap.hevcSafeHeight)
                    .put("adaptivePlayback", adaptiveCap)
                    .put("route", route.name.lowercase())
                    .put("audio", audioMode != "off")
                    .put("audioMode", audioMode)
                    .put("audioDevice", audioDevice)
                    .put("audioAutoSwitch", audioAutoSwitch)
                    .put("audioCodecs", JSONArray().put("pcm16"))
                client.sendText(hs.toString())

                val msg = ScreenMirrorWsClient.Message()
                if (!client.readMessage(msg)) {
                    if (msg.opcode == ScreenMirrorWsClient.OP_CLOSE && msg.closeReason == "native-unavailable") {
                        throw WsFallbackException("native-unavailable")
                    }
                    if (alive()) {
                        if (surface !== renderSurface || !renderSurface.isValid) surfaceGone = true
                        else emit(gen, "reconnecting", "handshake-closed")
                    }
                    continue
                }
                if (msg.opcode != ScreenMirrorWsClient.OP_BINARY || msg.length < 12 || msg.data[0].toInt() != 0x02) {
                    throw IllegalStateException("Unexpected StreamMeta header")
                }

                if (!renderSurface.isValid || surface !== renderSurface) {
                    surfaceGone = true
                    continue
                }

                val streamW = readU16Le(msg.data, 1)
                val streamH = readU16Le(msg.data, 3)
                if (streamW > 0 && streamH > 0) {
                    videoW = streamW
                    videoH = streamH
                    activity.runOnUiThread {
                        if (running && gen == generation) {
                            applyLayoutLocked(lastLeft, lastTop, lastW, lastH, visible = true)
                        }
                    }
                }
                val codecFlags = msg.data[5].toInt() and 0xFF
                val isHevc = (codecFlags and 1) != 0
                negotiatedHevc = isHevc
                val mime = if (isHevc) "video/hevc" else "video/avc"
                val chosenName = if (isHevc) cap.hevcDecoder else cap.h264Decoder
                val negotiatedAdaptive = if (isHevc) cap.hevcAdaptive else cap.h264Adaptive

                val dec = createDecoder(mime, chosenName)
                val guard = CodecGuard(dec, { it.stop() }, { it.release() })
                ac = guard

                val fmt = MediaFormat.createVideoFormat(mime, streamW, streamH)
                if (negotiatedAdaptive) {
                    fmt.setInteger(MediaFormat.KEY_MAX_WIDTH, maxOf(streamW, 64))
                    fmt.setInteger(MediaFormat.KEY_MAX_HEIGHT, maxOf(streamH, 64))
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    fmt.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                }
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    fmt.setInteger(MediaFormat.KEY_PRIORITY, 0)
                    fmt.setInteger(MediaFormat.KEY_OPERATING_RATE, Short.MAX_VALUE.toInt())
                }
                setVendorInt(fmt, "vendor.qti-ext-dec-low-latency.enable", 1)
                setVendorInt(fmt, "vendor.low-latency.enable", 1)
                setVendorInt(fmt, "vendor.rtc-ext-dec-low-latency.enable", 1)

                var registered = false
                synchronized(gate) {
                    if (alive() && surface === renderSurface && renderSurface.isValid) {
                        activeCodec = guard
                        registered = true
                    }
                }
                if (!registered) {
                    surfaceGone = surface !== renderSurface || !renderSurface.isValid
                    continue
                }

                val configured = guard.withAccess(false) { d ->
                    d.configure(fmt, renderSurface, null, 0)
                    d.start()
                    true
                }
                if (!configured || !alive()) {
                    surfaceGone = surface !== renderSurface || !renderSurface.isValid
                    continue
                }
                client.sendBinary(byteArrayOf(0x10))

                val metaJson = JSONObject()
                    .put("codec", if (isHevc) "hevc" else "h264")
                    .put("width", streamW)
                    .put("height", streamH)
                    .put("route", route.name.lowercase())
                    .put("decoder", chosenName ?: mime)
                    .toString()

                val inFlight = InFlightTracker()
                inFlightRef = inFlight
                val telemetry = DecodeTelemetryWindow(windowMs = 500L, startMs = SystemClock.elapsedRealtime())
                val queueNsMap = ConcurrentHashMap<Long, Long>()
                val outFailed = AtomicBoolean(false)
                outThread = Thread({
                    try {
                        Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY)
                        val info = MediaCodec.BufferInfo()
                        val peekInfo = MediaCodec.BufferInfo()
                        while (alive() && !guard.isStopping()) {
                            val idx = guard.withAccess(-1) { d -> d.dequeueOutputBuffer(info, 5_000) }
                            if (idx < 0) continue

                            var latestPts = info.presentationTimeUs
                            var staleDropped = 0
                            val rendered = guard.withAccess(false) { d ->
                                var latestIdx = idx
                                while (!guard.isStopping()) {
                                    val nextIdx = d.dequeueOutputBuffer(peekInfo, 0)
                                    if (nextIdx >= 0) {
                                        d.releaseOutputBuffer(latestIdx, false)
                                        queueNsMap.remove(latestPts)
                                        staleDropped++
                                        latestIdx = nextIdx
                                        latestPts = peekInfo.presentationTimeUs
                                    } else if (nextIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                                        continue
                                    } else {
                                        break
                                    }
                                }
                                if (guard.isStopping() || !renderSurface.isValid || surface !== renderSurface) {
                                    try { d.releaseOutputBuffer(latestIdx, false) } catch (_: Throwable) {}
                                    false
                                } else {
                                    d.releaseOutputBuffer(latestIdx, true)
                                    true
                                }
                            }
                            inFlight.onOutputReleased(1 + staleDropped)
                            val tRelNs = System.nanoTime()
                            val qNs = queueNsMap.remove(latestPts)
                            if (qNs != null && rendered) {
                                val decMs = (tRelNs - qNs) / 1_000_000.0
                                telemetry.recordDecoded(decMs, inFlight.current())
                            }
                            telemetry.pollPacket(SystemClock.elapsedRealtime())?.let { pkt ->
                                try { client.sendBinary(pkt) } catch (_: Throwable) {}
                            }
                            if (rendered && !renderedFirst.getAndSet(true)) {
                                backoffMs = 250L
                                emit(gen, "connected", metaJson)
                            }
                        }
                    } catch (_: Throwable) {
                        outFailed.set(true)
                        try { client.close() } catch (_: Throwable) {}
                    }
                }, "pb-codec-out").apply { isDaemon = true }
                outThread.start()

                touchQueue.clear()
                touchThread = Thread({
                    Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_DISPLAY)
                    while (alive() && !guard.isStopping() && !outFailed.get()) {
                        val pkt = try { touchQueue.take(250) } catch (_: InterruptedException) { break } ?: continue
                        try { client.sendBinary(pkt) } catch (_: Throwable) { break }
                    }
                }, "pb-touch-tx").apply { isDaemon = true }
                touchThread.start()

                val pingIntervalMs = if (route == AgentLink.Route.WIFI) 1500L else 3000L
                pingThread = Thread({
                    val pingPkt = ByteArray(9).apply { this[0] = 0x20 }
                    while (alive() && !guard.isStopping() && !outFailed.get()) {
                        try {
                            Thread.sleep(pingIntervalMs)
                        } catch (_: InterruptedException) {
                            break
                        }
                        if (!alive() || guard.isStopping() || outFailed.get()) break
                        writeI64Le(pingPkt, 1, System.nanoTime())
                        try { client.sendBinary(pingPkt) } catch (_: Throwable) { break }
                    }
                }, "pb-ws-ping").apply { isDaemon = true }
                pingThread.start()

                val pong = ByteArray(9).apply { this[0] = 0x21 }
                @Suppress("UNUSED_VARIABLE")
                var lastPongNs = 0L
                var needIdr = false
                var lastIdrReqMs = 0L
                fun requestIdrThrottled() {
                    val now = SystemClock.elapsedRealtime()
                    if (now - lastIdrReqMs >= 300L) {
                        lastIdrReqMs = now
                        try { client.sendBinary(byteArrayOf(0x10)) } catch (_: Exception) {}
                    }
                }
                while (alive() && !guard.isStopping() && !outFailed.get() && client.readMessage(msg)) {
                    if (msg.opcode != ScreenMirrorWsClient.OP_BINARY || msg.length < 1) continue
                    when (msg.data[0].toInt() and 0xFF) {
                        0x02 -> if (msg.length >= 12) {
                            val nextW = readU16Le(msg.data, 1)
                            val nextH = readU16Le(msg.data, 3)
                            Log.i("PhoneBoxScreen", "StreamMeta updated: ${nextW}x${nextH}")
                        }
                        0x24 -> if (msg.length >= 2) {
                            setKeyboardVisible(msg.data[1].toInt() != 0)
                        }
                        0x04 -> {
                            val meta = ScreenAudioWire.parseMeta(msg.data, msg.length)
                            if (meta != null && meta.active && meta.codec == ScreenAudioWire.CODEC_PCM16) {
                                val player = audioPlayer ?: ScreenMirrorAudioPlayer().also {
                                    audioPlayer = it
                                    synchronized(gate) {
                                        if (alive()) activeAudio = it
                                    }
                                }
                                player.configure(
                                    meta.sampleRate,
                                    meta.channels,
                                    meta.frameSamples,
                                    isWifi = route == AgentLink.Route.WIFI,
                                )
                            } else {
                                audioPlayer?.stop()
                                synchronized(gate) {
                                    if (activeAudio === audioPlayer) activeAudio = null
                                }
                                audioPlayer = null
                            }
                        }
                        0x03 -> {
                            val payloadLen = ScreenAudioWire.parseFramePayloadLength(msg.data, msg.length)
                            if (payloadLen > 0) {
                                audioPlayer?.enqueue(msg.data, 15, payloadLen)
                            }
                        }
                        0x20 -> if (msg.length >= 9) {
                            System.arraycopy(msg.data, 1, pong, 1, 8)
                            client.sendBinary(pong, 0, 9)
                        }
                        0x21 -> if (msg.length >= 9) {
                            lastPongNs = System.nanoTime()
                        }
                        0x01 -> if (msg.length >= 14) {
                            val payloadLen = readI32Le(msg.data, 1)
                            val ptsUs = readI64Le(msg.data, 5)
                            val flags = msg.data[13].toInt() and 0xFF
                            val isIdr = (flags and 1) != 0
                            if (payloadLen <= 0 || 14 + payloadLen > msg.length) continue
                            if (needIdr && !isIdr) {
                                requestIdrThrottled()
                                continue
                            }
                            val slotWaitMs = if (isIdr) {
                                if (route == AgentLink.Route.WIFI) 20L else 12L
                            } else {
                                if (route == AgentLink.Route.WIFI) 10L else 6L
                            }
                            if (!inFlight.awaitSlot(isIdr, slotWaitMs)) {
                                telemetry.recordDropped()
                                needIdr = true
                                requestIdrThrottled()
                                continue
                            }
                            val handled = guard.withAccess(false) { d ->
                                val inIdx = d.dequeueInputBuffer(20_000)
                                if (inIdx < 0) {
                                    inFlight.cancelReserved()
                                    needIdr = true
                                    requestIdrThrottled()
                                    return@withAccess true
                                }
                                val ib = d.getInputBuffer(inIdx)
                                if (ib != null && ib.capacity() >= payloadLen) {
                                    ib.clear()
                                    ib.put(msg.data, 14, payloadLen)
                                    val codecFlags = if (isIdr) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                                    if (queueNsMap.size > 64) queueNsMap.clear()
                                    queueNsMap[ptsUs] = System.nanoTime()
                                    d.queueInputBuffer(inIdx, 0, payloadLen, ptsUs, codecFlags)
                                    if (isIdr) needIdr = false
                                } else {
                                    inFlight.cancelReserved()
                                    d.queueInputBuffer(inIdx, 0, 0, ptsUs, 0)
                                    needIdr = true
                                    requestIdrThrottled()
                                }
                                true
                            }
                            if (!handled) {
                                inFlight.cancelReserved()
                                break
                            }
                        }
                    }
                }

                if (outFailed.get() && negotiatedHevc && !renderedFirst.get()) {
                    allowHevc = false
                }
                if (alive() && msg.opcode == ScreenMirrorWsClient.OP_CLOSE && msg.closeReason == "native-unavailable") {
                    throw WsFallbackException("native-unavailable")
                }
                if (alive()) {
                    if (surface !== renderSurface || !renderSurface.isValid) {
                        surfaceGone = true
                    } else {
                        emit(gen, "reconnecting", if (outFailed.get()) "decoder-reset" else "stream-closed")
                    }
                }
            } catch (fallback: WsFallbackException) {
                synchronized(gate) {
                    if (gen == generation) running = false
                }
                hideSurfaceUi(gen)
                emitForGen(gen, "fallback", fallback.message ?: "ws-unavailable")
                break
            } catch (inactive: WsSessionInactiveException) {
                if (alive()) {
                    emit(gen, "restarting", inactive.message ?: "session-inactive")
                }
            } catch (t: Throwable) {
                if (negotiatedHevc && !renderedFirst.get()) {
                    allowHevc = false
                }
                if (alive()) {
                    if (surface !== renderSurface || !renderSurface.isValid) {
                        surfaceGone = true
                    } else {
                        Log.w("PhoneBoxScreen", "Native screen mirror error, retrying: ${t.message}")
                        emit(gen, "reconnecting", t.message ?: t.javaClass.simpleName)
                    }
                }
            } finally {
                try { ws?.close() } catch (_: Throwable) {}
                synchronized(gate) {
                    if (activeWs === ws) activeWs = null
                    if (activeCodec === ac) activeCodec = null
                    if (activeAudio === audioPlayer) activeAudio = null
                }
                ac?.requestStop()
                audioPlayer?.stop()
                touchQueue.clear()
                touchThread?.interrupt()
                try { touchThread?.join(250) } catch (_: InterruptedException) {}
                pingThread?.interrupt()
                try { pingThread?.join(250) } catch (_: InterruptedException) {}
                inFlightRef?.reset()
                outThread?.interrupt()
                try { outThread?.join(500) } catch (_: InterruptedException) {}
                ac?.releaseSafely()
                releaseWifiLock()
            }

            if (!alive()) break
            if (surfaceGone) {
                backoffMs = 250L
            } else {
                try {
                    Thread.sleep(backoffMs)
                } catch (_: InterruptedException) {
                    break
                }
                backoffMs = minOf(backoffMs * 2L, 3000L)
            }
        }

        releaseWifiLock()
        if (!isAlive(gen)) hideSurfaceUi(gen)
    }

    private fun acquireWifiLock() {
        synchronized(wifiLockGate) {
            runCatching {
                val current = wifiLock
                if (current != null && current.isHeld) return
                val wm = activity.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
                val mode = wifiLockMode()
                val lock = wm.createWifiLock(mode, "PhoneBox:ScreenMirror").apply {
                    setReferenceCounted(false)
                    acquire()
                }
                wifiLock = lock
            }
        }
    }

    private fun releaseWifiLock() {
        synchronized(wifiLockGate) {
            runCatching {
                val lock = wifiLock
                wifiLock = null
                if (lock != null && lock.isHeld) lock.release()
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun wifiLockMode(): Int =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) WifiManager.WIFI_MODE_FULL_LOW_LATENCY
        else WifiManager.WIFI_MODE_FULL_HIGH_PERF

    private fun writeI64Le(dst: ByteArray, off: Int, v: Long) {
        for (i in 0 until 8) dst[off + i] = ((v ushr (i * 8)) and 0xFFL).toByte()
    }

    private fun hideSurfaceUi(gen: Int) {
        activity.runOnUiThread {
            if (gen == generation || !running) surfaceView.visibility = View.GONE
        }
    }

    private fun createDecoder(mime: String, preferredName: String?): MediaCodec {
        if (!preferredName.isNullOrEmpty()) {
            try { return MediaCodec.createByCodecName(preferredName) } catch (_: Throwable) {}
        }
        return MediaCodec.createDecoderByType(mime)
    }

    private fun setVendorInt(fmt: MediaFormat, key: String, valInt: Int) {
        try { fmt.setInteger(key, valInt) } catch (_: Throwable) {}
    }

    private fun readU16Le(b: ByteArray, o: Int): Int =
        (b[o].toInt() and 0xFF) or ((b[o + 1].toInt() and 0xFF) shl 8)

    private fun readI32Le(b: ByteArray, o: Int): Int =
        (b[o].toInt() and 0xFF) or
            ((b[o + 1].toInt() and 0xFF) shl 8) or
            ((b[o + 2].toInt() and 0xFF) shl 16) or
            ((b[o + 3].toInt() and 0xFF) shl 24)

    private fun readI64Le(b: ByteArray, o: Int): Long {
        var v = 0L
        for (i in 7 downTo 0) v = (v shl 8) or (b[o + i].toLong() and 0xFFL)
        return v
    }
}
