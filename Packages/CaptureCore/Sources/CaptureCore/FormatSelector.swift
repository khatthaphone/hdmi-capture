import Foundation

/// A frame-rate range a capture format supports, in frames per second.
public struct FrameRateRange: Codable, Hashable, Sendable {
    public var min: Double
    public var max: Double

    public init(min: Double, max: Double) {
        self.min = min
        self.max = max
    }
}

/// Framework-free description of a capture format, so selection logic stays pure and testable.
public struct FormatDescriptor: Codable, Hashable, Sendable {
    public var width: Int
    public var height: Int
    /// Media subtype as a four-character code, e.g. "420v", "yuvs", "dmb1" (MJPEG). UVC names
    /// ("NV12", "YUY2", "MJPG") are understood too, so the shared fixtures run on Apple and Android.
    public var fourCC: String
    public var frameRateRanges: [FrameRateRange]

    public init(width: Int, height: Int, fourCC: String, frameRateRanges: [FrameRateRange]) {
        self.width = width
        self.height = height
        self.fourCC = fourCC
        self.frameRateRanges = frameRateRanges
    }

    public var pixelCount: Int { width * height }
    public var maxFrameRate: Double { frameRateRanges.map(\.max).max() ?? 0 }
    public var isCompressed: Bool { Self.compressedFourCCs.contains(fourCC) }
    /// Exactly 16:9 — what nearly every HDMI source outputs.
    public var isWidescreen: Bool { width * 9 == height * 16 }

    static let compressedFourCCs: Set<String> = ["dmb1", "jpeg", "mjpa", "mjpg", "avc1", "hvc1", "MJPG", "H264", "H265"]

    /// The supported rate closest to `target`.
    public func nearestSupportedRate(to target: Double) -> Double {
        frameRateRanges
            .map { Swift.min(Swift.max(target, $0.min), $0.max) }
            .min { abs($0 - target) < abs($1 - target) } ?? 0
    }
}

public enum FormatPreference: Codable, Hashable, Sendable {
    /// Smooth frame rate first, then 16:9, then highest resolution.
    case auto
    /// A specific resolution, optionally at a specific frame rate.
    case pinned(width: Int, height: Int, frameRate: Double?)
}

public struct FormatChoice: Hashable, Sendable {
    /// Index into the device's format list.
    public var index: Int
    public var format: FormatDescriptor
    public var frameRate: Double
}

public enum FormatSelector {
    /// Formats at or above this rate are treated as "smooth" and win over higher resolutions below it.
    public static let smoothFrameRate: Double = 49.9

    public static func select(from formats: [FormatDescriptor], preference: FormatPreference = .auto) -> FormatChoice? {
        let usable = formats.indices.filter { formats[$0].maxFrameRate > 0 }
        guard !usable.isEmpty else { return nil }

        if case let .pinned(width, height, frameRate) = preference {
            let matches = usable.filter { formats[$0].width == width && formats[$0].height == height }
            if !matches.isEmpty {
                return pinned(matches, in: formats, frameRate: frameRate)
            }
        }
        let best = usable.max { autoRank(formats[$0]).lexicographicallyPrecedes(autoRank(formats[$1])) }!
        return FormatChoice(index: best, format: formats[best], frameRate: formats[best].maxFrameRate)
    }

    /// Distinct resolution/frame-rate combinations, best first, for a settings picker.
    public static func options(from formats: [FormatDescriptor]) -> [FormatPreference] {
        var seen = Set<String>()
        return formats
            .filter { $0.maxFrameRate > 0 }
            .sorted { [Double($0.pixelCount), $0.maxFrameRate].lexicographicallyPrecedes([Double($1.pixelCount), $1.maxFrameRate]) }
            .reversed()
            .compactMap { f in
                let rate = (f.maxFrameRate * 100).rounded() / 100
                guard seen.insert("\(f.width)x\(f.height)@\(rate)").inserted else { return nil }
                return .pinned(width: f.width, height: f.height, frameRate: rate)
            }
    }

    private static func pinned(_ matches: [Int], in formats: [FormatDescriptor], frameRate: Double?) -> FormatChoice {
        guard let target = frameRate else {
            let best = matches.max { autoRank(formats[$0]).lexicographicallyPrecedes(autoRank(formats[$1])) }!
            return FormatChoice(index: best, format: formats[best], frameRate: formats[best].maxFrameRate)
        }
        // Nearest achievable rate first, then prefer uncompressed.
        func key(_ i: Int) -> [Double] {
            [-abs(formats[i].nearestSupportedRate(to: target) - target), encodingScore(formats[i])]
        }
        let best = matches.max { key($0).lexicographicallyPrecedes(key($1)) }!
        return FormatChoice(index: best, format: formats[best], frameRate: formats[best].nearestSupportedRate(to: target))
    }

    private static func autoRank(_ f: FormatDescriptor) -> [Double] {
        [f.maxFrameRate >= smoothFrameRate ? 1 : 0, f.isWidescreen ? 1 : 0, Double(f.pixelCount), f.maxFrameRate, encodingScore(f)]
    }

    /// Uncompressed YCbCr avoids an MJPEG decode step (latency + CPU).
    private static func encodingScore(_ f: FormatDescriptor) -> Double {
        switch f.fourCC {
        case "420v", "420f", "NV12", "NV21", "I420": 3
        case "yuvs", "2vuy", "yuv2", "YUY2", "UYVY": 2
        case _ where f.isCompressed: 0
        default: 1
        }
    }
}

public enum FourCC {
    public static func string(_ code: UInt32) -> String {
        let bytes = [24, 16, 8, 0].map { UInt8((code >> $0) & 0xFF) }
        let printable = bytes.allSatisfy { $0 >= 32 && $0 < 127 }
        return printable ? String(decoding: bytes, as: UTF8.self) : String(format: "0x%08X", code)
    }
}
