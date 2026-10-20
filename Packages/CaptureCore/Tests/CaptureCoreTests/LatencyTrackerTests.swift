import Testing
@testable import CaptureCore

struct LatencyTrackerTests {
    @Test func averageAndP95() throws {
        var tracker = LatencyTracker(window: 2)
        for i in 0..<100 {
            tracker.add(i < 95 ? 0.030 : 0.100, at: Double(i) * 0.01)
        }
        let summary = try #require(tracker.summary(now: 1))
        #expect(summary.count == 100)
        #expect(abs(summary.averageMs - 33.5) < 0.01)
        #expect(abs(summary.p95Ms - 30) < 0.01)
    }

    @Test func dropsOldSamplesAndNonsense() throws {
        var tracker = LatencyTracker(window: 2)
        tracker.add(0.5, at: 0)
        tracker.add(-1, at: 5)
        tracker.add(10, at: 5)
        tracker.add(0.02, at: 5)
        let summary = try #require(tracker.summary(now: 5))
        #expect(summary.count == 1)
        #expect(abs(summary.averageMs - 20) < 0.01)
        #expect(LatencyTracker().summary(now: 0) == nil)
    }
}
