package com.example.hdmicaptain

import android.Manifest
import android.app.PictureInPictureParams
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.provider.Settings
import android.util.Log
import android.util.Rational
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.lifecycleScope
import com.example.hdmicaptain.billing.ProFeature
import com.example.hdmicaptain.billing.ProStore
import com.example.hdmicaptain.capture.ViewerPhase
import com.example.hdmicaptain.model.DevicePreset
import com.example.hdmicaptain.model.FormatPreference
import com.example.hdmicaptain.model.TestPattern
import com.example.hdmicaptain.settings.SettingsActions
import com.example.hdmicaptain.settings.SettingsScreen
import com.example.hdmicaptain.settings.SettingsState
import com.example.hdmicaptain.viewer.Feedback
import com.example.hdmicaptain.viewer.HdmiCaptainTheme
import com.example.hdmicaptain.viewer.PermissionPhase
import com.example.hdmicaptain.viewer.UpgradeSheet
import com.example.hdmicaptain.viewer.ViewerHost
import com.example.hdmicaptain.viewer.ViewerModel
import com.example.hdmicaptain.viewer.ViewerScreen
import kotlinx.coroutines.launch
import java.io.File

class MainActivity : ComponentActivity() {
    private val graph by lazy { CaptureGraph(applicationContext, lifecycleScope) }
    private val model by lazy {
        ViewerModel(applicationContext, lifecycleScope, graph.renderer, graph.controller, graph.audio, graph.catalog.cards, graph.prefs)
    }

    // Product ID = <application id>.pro, as on Apple (ADR 0004).
    private val pro by lazy { ProStore(applicationContext, lifecycleScope, "${BuildConfig.APPLICATION_ID}.pro") }

    private var permission by mutableStateOf<PermissionPhase?>(null)
    private var inPip by mutableStateOf(false)
    private var showSettings by mutableStateOf(false)
    private var captureStarted = false

    // UVC cards need CAMERA (Android 9+) before the USB grant; RECORD_AUDIO for the card's sound.
    private val capturePermissions = arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO)
    private val permissionRequest = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (hasCapturePermissions()) startCapture() else permission = PermissionPhase.Denied
    }

    // Settings › LUT: any file (many pickers don't know a .cube MIME type); the parser checks it.
    private val lutPicker = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@registerForActivityResult
        runCatching { contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
        val name = contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        }?.substringBeforeLast(".")
        model.setLut(uri.toString(), name ?: "LUT")
    }

    private val host = object : ViewerHost {
        override fun continueFromWelcome() {
            if (hasCapturePermissions()) startCapture() else permissionRequest.launch(capturePermissions)
        }
        override fun openAppSettings() {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
        }
        override fun retry() { graph.controller.retry() }
        override fun showHelp() = Unit  // handled inside ViewerScreen
        override fun record() {
            if (pro.gate(ProFeature.Recording)) model.show(Feedback("Recording isn't available on Android yet"))
        }
        override fun saveRecent() {
            if (pro.gate(ProFeature.ReplayBuffer)) model.show(Feedback("Save Recent isn't available on Android yet"))
        }
        override fun pictureInPicture() = enterPip()
        override fun openSettings() {
            showSettings = true
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        immersive()
        // DEBUG launch extras mirror the Apple QA arguments: --es testPattern smpteBars, --ez qaShowStats true.
        val pattern = TestPattern.fromKey(intent.getStringExtra("testPattern"))
        pro.start()
        if (BuildConfig.DEBUG) {
            pro.debugUnlock = intent.getBooleanExtra("debugUnlockPro", false)
            if (intent.getStringExtra("qaAction") == "upgrade") pro.requestUpgrade()
            if (intent.hasExtra("qaShowStats")) model.setShowDiagnostics(intent.getBooleanExtra("qaShowStats", false))
            model.pinControls = intent.getBooleanExtra("qaPinControls", false)
        }
        when {
            pattern != null -> model.setTestPattern(pattern)
            hasCapturePermissions() -> startCapture()
            else -> permission = PermissionPhase.Welcome  // explain before the system asks
        }
        setContent {
            HdmiCaptainTheme {
                val isPro by pro.isPro.collectAsState()
                ViewerScreen(model, host, permission, inPictureInPicture = inPip, isPro = isPro)
                if (showSettings) Settings(onDone = { showSettings = false })
                val upgrade by pro.upgradeRequest.collectAsState()
                upgrade?.let { request ->
                    val price by pro.price.collectAsState()
                    val state by pro.purchaseState.collectAsState()
                    UpgradeSheet(isPro, request.feature, price, state,
                        onBuy = { pro.purchase(this@MainActivity) }, onRestore = { pro.restore() }, onDismiss = pro::dismissUpgrade)
                }
            }
        }
        lifecycleScope.launch {
            graph.controller.phase.collect { phase ->
                val live = phase is ViewerPhase.Live
                // Never let the screen sleep while the picture is live.
                if (live) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                updatePipParams(phase)
            }
        }

        val soakMinutes = intent.getIntExtra("qaSoakMinutes", 0)
        if (BuildConfig.DEBUG && soakMinutes > 0) QASoak(this, graph).start(lifecycleScope, soakMinutes)
        if (BuildConfig.DEBUG && intent.getStringExtra("qaAction") == "screenshot") {
            window.decorView.postDelayed({ saveQAScreenshot() }, 1500)
        }
    }

    private fun startCapture() {
        permission = null
        if (captureStarted) return
        captureStarted = true
        lifecycleScope.launch {
            // Come back to the card used last time (spec §12.2).
            graph.controller.preferredKey = graph.prefs.appPrefs().lastCardKey
            graph.controller.start()
        }
        // Capture follows visibility (STARTED/STOPPED), not focus: split screen and PiP keep it live.
        lifecycle.addObserver(LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> graph.controller.onVisible()
                Lifecycle.Event.ON_STOP -> graph.controller.onHidden()
                else -> Unit
            }
        })
    }

    override fun onResume() {
        super.onResume()
        lifecycleScope.launch { runCatching { pro.refresh() } }  // refunds and family changes
        // Coming back from system Settings after allowing Camera.
        if (permission == PermissionPhase.Denied && hasCapturePermissions()) startCapture()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        graph.catalog.refresh()  // launched again by a USB attach
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        inPip = isInPictureInPictureMode
    }

    override fun onDestroy() {
        graph.release()
        super.onDestroy()
    }

    /** Video edge to edge; system bars come back with a swipe. */
    private fun immersive() {
        WindowCompat.getInsetsController(window, window.decorView).apply {
            hide(WindowInsetsCompat.Type.systemBars())
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    private fun pipParams(phase: ViewerPhase): PictureInPictureParams {
        val builder = PictureInPictureParams.Builder()
        (phase as? ViewerPhase.Live)?.info?.let { builder.setAspectRatio(Rational(it.width, it.height)) }
        // Leaving the app with a live picture goes to PiP instead of stopping capture (Android 12+).
        if (Build.VERSION.SDK_INT >= 31) builder.setAutoEnterEnabled(phase is ViewerPhase.Live).setSeamlessResizeEnabled(false)
        return builder.build()
    }

    private fun updatePipParams(phase: ViewerPhase) = runCatching { setPictureInPictureParams(pipParams(phase)) }

    private fun enterPip() {
        runCatching { enterPictureInPictureMode(pipParams(graph.controller.phase.value)) }
    }

    @Composable
    private fun Settings(onDone: () -> Unit) {
        val preset by model.preset.collectAsState()
        val cards by graph.catalog.cards.collectAsState()
        val card by graph.controller.currentCard.collectAsState()
        val formats by graph.controller.formatOptions.collectAsState()
        val diagnostics by model.showDiagnostics.collectAsState()
        SettingsScreen(
            SettingsState(cards.map { it.key to it.name }, card?.key, formats, preset, diagnostics, BuildConfig.VERSION_NAME),
            object : SettingsActions {
                override fun selectCard(key: String) { graph.controller.select(key) }
                override fun setFormat(format: FormatPreference) = model.updatePreset(model.preset.value.copy(format = format))
                override fun update(preset: DevicePreset) = model.updatePreset(preset)
                override fun setShowDiagnostics(on: Boolean) = model.setShowDiagnostics(on)
                override fun chooseLut() = lutPicker.launch(arrayOf("*/*"))
                override fun showLicenses() = model.show(Feedback("Licenses: libusb (LGPL-2.1), libuvc (BSD), libjpeg-turbo (BSD/IJG), Oboe (Apache-2.0)"))
            },
            onDone,
        )
    }

    private fun hasCapturePermissions() = capturePermissions.all { checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }

    /** DEBUG: writes the renderer's clean frame to app files (pull with `make android-soak`). */
    private fun saveQAScreenshot() {
        val bitmap = graph.renderer.screenshot()
        if (bitmap == null) {
            Log.w("HDMICaptain", "qa screenshot: no frame")
            return
        }
        val file = File(filesDir, "qa-frame.png")
        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        Log.i("HDMICaptain", "qa screenshot: ${file.absolutePath} ${bitmap.width}x${bitmap.height}")
    }
}
