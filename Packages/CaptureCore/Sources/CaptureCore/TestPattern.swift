import CoreVideo
import Foundation

/// Generated reference images shown through the normal render path (no device needed).
public enum TestPattern: String, CaseIterable, Codable, Sendable {
    case smpteBars
    case fullBars
    case grid
    case grayRamp

    public var title: String {
        switch self {
        case .smpteBars: "SMPTE Color Bars (75 %)"
        case .fullBars: "Color Bars (100 %)"
        case .grid: "Grid"
        case .grayRamp: "Gray Ramp"
        }
    }

    /// The eight 100 % bars, left to right, as RGB 0…1 (white, yellow, cyan, green, magenta, red, blue, black).
    public static let fullBarColors: [SIMD3<Double>] = [
        [1, 1, 1], [1, 1, 0], [0, 1, 1], [0, 1, 0], [1, 0, 1], [1, 0, 0], [0, 0, 1], [0, 0, 0],
    ]

    /// Builds a limited-range BT.709 420v buffer.
    public func makePixelBuffer(width: Int = 1920, height: Int = 1080) -> CVPixelBuffer? {
        let attrs = [kCVPixelBufferMetalCompatibilityKey as String: true, kCVPixelBufferIOSurfacePropertiesKey as String: [:]] as CFDictionary
        var buffer: CVPixelBuffer?
        guard CVPixelBufferCreate(nil, width, height, kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange, attrs, &buffer) == kCVReturnSuccess,
              let buffer
        else { return nil }
        CVBufferSetAttachment(buffer, kCVImageBufferYCbCrMatrixKey, kCVImageBufferYCbCrMatrix_ITU_R_709_2, .shouldPropagate)
        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }
        let y = CVPixelBufferGetBaseAddressOfPlane(buffer, 0)!.assumingMemoryBound(to: UInt8.self)
        let c = CVPixelBufferGetBaseAddressOfPlane(buffer, 1)!.assumingMemoryBound(to: UInt8.self)
        let yStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 0)
        let cStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 1)

        for row in 0..<height {
            for col in 0..<width {
                let ycc = sample(x: Double(col) / Double(width), y: Double(row) / Double(height), width: width, height: height, col: col, row: row)
                y[row * yStride + col] = ycc.0
                if row % 2 == 0, col % 2 == 0 {
                    c[(row / 2) * cStride + col] = ycc.1
                    c[(row / 2) * cStride + col + 1] = ycc.2
                }
            }
        }
        return buffer
    }

    private func sample(x: Double, y: Double, width: Int, height: Int, col: Int, row: Int) -> (UInt8, UInt8, UInt8) {
        switch self {
        case .fullBars:
            return Self.ycbcr(Self.fullBarColors[min(7, Int(x * 8))])
        case .smpteBars:
            let seventh = min(6, Int(x * 7))
            if y < 0.67 {
                return Self.ycbcr(Self.fullBarColors[seventh] * 0.75)
            } else if y < 0.75 {
                // Reverse blue bars: blue, black, magenta, black, cyan, black, white (75 %).
                let reverse: [SIMD3<Double>] = [[0, 0, 0.75], [0, 0, 0], [0.75, 0, 0.75], [0, 0, 0], [0, 0.75, 0.75], [0, 0, 0], [0.75, 0.75, 0.75]]
                return Self.ycbcr(reverse[seventh])
            } else {
                // -I, 100 % white, +Q, black, then PLUGE (below black, black, above black).
                let segment = x * 7
                switch segment {
                case ..<1.25: return (16 + 0, 158, 95)          // -I approximation
                case ..<2.5: return Self.ycbcr([1, 1, 1])
                case ..<3.75: return (16 + 18, 155, 166)        // +Q approximation
                case ..<5: return (16, 128, 128)
                case ..<5.33: return (12, 128, 128)             // PLUGE −
                case ..<5.67: return (16, 128, 128)
                case ..<6: return (20, 128, 128)                // PLUGE +
                default: return (16, 128, 128)
                }
            }
        case .grid:
            let cell = max(8, height / 18)
            let onLine = col % cell == 0 || row % cell == 0 || col == width - 1 || row == height - 1
            let center = abs(col - width / 2) <= 1 || abs(row - height / 2) <= 1
            return onLine || center ? (235, 128, 128) : (16, 128, 128)
        case .grayRamp:
            let level = y < 0.5 ? x : (Double(min(10, Int(x * 11))) / 10)
            return Self.ycbcr([level, level, level])
        }
    }

    /// BT.709 limited-range conversion of RGB 0…1.
    static func ycbcr(_ rgb: SIMD3<Double>) -> (UInt8, UInt8, UInt8) {
        let luma = 0.2126 * rgb.x + 0.7152 * rgb.y + 0.0722 * rgb.z
        let cb = (rgb.z - luma) / 1.8556
        let cr = (rgb.x - luma) / 1.5748
        func byte(_ v: Double) -> UInt8 { UInt8(max(0, min(255, v.rounded()))) }
        return (byte(16 + 219 * luma), byte(128 + 224 * cb), byte(128 + 224 * cr))
    }
}

/// Framing overlays drawn above the video (not into it), so they never reach recordings.
public struct GuideSettings: Codable, Equatable, Sendable {
    public enum Mask: String, Codable, CaseIterable, Sendable {
        case scope = "2.39:1", flat = "1.85:1", classic = "4:3", vertical = "9:16", square = "1:1"
        public var ratio: Double {
            switch self {
            case .scope: 2.39
            case .flat: 1.85
            case .classic: 4.0 / 3.0
            case .vertical: 9.0 / 16.0
            case .square: 1
            }
        }
    }

    public var thirds = false
    public var center = false
    public var safeAreas = false
    public var mask: Mask?
    public var opacity = 0.6

    public init() {}

    public var isEmpty: Bool { !thirds && !center && !safeAreas && mask == nil }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        thirds = try c.decodeIfPresent(Bool.self, forKey: .thirds) ?? false
        center = try c.decodeIfPresent(Bool.self, forKey: .center) ?? false
        safeAreas = try c.decodeIfPresent(Bool.self, forKey: .safeAreas) ?? false
        mask = try c.decodeIfPresent(Mask.self, forKey: .mask)
        opacity = try c.decodeIfPresent(Double.self, forKey: .opacity) ?? 0.6
    }

    /// The visible region for a mask inside a video rect (letterbox or pillarbox).
    public static func maskRect(_ mask: Mask, in video: CGRect) -> CGRect {
        let videoRatio = video.width / video.height
        if mask.ratio >= videoRatio {
            let height = video.width / mask.ratio
            return CGRect(x: video.minX, y: video.midY - height / 2, width: video.width, height: height)
        }
        let width = video.height * mask.ratio
        return CGRect(x: video.midX - width / 2, y: video.minY, width: width, height: video.height)
    }
}
