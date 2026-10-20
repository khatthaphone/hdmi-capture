import Foundation

/// Rolling latency samples (seconds) over a time window, for the stats HUD.
public struct LatencyTracker: Sendable {
    public struct Summary: Equatable, Sendable {
        public var averageMs: Double
        public var p95Ms: Double
        public var count: Int
    }

    public let window: Double
    private var samples: [(time: Double, value: Double)] = []

    public init(window: Double = 2) {
        self.window = window
    }

    public mutating func add(_ value: Double, at time: Double) {
        // Ignore nonsense (clock mismatch or stale timestamps).
        guard value >= 0, value < 2 else { return }
        samples.append((time, value))
        let cutoff = time - window
        if let first = samples.firstIndex(where: { $0.time >= cutoff }), first > 0 {
            samples.removeFirst(first)
        }
    }

    public func summary(now: Double) -> Summary? {
        let recent = samples.filter { $0.time >= now - window }.map(\.value).sorted()
        guard !recent.isEmpty else { return nil }
        let p95 = recent[min(recent.count - 1, Int((Double(recent.count) * 0.95).rounded(.up)) - 1)]
        return Summary(averageMs: recent.reduce(0, +) / Double(recent.count) * 1000, p95Ms: p95 * 1000, count: recent.count)
    }
}
