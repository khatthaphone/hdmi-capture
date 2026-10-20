import CoreGraphics
import CoreVideo
import Foundation

/// Luma histogram, luma waveform and CbCr vectorscope from a sampled 4:2:0 frame.
public struct ScopeData: Equatable, Sendable {
    public static let levels = 256
    public static let waveformColumns = 256
    public static let vectorSize = 128

    /// Counts per luma code value (0…255).
    public var histogram: [UInt32]
    /// `waveformColumns` columns × 256 rows of counts (row = luma code value).
    public var waveform: [UInt32]
    /// `vectorSize`² counts over (Cb, Cr).
    public var vectorscope: [UInt32]
    public var fullRange: Bool

    public static func analyze(_ buffer: CVPixelBuffer, samplesX: Int = 320, samplesY: Int = 180) -> ScopeData? {
        let format = CVPixelBufferGetPixelFormatType(buffer)
        guard format == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange || format == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        else { return nil }
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(buffer, .readOnly) }
        guard let yBase = CVPixelBufferGetBaseAddressOfPlane(buffer, 0)?.assumingMemoryBound(to: UInt8.self),
              let cBase = CVPixelBufferGetBaseAddressOfPlane(buffer, 1)?.assumingMemoryBound(to: UInt8.self)
        else { return nil }
        let width = CVPixelBufferGetWidthOfPlane(buffer, 0)
        let height = CVPixelBufferGetHeightOfPlane(buffer, 0)
        let yStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 0)
        let cStride = CVPixelBufferGetBytesPerRowOfPlane(buffer, 1)

        var histogram = [UInt32](repeating: 0, count: levels)
        var waveform = [UInt32](repeating: 0, count: waveformColumns * levels)
        var vectorscope = [UInt32](repeating: 0, count: vectorSize * vectorSize)
        for sy in 0..<samplesY {
            let py = (sy * 2 + 1) * height / (samplesY * 2)
            for sx in 0..<samplesX {
                let px = (sx * 2 + 1) * width / (samplesX * 2)
                let y = Int(yBase[py * yStride + px])
                let c = (py / 2) * cStride + (px / 2) * 2
                let cb = Int(cBase[c]), cr = Int(cBase[c + 1])
                histogram[y] += 1
                waveform[y * waveformColumns + sx * waveformColumns / samplesX] += 1
                let vx = cb * vectorSize / 256, vy = (255 - cr) * vectorSize / 256
                vectorscope[vy * vectorSize + vx] += 1
            }
        }
        return ScopeData(histogram: histogram, waveform: waveform, vectorscope: vectorscope,
                         fullRange: format == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange)
    }

    /// Where 75 % colour bars land on the vectorscope (normalized 0…1, y down), for target boxes.
    public static let barTargets: [(name: String, point: CGPoint)] = {
        let names = ["Yl", "Cy", "G", "Mg", "R", "B"]
        let colors: [SIMD3<Double>] = [[1, 1, 0], [0, 1, 1], [0, 1, 0], [1, 0, 1], [1, 0, 0], [0, 0, 1]]
        return zip(names, colors).map { name, rgb in
            let ycc = TestPattern.ycbcr(rgb * 0.75)
            return (name, CGPoint(x: Double(ycc.1) / 256, y: Double(255 - Int(ycc.2)) / 256))
        }
    }()

    // MARK: Images

    /// Waveform as a green-on-black image, 256 × 256 (top = 255).
    public func waveformImage() -> CGImage? {
        Self.image(width: Self.waveformColumns, height: Self.levels, counts: waveform, flipRows: true, tint: (0.35, 1, 0.45))
    }

    public func vectorscopeImage() -> CGImage? {
        Self.image(width: Self.vectorSize, height: Self.vectorSize, counts: vectorscope, flipRows: false, tint: (0.45, 1, 0.55))
    }

    /// Histogram bars, 256 × 100.
    public func histogramImage(height: Int = 100) -> CGImage? {
        let peak = Double(histogram.max() ?? 1)
        var counts = [UInt32](repeating: 0, count: Self.levels * height)
        for (level, count) in histogram.enumerated() {
            let bar = Int((Double(count) / max(peak, 1)).squareRoot() * Double(height))
            for row in 0..<bar { counts[(height - 1 - row) * Self.levels + level] = 1 }
        }
        return Self.image(width: Self.levels, height: height, counts: counts, flipRows: false, tint: (0.85, 0.85, 0.85), binary: true)
    }

    private static func image(width: Int, height: Int, counts: [UInt32], flipRows: Bool,
                              tint: (Double, Double, Double), binary: Bool = false) -> CGImage? {
        let peak = Double(counts.max() ?? 1)
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        for row in 0..<height {
            let sourceRow = flipRows ? height - 1 - row : row
            for col in 0..<width {
                let count = Double(counts[sourceRow * width + col])
                guard count > 0 else { continue }
                // Log scale so faint traces stay visible next to dense ones.
                let v = binary ? 1 : min(1, 0.25 + 0.75 * log1p(count) / log1p(max(peak, 1)))
                let i = (row * width + col) * 4
                pixels[i] = UInt8(255 * v * tint.0)
                pixels[i + 1] = UInt8(255 * v * tint.1)
                pixels[i + 2] = UInt8(255 * v * tint.2)
                pixels[i + 3] = 255
            }
        }
        guard let provider = CGDataProvider(data: Data(pixels) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }
}
