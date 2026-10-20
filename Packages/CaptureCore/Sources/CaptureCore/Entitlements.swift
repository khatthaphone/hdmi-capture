import Foundation

/// Capabilities unlocked by the one-time "HDMI Captain Pro" purchase (ADR 0004).
/// Everything not listed here is free.
public enum ProFeature: String, CaseIterable, Sendable {
    case recording
    case replayBuffer
    case streaming
    case virtualCamera

    public var title: String {
        switch self {
        case .recording: "Recording"
        case .replayBuffer: "Instant replay"
        case .streaming: "Live streaming"
        case .virtualCamera: "Virtual camera"
        }
    }
}

/// A store transaction reduced to what the unlock decision needs, so the decision is testable
/// without StoreKit.
public struct EntitlementRecord: Equatable, Sendable {
    public var productID: String
    /// Only cryptographically verified transactions may unlock anything.
    public var isVerified: Bool
    public var revocationDate: Date?

    public init(productID: String, isVerified: Bool, revocationDate: Date? = nil) {
        self.productID = productID
        self.isVerified = isVerified
        self.revocationDate = revocationDate
    }
}

public enum EntitlementResolver {
    public static func isPro(_ records: [EntitlementRecord], proProductID: String) -> Bool {
        records.contains { $0.productID == proProductID && $0.isVerified && $0.revocationDate == nil }
    }
}
