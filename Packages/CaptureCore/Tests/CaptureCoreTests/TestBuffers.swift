import CoreVideo
import Metal

/// Builds 4:2:0 bi-planar pixel buffers for tests. `pixel(x, y)` returns (Y, Cb, Cr) in 0…255.
func makeYCbCrBuffer(width: Int = 64, height: Int = 36, fullRange: Bool = false,
                     pixel: (Int, Int) -> (UInt8, UInt8, UInt8)) -> CVPixelBuffer {
    let format = fullRange ? kCVPixelFormatType_420YpCbCr8BiPlanarFullRange : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
    let attrs = [kCVPixelBufferMetalCompatibilityKey as String: true, kCVPixelBufferIOSurfacePropertiesKey as String: [:]] as CFDictionary
    var buffer: CVPixelBuffer?
    CVPixelBufferCreate(nil, width, height, format, attrs, &buffer)
    let pb = buffer!
    CVPixelBufferLockBaseAddress(pb, [])
    let y = CVPixelBufferGetBaseAddressOfPlane(pb, 0)!.assumingMemoryBound(to: UInt8.self)
    let c = CVPixelBufferGetBaseAddressOfPlane(pb, 1)!.assumingMemoryBound(to: UInt8.self)
    let yStride = CVPixelBufferGetBytesPerRowOfPlane(pb, 0)
    let cStride = CVPixelBufferGetBytesPerRowOfPlane(pb, 1)
    for row in 0..<height {
        for col in 0..<width {
            let (py, pcb, pcr) = pixel(col, row)
            y[row * yStride + col] = py
            if row % 2 == 0, col % 2 == 0 {
                c[(row / 2) * cStride + col] = pcb
                c[(row / 2) * cStride + col + 1] = pcr
            }
        }
    }
    CVPixelBufferUnlockBaseAddress(pb, [])
    return pb
}

extension MTLStorageMode {
    /// CPU-readable storage for render targets read back in tests (`.managed` exists only on macOS).
    static func readable(on device: MTLDevice) -> MTLStorageMode {
        #if os(macOS)
        device.hasUnifiedMemory ? .shared : .managed
        #else
        .shared
        #endif
    }
}
