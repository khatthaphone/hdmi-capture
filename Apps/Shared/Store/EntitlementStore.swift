import CaptureCore
import Foundation
import Observation
import StoreKit

/// StoreKit 2 side of the Pro unlock (ADR 0004). Feature code only calls `isUnlocked(_:)`.
@MainActor
@Observable
final class EntitlementStore {
    enum PurchaseState: Equatable {
        case idle
        case purchasing
        /// Waiting on Ask to Buy or similar approval.
        case pending
        case failed(String)
    }

    static let proProductID = "com.example.hdmicaptain.pro"

    private(set) var isPro = false
    private(set) var product: Product?
    private(set) var purchaseState: PurchaseState = .idle

    /// Drives the upgrade sheet; set when a locked Pro feature is chosen.
    var isShowingUpgrade = false
    private(set) var highlightedFeature: ProFeature?

    #if DEBUG
    /// Developer toggle for exercising Pro features without the store.
    var debugUnlock = UserDefaults.standard.bool(forKey: "debugUnlockPro") {
        didSet { UserDefaults.standard.set(debugUnlock, forKey: "debugUnlockPro") }
    }
    #endif

    @ObservationIgnored private var updates: Task<Void, Never>?
    @ObservationIgnored private let honorsDebugUnlock: Bool

    init(listenForUpdates: Bool = true, honorsDebugUnlock: Bool = true) {
        self.honorsDebugUnlock = honorsDebugUnlock
        if listenForUpdates {
            // Refunds, revocations, Family Sharing and purchases made on another Mac arrive here.
            updates = Task { [weak self] in
                for await result in Transaction.updates {
                    if case let .verified(transaction) = result { await transaction.finish() }
                    await self?.refresh()
                }
            }
            Task {
                await refresh()
                await loadProduct()
            }
        }
    }

    func isUnlocked(_ feature: ProFeature) -> Bool {
        #if DEBUG
        if honorsDebugUnlock, debugUnlock { return true }
        #endif
        return isPro
    }

    /// Entry point for every Pro action: runs it when unlocked, otherwise offers the upgrade.
    /// Returns whether the action may proceed.
    @discardableResult
    func gate(_ feature: ProFeature) -> Bool {
        if isUnlocked(feature) { return true }
        requestUpgrade(highlighting: feature)
        return false
    }

    func requestUpgrade(highlighting feature: ProFeature? = nil) {
        highlightedFeature = feature
        if case .failed = purchaseState { purchaseState = .idle }
        isShowingUpgrade = true
    }

    func loadProduct() async {
        do {
            product = try await Product.products(for: [Self.proProductID]).first
        } catch {
            product = nil
        }
    }

    func purchase() async {
        if product == nil { await loadProduct() }
        guard let product else {
            purchaseState = .failed("The App Store is not available right now.")
            return
        }
        purchaseState = .purchasing
        do {
            switch try await product.purchase() {
            case let .success(.verified(transaction)):
                await transaction.finish()
                await refresh()
                purchaseState = .idle
            case .success(.unverified):
                purchaseState = .failed("The purchase could not be verified.")
            case .pending:
                purchaseState = .pending
            case .userCancelled:
                purchaseState = .idle
            @unknown default:
                purchaseState = .idle
            }
        } catch {
            purchaseState = .failed(error.localizedDescription)
        }
    }

    func restore() async {
        do {
            try await AppStore.sync()
        } catch {
            purchaseState = .failed(error.localizedDescription)
        }
        await refresh()
    }

    func refresh() async {
        var records: [EntitlementRecord] = []
        for await result in Transaction.currentEntitlements {
            switch result {
            case let .verified(transaction):
                records.append(EntitlementRecord(productID: transaction.productID, isVerified: true, revocationDate: transaction.revocationDate))
            case let .unverified(transaction, _):
                records.append(EntitlementRecord(productID: transaction.productID, isVerified: false))
            }
        }
        isPro = EntitlementResolver.isPro(records, proProductID: Self.proProductID)
    }
}
