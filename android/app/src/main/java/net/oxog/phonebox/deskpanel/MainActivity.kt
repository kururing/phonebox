package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.annotation.TargetApi
import android.app.Activity
import android.app.ActivityManager
import android.app.AlertDialog
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.res.Configuration
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.media.AudioManager
import android.os.BatteryManager
import android.os.SystemClock
import android.os.Bundle
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.text.format.DateFormat
import android.util.Log
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import android.widget.Toast
import android.webkit.JavascriptInterface
import android.webkit.ConsoleMessage
import android.webkit.RenderProcessGoneDetail
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebChromeClient
import android.webkit.WebViewClient
import android.window.OnBackInvokedCallback
import android.window.OnBackInvokedDispatcher
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONArray
import org.json.JSONObject
import java.util.Locale

/**
 * Full-screen desk panel. The UI is plain HTML/JS bundled in assets/www.
 * The UI calls "/agent/..." URLs; they are intercepted here and forwarded to the PhoneBox agent on the PC,
 * over USB (adb reverse) or, once paired, over Wi-Fi / Tailscale (see AgentLink). The WebView itself therefore
 * never makes local-network requests (no CORS, mixed-content or local-network-access issues).
 */
class MainActivity : Activity(), SensorEventListener {

    private lateinit var rootContainer: FrameLayout
    private lateinit var web: WebView
    private lateinit var screenMirror: ScreenMirrorPlayer
    /** False when Android could not start its WebView (#385): the activity then only shows why. */
    private val hasWeb get() = ::web.isInitialized
    private val panelLanguageTag = Regex("auto|[a-z]{2}(-[A-Za-z]{2,4})?")
    private val panelPreferences by lazy { getSharedPreferences("panel-ui", MODE_PRIVATE) }

    private fun pairingContext(): Context {
        val tag = panelPreferences.getString("language", null) ?: return this
        return createConfigurationContext(Configuration(resources.configuration).apply {
            setLocale(Locale.forLanguageTag(tag))
        })
    }
    // Set first in onDestroy: dictation and stream callbacks already queued on the UI thread then skip the destroyed WebView.
    private var destroyed = false
    private val dictation by lazy { Dictation(this) { script -> if (!destroyed) web.evaluateJavascript(script, null) } }
    private lateinit var link: AgentLink
    private lateinit var stream: AgentStream
    @Volatile private var resumed = false
    @Volatile private var volumeKeysFlipPages = true
    @Volatile private var mirrorLocked = false
    private var volUpDown = false
    private var volDownDown = false
    private var volChordFired = false
    private var volChordExitRunnable: Runnable? = null
    private var lastVolUpTapMs = 0L
    private var lastVolDownTapMs = 0L

    private fun exitPhoneBoxApp() {
        runOnUiThread {
            try {
                val am = getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
                val pinned = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) am.lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE
                    else @Suppress("DEPRECATION") am.isInLockTaskMode
                if (pinned) stopLockTask()
            } catch (_: Exception) {}
            finishAndRemoveTask()
        }
    }
    private val sensors by lazy { getSystemService(Context.SENSOR_SERVICE) as SensorManager? }
    private var lastLux = -1f
    private var lastLuxAt = 0L
    private var near: Boolean? = null

    private val assetLoader by lazy {
        WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING)
        link = AgentLink(this)
        stream = AgentStream(link) { event, data ->
            val js = "window.onAgentStream && window.onAgentStream(${JSONObject.quote(event)}, ${JSONObject.quote(data)})"
            runOnUiThread { if (!destroyed) web.evaluateJavascript(js, null) }
        }

        rootContainer = FrameLayout(this)
        web = try { WebView(this) } catch (e: Exception) {
            // Some Android 5.1 vendor systems cannot start any WebView update ("Invalid reflection", #385): say so
            // instead of crashing at every launch.
            Log.e("PhoneBox", "WebView failed to start", e)
            setContentView(TextView(this).apply {
                text = getString(R.string.webview_failed)
                setTextColor(0xFFFFFFFF.toInt())
                setBackgroundColor(0xFF000000.toInt())
                textSize = 20f
                gravity = Gravity.CENTER
                setPadding(48, 48, 48, 48)
            })
            return
        }
        web.setBackgroundColor(0xFF000000.toInt())
        rootContainer.addView(web, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        screenMirror = ScreenMirrorPlayer(this, rootContainer, link) { event, detail ->
            val js = "window.onNativeScreenMirror && window.onNativeScreenMirror(${JSONObject.quote(event)}, ${JSONObject.quote(detail)})"
            runOnUiThread { if (!destroyed) web.evaluateJavascript(js, null) }
        }
        setContentView(rootContainer)

        with(web.settings) {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = false
            cacheMode = WebSettings.LOAD_NO_CACHE
            textZoom = 100
        }
        web.isVerticalScrollBarEnabled = false
        web.isHorizontalScrollBarEnabled = false
        web.webChromeClient = object : WebChromeClient() {
            override fun onConsoleMessage(message: ConsoleMessage): Boolean {
                if (message.messageLevel() == ConsoleMessage.MessageLevel.ERROR)
                    Log.e("PhoneBoxWeb", "${message.sourceId()}:${message.lineNumber()} ${message.message()}")
                return true
            }
        }
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val url = request.url
                if (url.host == ASSET_HOST && url.path?.startsWith("/agent/") == true)
                    return if (fromPanel(request)) proxy(request) else FORBIDDEN()
                val r = assetLoader.shouldInterceptRequest(url)
                // the panel is made of ES modules, which need a JavaScript MIME type
                if (r != null && url.path?.endsWith(".js") == true) r.mimeType = "text/javascript"
                return r
            }

            // Old WebView without document-start scripts: hand the bridge key to the main frame only (evaluateJavascript
            // never runs in iframes), and only when it shows the panel. Bridge calls before it arrives get nothing.
            override fun onPageStarted(view: WebView, url: String?, favicon: android.graphics.Bitmap?) = giveKey(view, url)
            override fun onPageFinished(view: WebView, url: String?) = giveKey(view, url)

            @TargetApi(Build.VERSION_CODES.O)
            override fun onRenderProcessGone(view: WebView, detail: RenderProcessGoneDetail): Boolean {
                if (destroyed || isFinishing) return true
                Log.e("PhoneBoxWeb", "WebView renderer gone (didCrash=${detail.didCrash()}); recreating")
                screenMirror.stop()
                (view.parent as? ViewGroup)?.removeView(view)
                try { view.destroy() } catch (_: Throwable) {}
                recreate()
                return true
            }
        }
        if (documentStartKey)
            WebViewCompat.addDocumentStartJavaScript(web, panelBootstrap(), setOf("https://$ASSET_HOST"))
        web.addJavascriptInterface(Bridge(), "Android")
        // Lets you debug the panel from the computer via chrome://inspect.
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)

        web.loadUrl("https://$ASSET_HOST/assets/www/index.html")
        hideSystemUi()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) registerBackCallback()
        handlePairingLink(intent)
    }

    @TargetApi(Build.VERSION_CODES.TIRAMISU)
    private fun registerBackCallback() {
        onBackInvokedDispatcher.registerOnBackInvokedCallback(
            OnBackInvokedDispatcher.PRIORITY_DEFAULT,
            OnBackInvokedCallback { handleBack() },
        )
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handlePairingLink(intent)
    }

    /** A pairing link from the computer's QR code (Settings › Wi-Fi › Pair a phone over Wi-Fi): ask, then pair. */
    private fun handlePairingLink(intent: Intent?) {
        val pairing = AgentLink.PairLink.parse(intent?.data) ?: return
        intent?.data = null // not again after a configuration change
        val context = pairingContext()
        AlertDialog.Builder(this)
            .setTitle(context.getString(R.string.pair_title))
            .setMessage(context.getString(R.string.pair_message, pairing.pc, pairing.hosts.joinToString(), pairing.port, pairing.check))
            .setPositiveButton(context.getString(R.string.pair_positive)) { _, _ -> pair(pairing, context) }
            .setNegativeButton(context.getString(R.string.pair_negative), null)
            .setOnDismissListener { hideSystemUi() }
            .show()
            .pairingButtons(context)
    }

    /** Dialog buttons as written: the default all-caps follows the phone's language ("PAİR" on a Turkish phone). */
    private fun AlertDialog.pairingButtons(context: Context): AlertDialog = apply {
        window?.decorView?.layoutDirection = context.resources.configuration.layoutDirection
        listOf(AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE).forEach { getButton(it)?.isAllCaps = false }
    }

    private fun pair(pairing: AgentLink.PairLink, context: Context) {
        Toast.makeText(context, context.getString(R.string.pair_progress, pairing.pc), Toast.LENGTH_SHORT).show()
        Thread {
            val problem = link.claim(pairing)
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread // the pairing is stored; there is no window to show it in
                if (problem == null) {
                    Toast.makeText(context, context.getString(R.string.pair_done, pairing.pc), Toast.LENGTH_LONG).show()
                    stream.stop(); if (resumed) stream.start(); if (hasWeb) web.reload()
                } else {
                    AlertDialog.Builder(this).setTitle(context.getString(R.string.pair_failed_title)).setMessage(problem.message(context))
                        .setPositiveButton(context.getString(R.string.ok), null).setOnDismissListener { hideSystemUi() }.show().pairingButtons(context)
                }
            }
        }.start()
    }

    /**
     * Only the panel itself may reach the agent. Every frame of the WebView comes through here, including the sandboxed
     * iframe of a Custom HTML widget, which is meant to talk to the panel through postMessage only. The panel sends
     * X-PhoneBox-Panel on its requests; a sandboxed frame cannot add it without a CORS preflight, which is refused.
     * Images (album art, process icons) cannot carry headers, but they are read-only GETs.
     */
    private fun fromPanel(req: WebResourceRequest): Boolean =
        req.requestHeaders.keys.any { it.equals(PANEL_HEADER, ignoreCase = true) } ||
            (req.method == "GET" && req.url.path in READ_ONLY_ASSETS)

    private fun proxy(req: WebResourceRequest): WebResourceResponse {
        val path = req.url.encodedPath!!.removePrefix("/agent")
        val query = req.url.encodedQuery?.let { "?$it" } ?: ""
        val r = if (req.method == "GET" && path == "/api/screen/h264") {
            val streamReply = link.stream(path + query, readTimeout = 0)
            if (streamReply == null) {
                Log.e("PhoneBoxScreen", "H.264 stream could not connect over USB or Wi-Fi")
                AgentLink.Reply(503, "text/plain", java.io.ByteArrayInputStream("PhoneBox Agent unreachable".toByteArray()))
            } else {
                Log.i("PhoneBoxScreen", "H.264 reply over ${streamReply.first}: HTTP ${streamReply.second.code}, ${streamReply.second.contentType}")
                streamReply.second
            }
        } else link.request(req.method, path + query)
        val ct = r.contentType ?: "application/octet-stream"
        val mime = ct.substringBefore(';').trim()
        val charset = if (ct.contains("charset=")) ct.substringAfter("charset=").trim() else null
        val status = if (r.code in 100..599 && r.code !in 300..399) r.code else 502
        return WebResourceResponse(mime, charset, status, if (status < 400) "OK" else "Error", NO_STORE + ("Content-Type" to ct), r.body)
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.setDecorFitsSystemWindows(false)
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_FULLSCREEN
        }
    }

    /** Volume up / down flip to the previous / next page (unless turned off in the settings).
     *  In mirror mode: volume change is completely blocked; Vol+ & Vol- together takes a PC screenshot,
     *  and double-pressing Vol+ or Vol- switches pages. */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val isVolUp = event.keyCode == KeyEvent.KEYCODE_VOLUME_UP
        val isVolDown = event.keyCode == KeyEvent.KEYCODE_VOLUME_DOWN
        if (!isVolUp && !isVolDown) return super.dispatchKeyEvent(event)

        if (mirrorLocked) {
            if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0) {
                if (isVolUp) volUpDown = true else volDownDown = true
                if (volUpDown && volDownDown) {
                    volChordFired = true
                    lastVolUpTapMs = 0L
                    lastVolDownTapMs = 0L
                    volChordExitRunnable?.let { rootContainer.removeCallbacks(it) }
                    val r = Runnable {
                        if (volUpDown && volDownDown) {
                            volChordExitRunnable = null
                            exitPhoneBoxApp()
                        }
                    }
                    volChordExitRunnable = r
                    rootContainer.postDelayed(r, 1500L)
                    return true
                }
                val now = SystemClock.elapsedRealtime()
                if (isVolUp) {
                    if (now - lastVolUpTapMs <= 400L) {
                        lastVolUpTapMs = 0L
                        web.evaluateJavascript("window.onAndroidKey && window.onAndroidKey('prev', true)", null)
                    } else {
                        lastVolUpTapMs = now
                    }
                } else {
                    if (now - lastVolDownTapMs <= 400L) {
                        lastVolDownTapMs = 0L
                        web.evaluateJavascript("window.onAndroidKey && window.onAndroidKey('next', true)", null)
                    } else {
                        lastVolDownTapMs = now
                    }
                }
            } else if (event.action == KeyEvent.ACTION_UP) {
                val wasChord = volChordFired && volChordExitRunnable != null
                volChordExitRunnable?.let { rootContainer.removeCallbacks(it) }
                volChordExitRunnable = null
                if (isVolUp) volUpDown = false else volDownDown = false
                if (wasChord && (!volUpDown || !volDownDown)) {
                    Thread({ try { link.request("POST", "/api/screen/key?key=prtsc") } catch (_: Throwable) {} }, "pb-prtsc").start()
                }
                if (!volUpDown && !volDownDown) volChordFired = false
            }
            return true
        }
        // Outside mirror mode: holding Vol+ & Vol- together for 1.5s also unpins and exits PhoneBox.
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0) {
            if (isVolUp) volUpDown = true else volDownDown = true
            if (volUpDown && volDownDown) {
                volChordExitRunnable?.let { rootContainer.removeCallbacks(it) }
                val r = Runnable {
                    if (volUpDown && volDownDown) {
                        volChordExitRunnable = null
                        exitPhoneBoxApp()
                    }
                }
                volChordExitRunnable = r
                rootContainer.postDelayed(r, 1500L)
                return true
            }
        } else if (event.action == KeyEvent.ACTION_UP) {
            volChordExitRunnable?.let { rootContainer.removeCallbacks(it) }
            volChordExitRunnable = null
            if (isVolUp) volUpDown = false else volDownDown = false
        }

        if (!volumeKeysFlipPages || !hasWeb) return super.dispatchKeyEvent(event)
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0) {
            val key = if (isVolUp) "prev" else "next"
            web.evaluateJavascript("window.onAndroidKey && window.onAndroidKey('$key')", null)
        }
        return true
    }

    private fun handleBack() {
        if (destroyed) return
        if (screenMirror.dismissKeyboardIfOpen()) return
        if (mirrorLocked) return
        web.evaluateJavascript("window.onAndroidBack && window.onAndroidBack()", null)
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        // Kiosk behaviour: Back does not leave the app, it returns from mirror mode or to the first page.
        if (!hasWeb) return super.onBackPressed() // nothing to return to without the panel
        handleBack()
    }

    override fun onResume() {
        super.onResume()
        if (!hasWeb) return
        resumed = true
        web.onResume()
        web.evaluateJavascript("window.onAppPause && window.onAppPause(false)", null) // the panel catches up at once
        stream.start()
        hideSystemUi()
        sensors?.getDefaultSensor(Sensor.TYPE_LIGHT)?.let { sensors?.registerListener(this, it, SensorManager.SENSOR_DELAY_NORMAL) }
        sensors?.getDefaultSensor(Sensor.TYPE_PROXIMITY)?.let { sensors?.registerListener(this, it, SensorManager.SENSOR_DELAY_NORMAL) }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == Dictation.PERMISSION_REQUEST)
            dictation.onPermission(grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED)
    }

    override fun onPause() {
        resumed = false
        dictation.cancel(0) // the microphone is never left open behind another app or a dark screen
        sensors?.unregisterListener(this)
        if (::screenMirror.isInitialized) screenMirror.stop()
        stream.stop()
        // WebView.onPause() leaves JavaScript running: tell the panel to stop polling and refreshing while nobody sees it
        if (hasWeb) {
            web.evaluateJavascript("window.onAppPause && window.onAppPause(true)", null)
            web.onPause()
        }
        super.onPause()
    }

    override fun onDestroy() {
        destroyed = true
        resumed = false
        dictation.cancel(0)
        sensors?.unregisterListener(this)
        if (::screenMirror.isInitialized) screenMirror.stop()
        stream.stop()
        try {
            val am = getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
            val pinned = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) am.lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE
                else @Suppress("DEPRECATION") am.isInLockTaskMode
            if (pinned) stopLockTask()
        } catch (_: Exception) {}
        link.trackpadSocket.close()
        link.close()
        if (hasWeb) {
            (web.parent as? ViewGroup)?.removeView(web) // a WebView must be detached before destroy()
            try { web.destroy() } catch (_: Throwable) {}
        }
        super.onDestroy()
    }

    /**
     * Room light (lux) and proximity for the panel: brightness that follows the room, the night screen when the room
     * goes dark, and waking the night screen with a wave of the hand. Light is passed on when it changes noticeably.
     */
    override fun onSensorChanged(e: SensorEvent) {
        when (e.sensor.type) {
            Sensor.TYPE_LIGHT -> {
                val lux = e.values[0]
                val now = SystemClock.elapsedRealtime()
                val jump = lastLux < 0 || kotlin.math.abs(lux - lastLux) > maxOf(3f, lastLux * 0.15f)
                if (!jump && now - lastLuxAt < 10_000) return
                if (now - lastLuxAt < 700) return
                lastLux = lux; lastLuxAt = now
                web.evaluateJavascript("window.onAndroidSensor && window.onAndroidSensor('light', $lux)", null)
            }
            Sensor.TYPE_PROXIMITY -> {
                val isNear = e.values[0] < minOf(5f, e.sensor.maximumRange)
                if (isNear == near) return
                near = isNear
                web.evaluateJavascript("window.onAndroidSensor && window.onAndroidSensor('proximity', $isNear)", null)
            }
        }
    }

    override fun onAccuracyChanged(sensor: Sensor, accuracy: Int) {}

    /**
     * Key for the bridge: addJavascriptInterface puts window.Android into EVERY frame of the WebView, including the
     * sandboxed iframe of a Custom HTML widget. Each call therefore carries this key, which only frames of the panel's
     * own origin receive (a document-start script with an origin rule; the sandboxed iframe has an opaque origin).
     * Calls without it do nothing (fail closed). A WebView without document-start scripts gets the key through
     * evaluateJavascript, which only reaches the main frame (see giveKey).
     */
    private val bridgeKey = java.util.UUID.randomUUID().toString()
    private val documentStartKey by lazy { WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT) }

    private fun panelBootstrap(): String {
        val configuration = resources.configuration
        val languages = JSONArray()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            val locales = configuration.locales
            for (index in 0 until locales.size()) languages.put(locales[index].toLanguageTag())
        } else {
            @Suppress("DEPRECATION")
            languages.put(configuration.locale.toLanguageTag())
        }
        return "window.__phoneboxKey='$bridgeKey';window.__phoneboxLanguages=$languages;"
    }

    private fun giveKey(view: WebView, url: String?) {
        if (!documentStartKey && url?.startsWith("https://$ASSET_HOST/") == true)
            view.evaluateJavascript(panelBootstrap(), null)
    }

    inner class Bridge {
        private fun ok(key: String) = key == bridgeKey

        private val validTrackpadTypes = setOf("move", "click", "rightClick", "mousedown", "mouseup", "scroll", "hscroll") +
            // multi-finger gestures: three or four fingers swiping, four or five spreading or pinching (the agent follows the Mac's settings)
            listOf("swipe3up", "swipe3down", "swipe3left", "swipe3right", "swipe4up", "swipe4down", "swipe4left", "swipe4right", "spread", "pinch")

        @JavascriptInterface
        fun trackpad(key: String, type: String, dx: Float, dy: Float, delta: Float) {
            if (!ok(key) || type !in validTrackpadTypes) return
            val cleanDx = if (dx.isFinite()) dx else 0f
            val cleanDy = if (dy.isFinite()) dy else 0f
            val cleanDelta = if (delta.isFinite()) delta else 0f
            link.sendTrackpad(type, cleanDx, cleanDy, cleanDelta)
        }

        // The WebView receives the saved agent preference. Remember it for pairing dialogs before the next connection.
        @JavascriptInterface
        fun setLanguage(key: String, tag: String) {
            if (!ok(key) || !panelLanguageTag.matches(tag)) return
            val language = when (tag) {
                "auto" -> null
                "zh" -> "zh-CN"
                "zh-Hant" -> "zh-TW"
                "pt" -> "pt-PT"
                else -> tag
            }
            if (panelPreferences.getString("language", null) != language)
                panelPreferences.edit().putString("language", language).apply()
        }

        /** Screen brightness 0..1; a negative value returns control to the system setting. */
        @JavascriptInterface
        fun setBrightness(key: String, value: Float) {
            if (!ok(key)) return
            runOnUiThread {
                val lp = window.attributes
                lp.screenBrightness =
                    if (value < 0f) WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE else value.coerceIn(0.01f, 1f)
                window.attributes = lp
            }
        }

        @JavascriptInterface
        fun haptic(key: String) {
            if (!ok(key)) return
            val v = getSystemService(Context.VIBRATOR_SERVICE) as Vibrator? ?: return
            if (!v.hasVibrator()) return
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) v.vibrate(VibrationEffect.createOneShot(14, VibrationEffect.DEFAULT_AMPLITUDE))
            else @Suppress("DEPRECATION") v.vibrate(14)
        }

        /** Notification vibration; pattern is a "wait,vibrate,wait,vibrate..." list of milliseconds. */
        @JavascriptInterface
        fun buzz(key: String, pattern: String) {
            if (!ok(key)) return
            val v = getSystemService(Context.VIBRATOR_SERVICE) as Vibrator? ?: return
            val timings = pattern.split(',').mapNotNull { it.trim().toLongOrNull() }.toLongArray()
            if (!v.hasVibrator() || timings.isEmpty()) return
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) v.vibrate(VibrationEffect.createWaveform(timings, -1))
            else @Suppress("DEPRECATION") v.vibrate(timings, -1)
        }

        @JavascriptInterface
        fun battery(key: String): String {
            if (!ok(key)) return "{}"
            val i = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val level = i?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
            val scale = i?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
            val pluggedType = i?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
            val temp = (i?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0) / 10.0
            val source = when (pluggedType) {
                BatteryManager.BATTERY_PLUGGED_USB -> "usb"
                BatteryManager.BATTERY_PLUGGED_AC -> "ac"
                BatteryManager.BATTERY_PLUGGED_WIRELESS -> "wireless"
                else -> ""
            }
            // The phone's own view of its USB data link (a sticky system broadcast): lets the agent tell a cable that
            // only charges from a host that lost the phone.
            val usb = try {
                registerReceiver(null, IntentFilter("android.hardware.usb.action.USB_STATE"))?.let { u ->
                    JSONObject()
                        .put("connected", u.getBooleanExtra("connected", false))
                        .put("configured", u.getBooleanExtra("configured", false))
                        .put("adb", u.getBooleanExtra("adb", false))
                }
            } catch (_: Exception) { null }
            return JSONObject()
                .put("level", if (level >= 0) level * 100 / scale else -1)
                .put("plugged", pluggedType != 0)
                .put("temp", temp)
                .put("source", source)
                .put("usb", usb ?: JSONObject.NULL)
                .toString()
        }

        /** "usb", "wifi" or "none": how the last agent request was delivered. */
        @JavascriptInterface
        fun transport(key: String): String = if (ok(key)) link.route.name.lowercase() else ""

        /** Whether the volume keys flip pages (true) or change the phone's volume (false). */
        @JavascriptInterface
        fun setVolumeKeys(key: String, enabled: Boolean) { if (ok(key)) volumeKeysFlipPages = enabled }

        /** Whether the phone is set to 24-hour time (Settings › System › Date & time). */
        @JavascriptInterface
        fun is24Hour(key: String): Boolean = if (ok(key)) DateFormat.is24HourFormat(this@MainActivity) else true

        /** Which sensors the phone has: {"light": true, "proximity": true}. */
        @JavascriptInterface
        fun sensors(key: String): String = if (!ok(key)) "{}" else JSONObject()
            .put("light", sensors?.getDefaultSensor(Sensor.TYPE_LIGHT) != null)
            .put("proximity", sensors?.getDefaultSensor(Sensor.TYPE_PROXIMITY) != null)
            .toString()

        /**
         * Screen pinning (lock task without a device owner): Home and Recents stay on the panel until it is unpinned
         * (Back + Overview held together, or turning the setting off). Android asks once to confirm.
         */
        @JavascriptInterface
        fun setPinned(key: String, on: Boolean) {
            if (!ok(key)) return
            runOnUiThread {
                val am = getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
                val pinned = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) am.lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE
                    else @Suppress("DEPRECATION") am.isInLockTaskMode
                try { if (on && !pinned) startLockTask() else if (!on && pinned) stopLockTask() } catch (_: Exception) {}
            }
        }

        /** The computers this phone is paired with (for switching over Wi-Fi). */
        @JavascriptInterface
        fun pcs(key: String): String = if (ok(key)) link.pcsJson() else "[]"

        /** Switches to another paired computer and reloads the panel from it. */
        @JavascriptInterface
        fun selectPc(key: String, id: String) {
            if (!ok(key) || !link.select(id)) return
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread
                stream.stop(); if (resumed) stream.start(); if (hasWeb) web.reload()
            }
        }

        @JavascriptInterface
        fun forgetPc(key: String, id: String) { if (ok(key)) link.forget(id) }

        /** True when the Wake button can work: the active computer's MACs are known and the phone is on a local network. */
        @JavascriptInterface
        fun canWake(key: String): Boolean = ok(key) && link.canWake()

        /**
         * Sends a Wake-on-LAN packet to the paired computer [id]. Takes a computer's id and never a MAC address, so a
         * page cannot aim the packet at an arbitrary machine. False when nothing could be sent.
         */
        @JavascriptInterface
        fun wakePc(key: String, id: String): Boolean = ok(key) && link.wake(id)

        /** "landscape" (default), "portrait" or "auto" (follow the phone). */
        @JavascriptInterface
        fun setOrientation(key: String, mode: String) {
            if (!ok(key)) return
            runOnUiThread {
                val want = when (mode) {
                    "portrait" -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                    "auto" -> ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR
                    else -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                }
                if (requestedOrientation != want) requestedOrientation = want
            }
        }

        /** Offers the panel as a home app (see HomeAlias in the manifest); the agent can then make it the default. */
        @JavascriptInterface
        fun setHomeScreen(key: String, on: Boolean) {
            if (!ok(key)) return
            val alias = ComponentName(this@MainActivity, "$packageName.HomeAlias")
            val want = if (on) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED
            if (packageManager.getComponentEnabledSetting(alias) != want)
                packageManager.setComponentEnabledSetting(alias, want, PackageManager.DONT_KILL_APP)
        }

        @JavascriptInterface
        fun version(key: String): String = if (ok(key)) packageManager.getPackageInfo(packageName, 0).versionName ?: "" else ""

        /** Native pixel size of the phone display in its current orientation. */
        @JavascriptInterface
        fun screenSize(key: String): String {
            if (!ok(key)) return "{}"
            val metrics = resources.displayMetrics
            return JSONObject().put("width", metrics.widthPixels).put("height", metrics.heightPixels).toString()
        }

        /** Dictation widget: true when the phone has a speech recognizer. */
        @JavascriptInterface
        fun canListen(key: String): Boolean = ok(key) && dictation.available()

        /**
         * Dictation widget: listens once ([lang] a BCP 47 tag or "") and returns the session's id; results arrive
         * through window.onAndroidSpeech(kind, text, id).
         */
        @JavascriptInterface
        fun listen(key: String, lang: String, offline: Boolean): Int =
            if (!ok(key)) 0 else dictation.start(lang.take(20).filter { it.isLetterOrDigit() || it == '-' }, offline)

        @JavascriptInterface
        fun stopListening(key: String, id: Int) { if (ok(key) && id > 0) dictation.stop(id) }

        @JavascriptInterface
        fun cancelListening(key: String, id: Int) { if (ok(key) && id > 0) dictation.cancel(id) }

        @JavascriptInterface
        fun startScreenMirror(
            key: String,
            left: Int,
            top: Int,
            width: Int,
            height: Int,
            preferHevc: Boolean,
            audioMode: String,
            audioDevice: String,
            audioAutoSwitch: Boolean,
            fitMode: String,
            targetFps: Int,
        ) {
            if (!ok(key) || destroyed) return
            screenMirror.start(
                left,
                top,
                width,
                height,
                preferHevc,
                audioMode = audioMode,
                audioDevice = audioDevice,
                audioAutoSwitch = audioAutoSwitch,
                fitMode = fitMode,
                targetFps = targetFps,
            )
        }

        @JavascriptInterface
        fun updateScreenMirrorRect(key: String, left: Int, top: Int, width: Int, height: Int) {
            if (!ok(key) || destroyed) return
            screenMirror.updateRect(left, top, width, height)
        }

        @JavascriptInterface
        fun stopScreenMirror(key: String) {
            if (!ok(key) || destroyed) return
            screenMirror.stop()
        }

        @JavascriptInterface
        fun screenMirrorCapabilities(key: String): String =
            if (ok(key)) screenMirror.capabilitiesJson() else "{}"

        @JavascriptInterface
        fun exitApp(key: String) {
            if (!ok(key) || destroyed) return
            exitPhoneBoxApp()
        }

        @JavascriptInterface
        fun setMirrorLocked(key: String, locked: Boolean) {
            if (!ok(key) || destroyed) return
            mirrorLocked = locked
            runOnUiThread {
                if (!destroyed) {
                    volumeControlStream = if (locked) AudioManager.STREAM_MUSIC else AudioManager.USE_DEFAULT_STREAM_TYPE
                    hideSystemUi()
                    if (locked) {
                        val am = getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
                        val pinned = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) am.lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE
                            else @Suppress("DEPRECATION") am.isInLockTaskMode
                        try { if (!pinned) startLockTask() } catch (_: Exception) {}
                    }
                }
            }
        }
    }

    companion object {
        private const val ASSET_HOST = "appassets.androidplatform.net"
        private val NO_STORE = mapOf("Cache-Control" to "no-store")
        private const val PANEL_HEADER = "X-PhoneBox-Panel"
        private val READ_ONLY_ASSETS = setOf("/agent/api/art", "/agent/api/procs/icon", "/agent/api/controls/radio-logo", "/agent/api/apps/icon", "/agent/api/discord/img", "/agent/api/shortcuts/icon",
            "/agent/api/steam/cover", "/agent/api/screen/h264")
        private fun FORBIDDEN() = WebResourceResponse("text/plain", "utf-8", 403, "Forbidden", NO_STORE, java.io.ByteArrayInputStream(ByteArray(0)))
    }
}
