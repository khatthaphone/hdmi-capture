package com.example.hdmicaptain.viewer

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.FiberManualRecord
import androidx.compose.material.icons.outlined.History
import androidx.compose.material.icons.outlined.Videocam
import androidx.compose.material.icons.outlined.WorkspacePremium
import androidx.compose.material.icons.outlined.Podcasts
import androidx.compose.material.icons.rounded.Verified
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.example.hdmicaptain.billing.ProFeature
import com.example.hdmicaptain.billing.ProStore.PurchaseState

/**
 * The one-time upgrade sheet (Apple UpgradeView). Never shown unless the user picks a Pro feature
 * or opens it. Buy Pro is the one obvious action; Not Now and Restore are quiet text buttons.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UpgradeSheet(
    isPro: Boolean,
    highlighted: ProFeature?,
    price: String?,
    state: PurchaseState,
    onBuy: () -> Unit,
    onRestore: () -> Unit,
    onDismiss: () -> Unit,
) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(
            Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 28.dp).padding(bottom = 28.dp).testTag("upgrade"),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Box {
                Icon(Icons.Outlined.WorkspacePremium, null, tint = Brand.gold, modifier = Modifier.size(80.dp))
                if (isPro) Icon(Icons.Rounded.Verified, null, tint = Color(0xFF34C759), modifier = Modifier.align(Alignment.BottomEnd).size(26.dp))
            }
            Text(
                title(isPro, highlighted),
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 12.dp),
            )
            Text(
                if (isPro) "Thank you for supporting HDMI Captain." else "One-time purchase. No subscription.",
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp),
            )
            Column(
                Modifier.padding(top = 20.dp).fillMaxWidth()
                    .background(MaterialTheme.colorScheme.surfaceContainer, RoundedCornerShape(12.dp)).padding(18.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                ProFeature.shown.forEach { feature ->
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Icon(
                            feature.icon, null,
                            tint = if (feature == highlighted) Brand.gold else MaterialTheme.colorScheme.onSurface,
                            modifier = Modifier.width(32.dp),
                        )
                        Column {
                            Text(feature.title, style = MaterialTheme.typography.titleSmall)
                            Text(feature.summary, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
            Text(
                "The live viewer, screenshots and every picture setting stay free.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(top = 12.dp),
            )
            when (state) {
                is PurchaseState.Failed -> Text(state.message, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 10.dp))
                PurchaseState.Pending -> Text(
                    "Waiting for approval. Pro unlocks as soon as the purchase is approved.",
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 10.dp),
                )
                else -> Unit
            }
            if (isPro) {
                Button(onClick = onDismiss, modifier = Modifier.fillMaxWidth().padding(top = 20.dp)) { Text("Done") }
            } else {
                Button(
                    onClick = onBuy,
                    enabled = state != PurchaseState.Purchasing,
                    modifier = Modifier.fillMaxWidth().padding(top = 20.dp),
                ) {
                    if (state == PurchaseState.Purchasing) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    } else {
                        Icon(Icons.Outlined.WorkspacePremium, null, Modifier.padding(end = 8.dp))
                        Text(price?.let { "Buy Pro — $it" } ?: "Buy Pro", fontWeight = FontWeight.SemiBold)
                    }
                }
            }
            Row(Modifier.padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                if (!isPro) TextButton(onClick = onDismiss) { Text("Not Now") }
                TextButton(onClick = onRestore) { Text("Restore Purchase") }
            }
        }
    }
}

/** Names what the user was trying to do when the sheet opened from a locked feature. */
private fun title(isPro: Boolean, feature: ProFeature?) = when {
    isPro -> "You have HDMI Captain Pro"
    feature == ProFeature.Recording -> "Record with HDMI Captain Pro"
    feature == ProFeature.ReplayBuffer -> "Save Recent with HDMI Captain Pro"
    else -> "HDMI Captain Pro"
}

private val ProFeature.icon
    get() = when (this) {
        ProFeature.Recording -> Icons.Outlined.FiberManualRecord
        ProFeature.ReplayBuffer -> Icons.Outlined.History
        ProFeature.Streaming -> Icons.Outlined.Podcasts
        ProFeature.VirtualCamera -> Icons.Outlined.Videocam
    }
