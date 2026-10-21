package com.example.hdmicaptain.billing

import com.example.hdmicaptain.billing.PurchaseRecord.State
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class EntitlementResolverTest {
    private val pro = "com.example.hdmicaptain.pro"

    @Test fun purchasedUnlocks() = assertTrue(EntitlementResolver.isPro(listOf(PurchaseRecord(listOf(pro), State.Purchased, true)), pro))

    @Test fun pendingWaits() {
        val records = listOf(PurchaseRecord(listOf(pro), State.Pending, false))
        assertFalse(EntitlementResolver.isPro(records, pro))
        assertTrue(EntitlementResolver.isPending(records, pro))
    }

    @Test fun refundedIsGone() = assertFalse(EntitlementResolver.isPro(emptyList(), pro))

    @Test fun otherProductsDontUnlock() = assertFalse(EntitlementResolver.isPro(listOf(PurchaseRecord(listOf("tip.jar"), State.Purchased, true)), pro))

    @Test fun unacknowledgedPurchasesGetAcknowledged() {
        val fresh = PurchaseRecord(listOf(pro), State.Purchased, false)
        val records = listOf(fresh, PurchaseRecord(listOf(pro), State.Pending, false))
        assertEquals(listOf(fresh), EntitlementResolver.needsAcknowledgement(records, pro))
    }

    @Test fun shownFeaturesHideStreamingAndVirtualCamera() =
        assertEquals(listOf(ProFeature.Recording, ProFeature.ReplayBuffer), ProFeature.shown)
}
