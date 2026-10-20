import CoreGraphics
import CoreVideo
import Metal
import Testing
@testable import CaptureCore

struct TestPatternTests {
    @Test(arguments: TestPattern.allCases)
    func generates(pattern: TestPattern) throws {
        let buffer = try #require(pattern.makePixelBuffer(width: 320, height: 180))
        #expect(CVPixelBufferGetWidth(buffer) == 320)
        let stats = try #require(SignalAnalyzer.stats(of: buffer))
        #expect(SignalAnalyzer.classify(stats, fullRange: false) == .picture)
    }

    @Test func fullBarsRenderToExpectedColors() throws {
        let renderer = try #require(MetalRenderer())
        let width = 640, height = 360
        let buffer = try #require(TestPattern.fullBars.makePixelBuffer(width: width, height: height))
        let device = renderer.device
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
        descriptor.usage = [.renderTarget]
        descriptor.storageMode = .readable(on: device)
        let target = try #require(device.makeTexture(descriptor: descriptor))
        let commandBuffer = try #require(device.makeCommandQueue()?.makeCommandBuffer())
        let textures = renderer.encode(buffer, into: target, options: RenderOptions(filter: .nearest), commandBuffer: commandBuffer)
        commandBuffer.commit()
        commandBuffer.waitUntilCompleted()
        _ = textures

        for (index, expected) in TestPattern.fullBarColors.enumerated() {
            var pixel = [UInt8](repeating: 0, count: 4)
            let x = width * (2 * index + 1) / 16
            target.getBytes(&pixel, bytesPerRow: width * 4, from: MTLRegionMake2D(x, height / 2, 1, 1), mipmapLevel: 0)
            let rgb = SIMD3(Int(pixel[2]), Int(pixel[1]), Int(pixel[0]))
            let want = SIMD3(Int(expected.x * 255), Int(expected.y * 255), Int(expected.z * 255))
            let diff = rgb &- want
            #expect(abs(diff.x) <= 4 && abs(diff.y) <= 4 && abs(diff.z) <= 4, "bar \(index): got \(rgb) want \(want)")
        }
    }

    @Test func maskRects() {
        let video = CGRect(x: 0, y: 0, width: 1920, height: 1080)
        let scope = GuideSettings.maskRect(.scope, in: video)
        #expect(scope.width == 1920 && abs(scope.height - 1920 / 2.39) < 0.01 && abs(scope.midY - 540) < 0.01)
        let vertical = GuideSettings.maskRect(.vertical, in: video)
        #expect(vertical.height == 1080 && abs(vertical.width - 607.5) < 0.01 && abs(vertical.midX - 960) < 0.01)
    }
}
