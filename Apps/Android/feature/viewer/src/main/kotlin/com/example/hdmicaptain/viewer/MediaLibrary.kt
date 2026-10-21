package com.example.hdmicaptain.viewer

import android.content.ContentValues
import android.content.Context
import android.graphics.Bitmap
import android.net.Uri
import android.provider.MediaStore
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Saves outputs where people find them: Pictures/HDMI Captain (no storage permission, API 29+). */
object MediaLibrary {
    private fun stamp() = SimpleDateFormat("yyyy-MM-dd 'at' HH.mm.ss", Locale.US).format(Date())

    fun saveScreenshot(context: Context, bitmap: Bitmap): Uri? {
        val values = ContentValues().apply {
            put(MediaStore.Images.Media.DISPLAY_NAME, "HDMI Captain ${stamp()}.png")
            put(MediaStore.Images.Media.MIME_TYPE, "image/png")
            put(MediaStore.Images.Media.RELATIVE_PATH, "Pictures/HDMI Captain")
            put(MediaStore.Images.Media.IS_PENDING, 1)
        }
        val resolver = context.contentResolver
        val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: return null
        val ok = resolver.openOutputStream(uri)?.use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) } == true
        if (!ok) {
            resolver.delete(uri, null, null)
            return null
        }
        resolver.update(uri, ContentValues().apply { put(MediaStore.Images.Media.IS_PENDING, 0) }, null, null)
        return uri
    }
}
