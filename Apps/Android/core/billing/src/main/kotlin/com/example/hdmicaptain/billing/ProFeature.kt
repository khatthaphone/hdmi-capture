package com.example.hdmicaptain.billing

/** What HDMI Captain Pro unlocks (ADR 0004). Everything not listed here is free and never checks this. */
enum class ProFeature(val title: String, val summary: String) {
    Recording("Recording", "Save full-quality video of the HDMI input, with its sound."),
    ReplayBuffer("Save Recent", "Missed a great moment? Save the last 15 seconds to 60 minutes after it happened."),
    Streaming("Live streaming", "Go live on Twitch, YouTube or any RTMP/SRT server."),
    VirtualCamera("Virtual camera", "Use the capture card as a camera in Zoom, Meet and FaceTime.");

    companion object {
        /** Not offered yet, as on Apple: Pro never sells something that isn't there. */
        val hidden = setOf(Streaming, VirtualCamera)
        val shown: List<ProFeature> get() = entries.filter { it !in hidden }
    }
}

/** A Play purchase reduced to what the unlock decision needs, so it is testable without Play. */
data class PurchaseRecord(val productIds: List<String>, val state: State, val acknowledged: Boolean) {
    enum class State { Purchased, Pending, Unspecified }
}

/** Pure unlock decision (CaptureCore's EntitlementResolver for Play). */
object EntitlementResolver {
    /** Only a completed purchase unlocks; pending (e.g. cash payment) waits; refunds disappear from the list. */
    fun isPro(records: List<PurchaseRecord>, productId: String): Boolean =
        records.any { productId in it.productIds && it.state == PurchaseRecord.State.Purchased }

    fun isPending(records: List<PurchaseRecord>, productId: String): Boolean =
        !isPro(records, productId) && records.any { productId in it.productIds && it.state == PurchaseRecord.State.Pending }

    /** Purchased but not yet acknowledged: Play refunds these after 3 days, so acknowledge them. */
    fun needsAcknowledgement(records: List<PurchaseRecord>, productId: String): List<PurchaseRecord> =
        records.filter { productId in it.productIds && it.state == PurchaseRecord.State.Purchased && !it.acknowledged }
}
