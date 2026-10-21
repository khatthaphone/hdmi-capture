import Foundation
import Testing
@testable import CaptureCore

/// Runs the JSON vectors in docs/fixtures/, which the Android Kotlin tests read too,
/// so both ports of the same logic stay in step.
struct SharedFixtureTests {
    private static let directory = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().appendingPathComponent("../../../../docs/fixtures").standardized

    private func load(_ name: String) throws -> [String: Any] {
        let data = try Data(contentsOf: Self.directory.appendingPathComponent(name))
        return try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func format(_ o: [String: Any]) -> FormatDescriptor {
        let ranges: [FrameRateRange]
        if let pairs = o["ranges"] as? [[Double]] {
            ranges = pairs.map { FrameRateRange(min: $0[0], max: $0[1]) }
        } else {
            ranges = (o["rates"] as! [Double]).map { FrameRateRange(min: $0, max: $0) }
        }
        return FormatDescriptor(width: o["w"] as! Int, height: o["h"] as! Int, fourCC: o["cc"] as! String, frameRateRanges: ranges)
    }

    private func content(_ value: Any) -> SignalContent {
        switch value {
        case let s as String: s == "black" ? .black : .picture
        case let o as [String: Any]:
            (o["solid"] as! [Int]).withUnsafeBufferPointer { .solid(red: $0[0], green: $0[1], blue: $0[2]) }
        default: .picture
        }
    }

    @Test func formatSelection() throws {
        let root = try load("format-selection.json")
        let sets = root["formatSets"] as? [String: [[String: Any]]] ?? [:]
        for case let c as [String: Any] in root["cases"] as! [Any] {
            let name = c["name"] as! String
            let formats = ((c["formats"] as? [[String: Any]]) ?? sets[c["formatSet"] as! String]!).map(format)
            var preference = FormatPreference.auto
            if let p = c["preference"] as? [String: Any] {
                preference = .pinned(width: p["w"] as! Int, height: p["h"] as! Int, frameRate: p["fps"] as? Double)
            }
            let choice = FormatSelector.select(from: formats, preference: preference)
            guard let e = c["expect"] as? [String: Any] else {
                #expect(choice == nil, "\(name)")
                continue
            }
            let got = try #require(choice, "\(name)")
            #expect("\(got.format.width)x\(got.format.height) \(got.format.fourCC)" == "\(e["w"]!)x\(e["h"]!) \(e["cc"]!)", "\(name)")
            #expect(abs(got.frameRate - (e["fps"] as! Double)) < 1e-6, "\(name)")
        }
    }

    @Test func signalClassify() throws {
        for case let c as [String: Any] in try load("signal-classify.json")["cases"] as! [Any] {
            let name = c["name"] as! String
            let s = c["stats"] as! [String: Double]
            let stats = SignalAnalyzer.Stats(meanY: s["meanY"]!, stdY: 0, meanCb: s["meanCb"]!, meanCr: s["meanCr"]!, stdChroma: 0,
                                             rangeY: s["rangeY"]!, rangeChroma: s["rangeChroma"]!)
            let got = SignalAnalyzer.classify(stats, fullRange: c["fullRange"] as! Bool)
            switch (content(c["expect"]!), got) {
            case let (.solid(r, g, b), .solid(gr, gg, gb)):
                #expect(abs(r - gr) <= 1 && abs(g - gg) <= 1 && abs(b - gb) <= 1, "\(name): \(got)")
            case let (want, _):
                #expect(want == got, "\(name)")
            }
        }
    }

    @Test func signalMonitor() throws {
        for case let c as [String: Any] in try load("signal-monitor.json")["cases"] as! [Any] {
            let monitor = SignalMonitor(persistence: c["persistence"] as! Double)
            for case let step as [String: Any] in c["steps"] as! [Any] {
                monitor.observe(content(step["content"]!), at: step["t"] as! Double)
                #expect(monitor.current.isNoSignal == content(step["expect"]!).isNoSignal, "\(c["name"]!) @ \(step["t"]!)")
            }
        }
    }

    @Test func recoveryBackoff() throws {
        for case let pair as [Double] in try load("recovery-backoff.json")["delays"] as! [Any] {
            #expect(CaptureEngine.restartDelay(attempt: Int(pair[0])) == pair[1])
        }
    }
}
