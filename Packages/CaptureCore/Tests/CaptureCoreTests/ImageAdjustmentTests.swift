import CoreVideo
import Metal
import Testing
@testable import CaptureCore

/// Renders 100 % bars through the real shader with given adjustments.
private func renderBars(_ adjustments: ImageAdjustments, width: Int = 640, height: Int = 360) throws -> (MTLTexture, MTLCommandBuffer) {
    let renderer = try #require(MetalRenderer())
    let buffer = try #require(TestPattern.fullBars.makePixelBuffer(width: width, height: height))
    let device = renderer.device
    let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
    descriptor.usage = [.renderTarget]
    descriptor.storageMode = .readable(on: device)
    let target = try #require(device.makeTexture(descriptor: descriptor))
    let commandBuffer = try #require(device.makeCommandQueue()?.makeCommandBuffer())
    let textures = renderer.encode(buffer, into: target, options: RenderOptions(filter: .nearest, adjustments: adjustments), commandBuffer: commandBuffer)
    commandBuffer.commit()
    commandBuffer.waitUntilCompleted()
    _ = textures
    return (target, commandBuffer)
}

private func pixel(_ texture: MTLTexture, x: Int, y: Int) -> SIMD3<Int> {
    var p = [UInt8](repeating: 0, count: 4)
    texture.getBytes(&p, bytesPerRow: texture.width * 4, from: MTLRegionMake2D(x, y, 1, 1), mipmapLevel: 0)
    return SIMD3(Int(p[2]), Int(p[1]), Int(p[0]))
}

struct ImageAdjustmentTests {
    @Test func identityMatchesUnadjustedOutput() throws {
        let (plain, _) = try renderBars(ImageAdjustments())
        let (identity, _) = try renderBars(ImageAdjustments.Preset.standard.values)
        for bar in 0..<8 {
            let x = 640 * (2 * bar + 1) / 16
            let diff = pixel(plain, x: x, y: 180) &- pixel(identity, x: x, y: 180)
            #expect(abs(diff.x) <= 1 && abs(diff.y) <= 1 && abs(diff.z) <= 1)
        }
    }

    @Test func zeroSaturationIsGrey() throws {
        let (texture, _) = try renderBars(ImageAdjustments(saturation: 0))
        let red = pixel(texture, x: 640 * 11 / 16, y: 180) // red bar
        #expect(abs(red.x - red.y) <= 2 && abs(red.y - red.z) <= 2)
    }

    @Test func brightnessLiftsBlack() throws {
        let (texture, _) = try renderBars(ImageAdjustments(brightness: 0.2))
        let black = pixel(texture, x: 640 * 15 / 16, y: 180)
        #expect(abs(black.x - 51) <= 3)
    }

    @Test func gammaAboveOneBrightensMidtones() throws {
        // Brightness −0.5 turns the white bar into mid-grey; gamma 1.5 should lift it.
        let (grey, _) = try renderBars(ImageAdjustments(brightness: -0.5))
        let (lifted, _) = try renderBars(ImageAdjustments(brightness: -0.5, gamma: 1.5))
        let before = pixel(grey, x: 20, y: 180).x, after = pixel(lifted, x: 20, y: 180).x
        #expect(abs(before - 128) <= 3)
        #expect(after > before + 30)
    }

    @Test func presetsRoundTrip() {
        for preset in ImageAdjustments.Preset.allCases {
            #expect(preset.values.preset == preset)
        }
        #expect(ImageAdjustments(brightness: 0.1).preset == nil)
    }

    @Test func sharpenedFullHDStaysUnderTwoMilliseconds() throws {
        _ = try renderBars(ImageAdjustments(sharpness: 1), width: 1920, height: 1080) // warm up
        var best = Double.infinity
        for _ in 0..<5 {
            let (_, commandBuffer) = try renderBars(ImageAdjustments(contrast: 1.2, saturation: 1.3, gamma: 1.2, sharpness: 1), width: 1920, height: 1080)
            best = min(best, commandBuffer.gpuEndTime - commandBuffer.gpuStartTime)
        }
        #expect(best < 0.002, "GPU time \(best * 1000) ms")
    }
}
