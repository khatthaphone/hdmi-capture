import CoreImage
import CoreVideo
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Full-resolution stills of the source frame (not the scaled window contents).
public enum FrameSnapshot {
    private static let context = CIContext(options: [.cacheIntermediates: false])

    /// Converts the frame to sRGB using its own YCbCr matrix and range attachments.
    public static func cgImage(from buffer: CVPixelBuffer) -> CGImage? {
        let image = CIImage(cvPixelBuffer: buffer)
        return context.createCGImage(image, from: image.extent, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
    }

    public static func pngData(from buffer: CVPixelBuffer) -> Data? {
        guard let image = cgImage(from: buffer) else { return nil }
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    /// "HDMI Captain 2026-10-20 at 14.05.09.png", unique within `folder`.
    public static func fileURL(in folder: URL, date: Date = Date()) -> URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        let base = "HDMI Captain \(formatter.string(from: date))"
        var url = folder.appendingPathComponent(base).appendingPathExtension("png")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent("\(base) \(n)").appendingPathExtension("png")
            n += 1
        }
        return url
    }
}
