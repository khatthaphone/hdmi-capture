import Metal
import Testing
@testable import CaptureCore

struct CubeLUTTests {
    @Test func parsesIdentity2() throws {
        let text = """
        # comment
        TITLE "Tiny"
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """
        let lut = try CubeLUT(text: text)
        #expect(lut.title == "Tiny")
        #expect(lut == CubeLUT(title: "Tiny", size: 2, domainMin: .zero, domainMax: .one, entries: CubeLUT.identity(size: 2).entries))
    }

    @Test func parsesGenerated33PointWithDomain() throws {
        let identity = CubeLUT.identity(size: 33)
        let body = identity.entries.map { "\($0.x) \($0.y) \($0.z)" }.joined(separator: "\n")
        let lut = try CubeLUT(text: "LUT_3D_SIZE 33\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n" + body)
        #expect(lut.size == 33)
        #expect(lut.entries.count == 35_937)
    }

    @Test func bakes1DCurve() throws {
        let lut = try CubeLUT(text: "LUT_1D_SIZE 2\n1 1 1\n0 0 0") // invert
        #expect(lut.size == 33)
        #expect(lut.entries.first == SIMD3(1, 1, 1))
        #expect(lut.entries.last == SIMD3(0, 0, 0))
    }

    @Test func rejectsMalformed() {
        #expect(throws: CubeLUT.ParseError.missingSize) { try CubeLUT(text: "0 0 0") }
        #expect(throws: CubeLUT.ParseError.wrongEntryCount(expected: 8, found: 1)) { try CubeLUT(text: "LUT_3D_SIZE 2\n0 0 0") }
        #expect(throws: CubeLUT.ParseError.badLine(2)) { try CubeLUT(text: "LUT_3D_SIZE 2\n0 zero 0") }
        #expect(throws: CubeLUT.ParseError.unsupportedSize(200)) { try CubeLUT(text: "LUT_3D_SIZE 200") }
    }

    private func render(lut: CubeLUT?, intensity: Double) throws -> [SIMD3<Int>] {
        let renderer = try #require(MetalRenderer())
        renderer.setLUT(lut)
        let buffer = try #require(TestPattern.fullBars.makePixelBuffer(width: 640, height: 360))
        let device = renderer.device
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: 640, height: 360, mipmapped: false)
        descriptor.usage = [.renderTarget]
        descriptor.storageMode = device.hasUnifiedMemory ? .shared : .managed
        let target = try #require(device.makeTexture(descriptor: descriptor))
        let commandBuffer = try #require(device.makeCommandQueue()?.makeCommandBuffer())
        let textures = renderer.encode(buffer, into: target, options: RenderOptions(filter: .nearest, lutIntensity: intensity), commandBuffer: commandBuffer)
        commandBuffer.commit()
        commandBuffer.waitUntilCompleted()
        _ = textures
        return (0..<8).map { bar in
            var p = [UInt8](repeating: 0, count: 4)
            target.getBytes(&p, bytesPerRow: 640 * 4, from: MTLRegionMake2D(640 * (2 * bar + 1) / 16, 180, 1, 1), mipmapLevel: 0)
            return SIMD3(Int(p[2]), Int(p[1]), Int(p[0]))
        }
    }

    @Test func identityLUTLeavesOutputUnchanged() throws {
        let plain = try render(lut: nil, intensity: 0)
        let graded = try render(lut: .identity(size: 33), intensity: 1)
        for (a, b) in zip(plain, graded) {
            let d = a &- b
            #expect(abs(d.x) <= 1 && abs(d.y) <= 1 && abs(d.z) <= 1, "\(a) vs \(b)")
        }
    }

    @Test func invertLUTInverts() throws {
        let invert = try CubeLUT(text: "LUT_1D_SIZE 2\n1 1 1\n0 0 0")
        let graded = try render(lut: invert, intensity: 1)
        #expect(graded[0].x <= 3)          // white → black
        #expect(graded[7].x >= 252)        // black → white
        let half = try render(lut: invert, intensity: 0.5)
        #expect(abs(half[0].x - 128) <= 3) // 50 % mix
    }
}
