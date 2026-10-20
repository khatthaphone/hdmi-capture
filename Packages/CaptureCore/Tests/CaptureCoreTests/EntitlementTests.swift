import Foundation
import Testing
@testable import CaptureCore

struct EntitlementResolverTests {
    let pro = "com.example.hdmicaptain.pro"

    @Test func nothingOwned() {
        #expect(!EntitlementResolver.isPro([], proProductID: pro))
    }

    @Test func verifiedPurchaseUnlocks() {
        #expect(EntitlementResolver.isPro([EntitlementRecord(productID: pro, isVerified: true)], proProductID: pro))
    }

    @Test func unverifiedNeverUnlocks() {
        #expect(!EntitlementResolver.isPro([EntitlementRecord(productID: pro, isVerified: false)], proProductID: pro))
    }

    @Test func revokedDoesNotUnlock() {
        let refunded = EntitlementRecord(productID: pro, isVerified: true, revocationDate: Date())
        #expect(!EntitlementResolver.isPro([refunded], proProductID: pro))
    }

    @Test func otherProductsDoNotUnlock() {
        #expect(!EntitlementResolver.isPro([EntitlementRecord(productID: "tip.small", isVerified: true)], proProductID: pro))
    }
}
