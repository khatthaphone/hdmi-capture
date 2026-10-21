import Testing
@testable import CaptureCore

struct LatencyProbeTests {
    /// Simulates a 60 fps capture of a screen that flips every 0.5 s, seen `delay` later.
    private func run(delay: Double, flips: Int = 30, black: Double = 16, white: Double = 235) -> LatencyProbe.Result {
        var probe = LatencyProbe()
        var screenWhite = false
        var changes: [(Double, Bool)] = []
        for i in 0..<flips {
            let t = 1 + Double(i) * 0.5
            screenWhite.toggle()
            probe.flip(at: t, white: screenWhite)
            changes.append((t, screenWhite))
        }
        var frame = 0.0
        while frame < 1 + Double(flips) * 0.5 + 1 {
            // What the card shows at `frame`: the screen state from `delay` earlier.
            let shown = changes.last { $0.0 <= frame - delay }?.1 ?? false
            probe.frame(at: frame, meanY: shown ? white : black)
            frame += 1.0 / 60
        }
        return probe.result
    }

    @Test func measuresAKnownDelayWithinAFrame() throws {
        let result = run(delay: 0.070)
        #expect(result.flips == 30)
        #expect(result.missed <= 1)  // the first transition calibrates the levels
        let median = try #require(result.medianMs)
        #expect(abs(median - 70) <= 17)
        #expect(try #require(result.p95Ms) - median <= 17)
    }

    @Test func repeatedRunsAgreeWithinAFrame() throws {
        let a = try #require(run(delay: 0.045).medianMs)
        let b = try #require(run(delay: 0.045).medianMs)
        #expect(abs(a - b) <= 1000.0 / 60)
    }

    @Test func limitedContrastStillWorks() throws {
        let median = try #require(run(delay: 0.1, black: 40, white: 120).medianMs)
        #expect(abs(median - 100) <= 17)
    }

    @Test func noLoopMeansEverythingMissed() {
        var probe = LatencyProbe()
        for i in 0..<10 { probe.flip(at: Double(i), white: i % 2 == 0) }
        for i in 0..<600 { probe.frame(at: Double(i) / 60, meanY: 90) }  // a static picture
        #expect(probe.result.samplesMs.isEmpty)
        #expect(probe.result.missed == 10)
        #expect(probe.result.medianMs == nil)
    }
}
