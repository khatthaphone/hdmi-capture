import CaptureCore
import StoreKitTest
import XCTest
@testable import HDMICaptain

/// Runs against Apps/macOS/HDMICaptain.storekit — no App Store account needed.
@MainActor
final class EntitlementStoreTests: XCTestCase {
    private var session: SKTestSession!

    override func setUp() async throws {
        session = try SKTestSession(configurationFileNamed: "HDMICaptain")
        session.resetToDefaultState()
        session.clearTransactions()
        session.disableDialogs = true
    }

    func testLockedBeforePurchase() async {
        let store = EntitlementStore(listenForUpdates: false)
        await store.refresh()
        XCTAssertFalse(store.isPro)
        for feature in ProFeature.allCases {
            XCTAssertFalse(store.isPro && store.isUnlocked(feature))
        }
    }

    func testProductLoads() async {
        let store = EntitlementStore(listenForUpdates: false)
        await store.loadProduct()
        XCTAssertEqual(store.product?.id, EntitlementStore.proProductID)
        XCTAssertEqual(store.product?.type, .nonConsumable)
    }

    func testPurchaseUnlocksAndSurvivesNewStore() async {
        let store = EntitlementStore(listenForUpdates: false)
        await store.purchase()
        XCTAssertEqual(store.purchaseState, .idle)
        XCTAssertTrue(store.isPro)

        // A fresh store (simulating relaunch) sees the same entitlement.
        let relaunched = EntitlementStore(listenForUpdates: false)
        await relaunched.refresh()
        XCTAssertTrue(relaunched.isPro)
    }

    func testRefundRevokes() async throws {
        let transaction = try await session.buyProduct(identifier: EntitlementStore.proProductID)
        let store = EntitlementStore(listenForUpdates: false)
        await store.refresh()
        XCTAssertTrue(store.isPro)

        try session.refundTransaction(identifier: UInt(transaction.id))
        // StoreKit applies refunds asynchronously.
        for _ in 0..<30 where store.isPro {
            try await Task.sleep(for: .milliseconds(100))
            await store.refresh()
        }
        XCTAssertFalse(store.isPro)
    }
}
