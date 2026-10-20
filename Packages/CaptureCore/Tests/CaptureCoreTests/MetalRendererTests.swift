import CoreVideo
import Metal
import Testing
@testable import CaptureCore

struct RenderGeometryTests {
    @Test func fitLetterboxes() {
        let g = RenderGeometry.compute(source: CGSize(width: 1920, height: 1080), drawable: CGSize(width: 1000, height: 1000), mode: .fit)
        #expect(g.viewport.width == 1000)
        #expect(g.viewport == CGRect(x: 0, y: 218, width: 1000, height: 563))
        #expect(g.sourceRect == CGRect(x: 0, y: 0, width: 1, height: 1))
    }

    @Test func fillCropsInsteadOfOverflowing() {
        let g = RenderGeometry.compute(source: CGSize(width: 1920, height: 1080), drawable: CGSize(width: 1000, height: 1000), mode: .fill)
        #expect(g.viewport == CGRect(x: 0, y: 0, width: 1000, height: 1000))
        #expect(abs(g.sourceRect.width - 0.5625) < 0.001)
        #expect(abs(g.sourceRect.midX - 0.5) < 0.001)
    }

    @Test func integerScaleIsPixelExact() {
        let g = RenderGeometry.compute(source: CGSize(width: 320, height: 240), drawable: CGSize(width: 1000, height: 800), mode: .integer)
        #expect(g.viewport == CGRect(x: 20, y: 40, width: 960, height: 720))
    }

    @Test func integerScaleFallsBackToFitWhenSmaller() {
        let g = RenderGeometry.compute(source: CGSize(width: 1920, height: 1080), drawable: CGSize(width: 960, height: 540), mode: .integer)
        #expect(g.viewport == CGRect(x: 0, y: 0, width: 960, height: 540))
    }
}

/// Renders synthetic 420v/420f frames offscreen and checks the YCbCr→RGB conversion.
struct ColorConversionTests {
    private func render(y: UInt8, cb: UInt8, cr: UInt8, format: OSType, range: ColorRange = .auto) throws -> SIMD3<Int> {
        let renderer = try #require(MetalRenderer())
        var buffer: CVPixelBuffer?
        let attrs = [kCVPixelBufferMetalCompatibilityKey as String: true, kCVPixelBufferIOSurfacePropertiesKey as String: [:]] as CFDictionary
        CVPixelBufferCreate(nil, 16, 16, format, attrs, &buffer)
        let pixelBuffer = try #require(buffer)
        CVBufferSetAttachment(pixelBuffer, kCVImageBufferYCbCrMatrixKey, kCVImageBufferYCbCrMatrix_ITU_R_709_2, .shouldPropagate)
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        for plane in 0..<2 {
            let base = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, plane)!.assumingMemoryBound(to: UInt8.self)
            let rows = CVPixelBufferGetHeightOfPlane(pixelBuffer, plane)
            let stride = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, plane)
            for i in 0..<(rows * stride) {
                base[i] = plane == 0 ? y : (i % 2 == 0 ? cb : cr)
            }
        }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, [])

        let device = renderer.device
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: 16, height: 16, mipmapped: false)
        descriptor.usage = [.renderTarget]
        descriptor.storageMode = .readable(on: device)
        let target = try #require(device.makeTexture(descriptor: descriptor))
        let queue = try #require(device.makeCommandQueue())
        let commandBuffer = try #require(queue.makeCommandBuffer())
        let textures = renderer.encode(pixelBuffer, into: target, options: RenderOptions(colorRange: range), commandBuffer: commandBuffer)
        #expect(textures.count == 2)
        #if os(macOS)
        if !device.hasUnifiedMemory, let blit = commandBuffer.makeBlitCommandEncoder() {
            blit.synchronize(resource: target)
            blit.endEncoding()
        }
        #endif
        commandBuffer.commit()
        commandBuffer.waitUntilCompleted()

        var pixel = [UInt8](repeating: 0, count: 4)
        target.getBytes(&pixel, bytesPerRow: 64, from: MTLRegionMake2D(8, 8, 1, 1), mipmapLevel: 0)
        return SIMD3(Int(pixel[2]), Int(pixel[1]), Int(pixel[0])) // BGRA → RGB
    }

    private func expectClose(_ rgb: SIMD3<Int>, _ expected: SIMD3<Int>, tolerance: Int = 3) {
        let diff = rgb &- expected
        #expect(abs(diff.x) <= tolerance && abs(diff.y) <= tolerance && abs(diff.z) <= tolerance, "got \(rgb), expected \(expected)")
    }

    private let video = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
    private let full = kCVPixelFormatType_420YpCbCr8BiPlanarFullRange

    @Test func limitedRangeWhiteAndBlack() throws {
        expectClose(try render(y: 235, cb: 128, cr: 128, format: video), SIMD3(255, 255, 255))
        expectClose(try render(y: 16, cb: 128, cr: 128, format: video), SIMD3(0, 0, 0))
    }

    @Test func limitedRangeBT709Red() throws {
        // BT.709 limited-range 100% red: Y=63 Cb=102 Cr=240.
        expectClose(try render(y: 63, cb: 102, cr: 240, format: video), SIMD3(255, 0, 0), tolerance: 4)
    }

    @Test func fullRangeAutoDetected() throws {
        expectClose(try render(y: 255, cb: 128, cr: 128, format: full), SIMD3(255, 255, 255))
        expectClose(try render(y: 0, cb: 128, cr: 128, format: full), SIMD3(0, 0, 0))
    }

    @Test func rangeOverride() throws {
        // Treating limited black as full range lifts it to 16.
        expectClose(try render(y: 16, cb: 128, cr: 128, format: video, range: .full), SIMD3(16, 16, 16))
    }
}
