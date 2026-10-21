import CoreMedia
import Foundation
import QuartzCore

/// Built-in latency test (task 035): the Mac shows a full-screen black/white flip on its own HDMI
/// output, which loops into the capture card; this matches each flip to the first captured frame
/// that shows it. Pure, so it is tested with synthetic timelines.
public struct LatencyProbe: Sendable {
    public struct Result: Equatable, Sendable {
        /// Flip on screen → frame arrived from the card, per matched flip (ms).
        public var samplesMs: [Double]
        public var flips: Int

        public var medianMs: Double? { Self.percentile(samplesMs, 0.5) }
        public var p95Ms: Double? { Self.percentile(samplesMs, 0.95) }
        /// Flips never seen in the capture (signal too dark, wrong screen, card not connected).
        public var missed: Int { flips - samplesMs.count }

        static func percentile(_ values: [Double], _ p: Double) -> Double? {
            guard !values.isEmpty else { return nil }
            let sorted = values.sorted()
            return sorted[min(sorted.count - 1, max(0, Int((Double(sorted.count) * p).rounded(.up)) - 1))]
        }
    }

    private struct Flip { var time: Double; var white: Bool; var matched: Bool }

    private var flips: [Flip] = []
    private var samples: [Double] = []
    private var darkest = Double.infinity
    private var brightest = -Double.infinity
    private var lastWhite: Bool?

    /// Luma levels must differ by this much before frames are classified (a black/white flip
    /// through any card easily clears it; a static picture never does).
    static let minimumContrast = 40.0

    public init() {}

    /// The screen showed white (or black) at `time` (host seconds, CACurrentMediaTime).
    public mutating func flip(at time: Double, white: Bool) {
        flips.append(Flip(time: time, white: white, matched: false))
    }

    /// A captured frame with mean luma `meanY` (0…255) arrived at `time` (host seconds).
    public mutating func frame(at time: Double, meanY: Double) {
        darkest = min(darkest, meanY)
        brightest = max(brightest, meanY)
        guard brightest - darkest >= Self.minimumContrast else { return }
        let white = meanY > (darkest + brightest) / 2
        defer { lastWhite = white }
        guard let previous = lastWhite, previous != white else { return }
        // The newest unmatched flip to this colour that happened before the frame caused it.
        guard let index = flips.lastIndex(where: { !$0.matched && $0.white == white && $0.time <= time }) else { return }
        flips[index].matched = true
        let latency = time - flips[index].time
        if latency < 1 { samples.append(latency * 1000) }
    }

    public var result: Result { Result(samplesMs: samples, flips: flips.count) }
}

/// Mean luma of each captured frame with its arrival time, for the latency test.
public final class LumaProbeSink: FrameSink, @unchecked Sendable {
    /// Called on the capture queue: (arrival host time, mean Y 0…255).
    private let onFrame: (Double, Double) -> Void

    public init(onFrame: @escaping (Double, Double) -> Void) {
        self.onFrame = onFrame
    }

    public func consume(_ sampleBuffer: CMSampleBuffer) {
        let arrived = CACurrentMediaTime()
        guard let buffer = CMSampleBufferGetImageBuffer(sampleBuffer), let stats = SignalAnalyzer.stats(of: buffer) else { return }
        onFrame(arrived, stats.meanY)
    }
}
