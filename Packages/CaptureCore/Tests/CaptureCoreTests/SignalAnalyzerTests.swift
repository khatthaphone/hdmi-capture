import CoreVideo
import Testing
@testable import CaptureCore

struct SignalAnalyzerTests {
    private func classify(_ buffer: CVPixelBuffer, fullRange: Bool = false) throws -> SignalContent {
        let stats = try #require(SignalAnalyzer.stats(of: buffer))
        return SignalAnalyzer.classify(stats, fullRange: fullRange)
    }

    @Test func limitedBlackIsNoSignal() throws {
        #expect(try classify(makeYCbCrBuffer(width: 1280, height: 720) { _, _ in (16, 128, 128) }) == .black)
    }

    @Test func fullRangeBlackIsNoSignal() throws {
        let buffer = makeYCbCrBuffer(fullRange: true) { _, _ in (0, 128, 128) }
        #expect(try classify(buffer, fullRange: true) == .black)
    }

    @Test func blueScreenIsSolidBlue() throws {
        // BT.709 limited-range 100 % blue: Y=32 Cb=240 Cr=118
        guard case let .solid(r, g, b) = try classify(makeYCbCrBuffer { _, _ in (32, 240, 118) }) else {
            Issue.record("expected solid colour")
            return
        }
        #expect(b > 230 && r < 30 && g < 30)
    }

    @Test func colorBarsArePicture() throws {
        let bars: [(UInt8, UInt8, UInt8)] = [(235, 128, 128), (219, 16, 138), (188, 154, 16), (173, 42, 26),
                                             (78, 214, 230), (63, 102, 240), (32, 240, 118), (16, 128, 128)]
        #expect(try classify(makeYCbCrBuffer(width: 640, height: 360) { x, _ in bars[x * 8 / 640] }) == .picture)
    }

    @Test func darkNoisyPictureIsPicture() throws {
        var rng = SystemRandomNumberGenerator()
        let buffer = makeYCbCrBuffer(width: 320, height: 180) { _, _ in (UInt8.random(in: 16...40, using: &rng), 128, 128) }
        #expect(try classify(buffer) == .picture)
    }

    @Test func thinTextOnBlackIsPicture() throws {
        // A dark terminal: black with a few 1-px light strokes.
        let buffer = makeYCbCrBuffer(width: 1280, height: 720) { x, y in
            (y % 24 == 12 && x % 9 < 5 && x < 900) ? (200, 128, 128) : (16, 128, 128)
        }
        #expect(try classify(buffer) == .picture)
    }

    @Test func unsupportedFormatReturnsNil() {
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(nil, 16, 16, kCVPixelFormatType_32BGRA, nil, &buffer)
        #expect(SignalAnalyzer.stats(of: buffer!) == nil)
    }
}

struct SignalMonitorTests {
    @Test func reportsLossOnlyAfterPersistence() {
        let monitor = SignalMonitor(persistence: 1.5)
        monitor.observe(.picture, at: 0)
        monitor.observe(.black, at: 1)
        monitor.observe(.black, at: 2)
        #expect(monitor.current == .picture) // only 1 s of black
        monitor.observe(.black, at: 2.6)
        #expect(monitor.current == .black)
    }

    @Test func recoveryIsImmediate() {
        let monitor = SignalMonitor(persistence: 1)
        monitor.observe(.black, at: 0)
        monitor.observe(.black, at: 2)
        #expect(monitor.current == .black)
        monitor.observe(.picture, at: 2.1)
        #expect(monitor.current == .picture)
    }

    @Test func blackSceneCutDoesNotTrigger() {
        let monitor = SignalMonitor(persistence: 1.5)
        monitor.observe(.black, at: 0)
        monitor.observe(.picture, at: 0.8)
        monitor.observe(.black, at: 1.2)
        monitor.observe(.black, at: 2.0)
        #expect(monitor.current == .picture)
    }

    @Test func flickeringCardScreenCountsAsOneLoss() {
        let monitor = SignalMonitor(persistence: 1)
        monitor.observe(.black, at: 0)
        monitor.observe(.solid(red: 0, green: 0, blue: 255), at: 0.5)
        monitor.observe(.black, at: 1.1)
        #expect(monitor.current.isNoSignal)
    }
}
