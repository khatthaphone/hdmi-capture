package com.example.hdmicaptain.billing

import android.app.Activity
import android.content.Context
import android.util.Log
import com.android.billingclient.api.AcknowledgePurchaseParams
import com.android.billingclient.api.BillingClient
import com.android.billingclient.api.BillingClientStateListener
import com.android.billingclient.api.BillingFlowParams
import com.android.billingclient.api.BillingResult
import com.android.billingclient.api.PendingPurchasesParams
import com.android.billingclient.api.ProductDetails
import com.android.billingclient.api.Purchase
import com.android.billingclient.api.QueryProductDetailsParams
import com.android.billingclient.api.QueryPurchasesParams
import com.android.billingclient.api.acknowledgePurchase
import com.android.billingclient.api.queryProductDetails
import com.android.billingclient.api.queryPurchasesAsync
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * HDMI Captain Pro through Play Billing (the Android EntitlementStore). One non-consumable
 * product: purchases are acknowledged, never consumed; re-checked at launch and on return to the
 * app so refunds and family changes apply. Feature code asks [gate] and nothing else.
 */
class ProStore(context: Context, private val scope: CoroutineScope, val productId: String) {
    sealed interface PurchaseState {
        data object Idle : PurchaseState
        data object Purchasing : PurchaseState
        data object Pending : PurchaseState
        data class Failed(val message: String) : PurchaseState
    }

    private val _isPro = MutableStateFlow(false)
    val isPro: StateFlow<Boolean> = _isPro.asStateFlow()

    private val _purchaseState = MutableStateFlow<PurchaseState>(PurchaseState.Idle)
    val purchaseState: StateFlow<PurchaseState> = _purchaseState.asStateFlow()

    private val _price = MutableStateFlow<String?>(null)
    /** Localised price from Play ("$9.99"), once loaded. */
    val price: StateFlow<String?> = _price.asStateFlow()

    /** Non-null while the upgrade sheet should show; the feature the user tried, if any. */
    private val _upgradeRequest = MutableStateFlow<UpgradeRequest?>(null)
    val upgradeRequest: StateFlow<UpgradeRequest?> = _upgradeRequest.asStateFlow()

    data class UpgradeRequest(val feature: ProFeature?)

    /** DEBUG builds only: `--ez debugUnlockPro true`. */
    var debugUnlock = false
        set(value) {
            field = value
            _isPro.value = value || purchased
        }

    private var purchased = false
    private var product: ProductDetails? = null

    private val client = BillingClient.newBuilder(context)
        .setListener { result, purchases -> onPurchasesUpdated(result, purchases) }
        .enablePendingPurchases(PendingPurchasesParams.newBuilder().enableOneTimeProducts().build())
        .enableAutoServiceReconnection()
        .build()

    fun start() {
        client.startConnection(object : BillingClientStateListener {
            override fun onBillingSetupFinished(result: BillingResult) {
                if (result.responseCode == BillingClient.BillingResponseCode.OK) scope.launch {
                    refresh()
                    loadProduct()
                } else {
                    Log.w(TAG, "billing setup: ${result.debugMessage}")
                }
            }

            override fun onBillingServiceDisconnected() = Unit  // auto reconnection
        })
    }

    fun isUnlocked(feature: ProFeature): Boolean = _isPro.value

    /** The one Pro check: true when unlocked, otherwise opens the upgrade sheet and returns false. */
    fun gate(feature: ProFeature): Boolean {
        if (isUnlocked(feature)) return true
        requestUpgrade(feature)
        return false
    }

    fun requestUpgrade(feature: ProFeature? = null) {
        _upgradeRequest.value = UpgradeRequest(feature)
    }

    fun dismissUpgrade() {
        _upgradeRequest.value = null
        if (_purchaseState.value is PurchaseState.Failed) _purchaseState.value = PurchaseState.Idle
    }

    fun purchase(activity: Activity) {
        val details = product ?: run {
            _purchaseState.value = PurchaseState.Failed("Couldn't reach Google Play. Check your connection and try again.")
            scope.launch { loadProduct() }
            return
        }
        _purchaseState.value = PurchaseState.Purchasing
        val params = BillingFlowParams.newBuilder()
            .setProductDetailsParamsList(listOf(BillingFlowParams.ProductDetailsParams.newBuilder().setProductDetails(details).build()))
            .build()
        val result = client.launchBillingFlow(activity, params)
        if (result.responseCode != BillingClient.BillingResponseCode.OK) {
            _purchaseState.value = PurchaseState.Failed("Couldn't start the purchase: ${result.debugMessage}")
        }
    }

    /** Restore Purchase: Play already knows; just ask again. */
    fun restore() = scope.launch { refresh() }

    /** Re-check owned purchases (launch, return to the app, Restore). */
    suspend fun refresh() {
        val result = client.queryPurchasesAsync(QueryPurchasesParams.newBuilder().setProductType(BillingClient.ProductType.INAPP).build())
        if (result.billingResult.responseCode == BillingClient.BillingResponseCode.OK) apply(result.purchasesList)
    }

    private suspend fun loadProduct() {
        val params = QueryProductDetailsParams.newBuilder().setProductList(
            listOf(QueryProductDetailsParams.Product.newBuilder().setProductId(productId).setProductType(BillingClient.ProductType.INAPP).build()),
        ).build()
        val result = client.queryProductDetails(params)
        product = result.productDetailsList?.firstOrNull()
        _price.value = product?.oneTimePurchaseOfferDetails?.formattedPrice
    }

    private fun onPurchasesUpdated(result: BillingResult, purchases: List<Purchase>?) {
        when (result.responseCode) {
            BillingClient.BillingResponseCode.OK -> scope.launch { apply(purchases.orEmpty()) }
            BillingClient.BillingResponseCode.USER_CANCELED -> _purchaseState.value = PurchaseState.Idle
            BillingClient.BillingResponseCode.ITEM_ALREADY_OWNED -> scope.launch { refresh() }
            else -> _purchaseState.value = PurchaseState.Failed("The purchase didn't go through. ${result.debugMessage}".trim())
        }
    }

    private suspend fun apply(purchases: List<Purchase>) {
        val records = purchases.map { it.record() }
        purchased = EntitlementResolver.isPro(records, productId)
        _isPro.value = purchased || debugUnlock
        _purchaseState.value = when {
            EntitlementResolver.isPending(records, productId) -> PurchaseState.Pending
            else -> PurchaseState.Idle
        }
        purchases.filter { it.record() in EntitlementResolver.needsAcknowledgement(records, productId) }.forEach { purchase ->
            val ack = client.acknowledgePurchase(AcknowledgePurchaseParams.newBuilder().setPurchaseToken(purchase.purchaseToken).build())
            if (ack.responseCode != BillingClient.BillingResponseCode.OK) Log.w(TAG, "acknowledge: ${ack.debugMessage}")
        }
    }

    private fun Purchase.record() = PurchaseRecord(
        productIds = products,
        state = when (purchaseState) {
            Purchase.PurchaseState.PURCHASED -> PurchaseRecord.State.Purchased
            Purchase.PurchaseState.PENDING -> PurchaseRecord.State.Pending
            else -> PurchaseRecord.State.Unspecified
        },
        acknowledged = isAcknowledged,
    )

    private companion object {
        const val TAG = "HDMICaptain"
    }
}
