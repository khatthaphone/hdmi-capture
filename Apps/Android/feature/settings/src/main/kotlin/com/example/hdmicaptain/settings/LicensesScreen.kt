package com.example.hdmicaptain.settings

import android.content.res.AssetManager
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties

/** Open-source notices from assets/licenses (libusb's LGPL notice must ship with the app). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LicensesScreen(assets: AssetManager, onDone: () -> Unit) {
    val files = remember { assets.list("licenses").orEmpty().sortedBy { if (it == "README.txt") "" else it } }
    var open by remember { mutableStateOf<String?>(null) }
    Dialog(onDismissRequest = onDone, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Scaffold(
            modifier = Modifier.fillMaxSize(),
            topBar = {
                TopAppBar(
                    title = { Text(open?.substringBeforeLast('.') ?: "Open-source licenses") },
                    actions = { TextButton(onClick = { if (open != null) open = null else onDone() }) { Text(if (open != null) "Back" else "Done") } },
                )
            },
        ) { padding ->
            val file = open
            if (file == null) {
                LazyColumn(Modifier.padding(padding)) {
                    items(files) { name ->
                        Text(
                            if (name == "README.txt") "Libraries used" else name.substringBeforeLast('.'),
                            Modifier.fillMaxWidth().clickable { open = name }.padding(horizontal = 20.dp, vertical = 14.dp),
                        )
                    }
                }
            } else {
                val text = remember(file) { assets.open("licenses/$file").bufferedReader().use { it.readText() } }
                LazyColumn(Modifier.padding(padding).padding(horizontal = 16.dp)) {
                    items(text.split("\n\n")) { paragraph ->
                        Text(paragraph, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, modifier = Modifier.padding(vertical = 6.dp))
                    }
                }
            }
        }
    }
}
