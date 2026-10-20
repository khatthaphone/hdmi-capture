import CoreGraphics
import Testing
@testable import CaptureCore

struct ScopeTests {
    @Test func barsProduceExpectedHistogramPeaks() throws {
        let buffer = try #require(TestPattern.fullBars.makePixelBuffer(width: 640, height: 360))
        let data = try #require(ScopeData.analyze(buffer))
        #expect(data.histogram.reduce(0, +) == 320 * 180)
        // Each of the 8 bars contributes 1/8 of samples at its luma value.
        for rgb in TestPattern.fullBarColors {
            let y = Int(TestPattern.ycbcr(rgb).0)
            #expect(data.histogram[y] >= UInt32(320 * 180 / 8 - 200), "luma \(y)")
        }
    }

    @Test func barsHitVectorscopeTargets() throws {
        let buffer = try #require(TestPattern.smpteBars.makePixelBuffer(width: 640, height: 360))
        let data = try #require(ScopeData.analyze(buffer))
        for (name, point) in ScopeData.barTargets {
            let x = Int(point.x * Double(ScopeData.vectorSize)), y = Int(point.y * Double(ScopeData.vectorSize))
            var hits: UInt32 = 0
            for dy in -1...1 { for dx in -1...1 {
                let xx = min(max(x + dx, 0), ScopeData.vectorSize - 1), yy = min(max(y + dy, 0), ScopeData.vectorSize - 1)
                hits += data.vectorscope[yy * ScopeData.vectorSize + xx]
            } }
            #expect(hits > 500, "\(name) target empty")
        }
    }

    @Test func waveformStepsMatchBars() throws {
        let buffer = try #require(TestPattern.fullBars.makePixelBuffer(width: 640, height: 360))
        let data = try #require(ScopeData.analyze(buffer))
        // First column is the white bar (Y=235), last is black (Y=16).
        #expect(data.waveform[235 * ScopeData.waveformColumns + 0] > 0)
        #expect(data.waveform[16 * ScopeData.waveformColumns + ScopeData.waveformColumns - 1] > 0)
        #expect(data.waveformImage()?.width == 256)
        #expect(data.vectorscopeImage()?.height == 128)
        #expect(data.histogramImage()?.height == 100)
    }
}
