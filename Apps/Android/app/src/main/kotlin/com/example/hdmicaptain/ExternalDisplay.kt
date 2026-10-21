package com.example.hdmicaptain

import android.app.Presentation
import android.content.Context
import android.hardware.display.DisplayManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Display
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import com.example.hdmicaptain.render.VideoRenderer

/**
 * Clean feed on an external display (USB-C DisplayPort, HDMI adapter, Miracast): the picture only,
 * full screen, no controls; the phone keeps its own view with the control bar (as the iPad's 057).
 */
class ExternalDisplay(private val context: Context, private val renderer: VideoRenderer) : DisplayManager.DisplayListener {
    private val displays = context.getSystemService(DisplayManager::class.java)
    private var presentation: Presentation? = null

    fun start() {
        displays.registerDisplayListener(this, Handler(Looper.getMainLooper()))
        update()
    }

    fun stop() {
        displays.unregisterDisplayListener(this)
        dismiss()
    }

    override fun onDisplayAdded(displayId: Int) = update()
    override fun onDisplayRemoved(displayId: Int) = update()
    override fun onDisplayChanged(displayId: Int) = Unit

    private fun update() {
        val display = displays.getDisplays(DisplayManager.DISPLAY_CATEGORY_PRESENTATION).firstOrNull()
        if (display?.displayId == presentation?.display?.displayId) return
        dismiss()
        if (display != null) show(display)
    }

    private fun show(display: Display) {
        val screen = object : Presentation(context, display) {
            override fun onCreate(savedInstanceState: Bundle?) {
                super.onCreate(savedInstanceState)
                window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                setContentView(SurfaceView(context).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) = renderer.setExternal(holder.surface)
                        override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
                        override fun surfaceDestroyed(holder: SurfaceHolder) = renderer.setExternal(null)
                    })
                })
            }
        }
        runCatching { screen.show() }.onSuccess { presentation = screen }
    }

    private fun dismiss() {
        presentation?.dismiss()
        presentation = null
    }
}
