import CoreGraphics
import Foundation
import ImageIO
import Testing
@testable import CaptureCore

struct FrameSnapshotTests {
    @Test func pngKeepsResolutionAndColors() throws {
        let buffer = try #require(TestPattern.fullBars.makePixelBuffer(width: 640, height: 360))
        let data = try #require(FrameSnapshot.pngData(from: buffer))
        let source = try #require(CGImageSourceCreateWithData(data as CFData, nil))
        let image = try #require(CGImageSourceCreateImageAtIndex(source, 0, nil))
        #expect(image.width == 640 && image.height == 360)

        // Draw into a known RGBA sRGB context and sample each bar.
        var pixels = [UInt8](repeating: 0, count: 640 * 360 * 4)
        let ctx = try #require(CGContext(data: &pixels, width: 640, height: 360, bitsPerComponent: 8, bytesPerRow: 640 * 4,
                                         space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: 640, height: 360))
        for (index, expected) in TestPattern.fullBarColors.enumerated() {
            let x = 640 * (2 * index + 1) / 16
            let offset = (180 * 640 + x) * 4
            let rgb = SIMD3(Int(pixels[offset]), Int(pixels[offset + 1]), Int(pixels[offset + 2]))
            let want = SIMD3(Int(expected.x * 255), Int(expected.y * 255), Int(expected.z * 255))
            let diff = rgb &- want
            #expect(abs(diff.x) <= 6 && abs(diff.y) <= 6 && abs(diff.z) <= 6, "bar \(index): got \(rgb) want \(want)")
        }
    }

    @Test func uniqueFileNames() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let date = Date(timeIntervalSince1970: 0)
        let first = FrameSnapshot.fileURL(in: folder, date: date)
        try Data().write(to: first)
        let second = FrameSnapshot.fileURL(in: folder, date: date)
        #expect(first != second)
        #expect(second.lastPathComponent.hasSuffix(" 2.png"))
    }
}
