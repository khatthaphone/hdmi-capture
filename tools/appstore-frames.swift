// App Store screenshot frames: a real app capture on the icon's navy, with a caption above it.
// usage: swift tools/appstore-frames.swift <mac|ipad> <capture.png> "<Headline>" "<Subline>" <out.png>
// Sizes: Mac 2880×1800, iPad 13" landscape 2752×2064 (App Store Connect accepted sizes).
import AppKit

let args = CommandLine.arguments
guard args.count == 6, let shot = NSImage(contentsOfFile: args[2]) else {
    print("usage: appstore-frames <mac|ipad> <capture.png> <headline> <subline> <out.png>")
    exit(1)
}
let size = args[1] == "mac" ? NSSize(width: 2880, height: 1800) : NSSize(width: 2752, height: 2064)
let headline = args[3], subline = args[4], out = args[5]

let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

// Background: the app icon's navy, lighter in the middle.
let navy = NSColor(red: 0.04, green: 0.09, blue: 0.27, alpha: 1)
let lighter = NSColor(red: 0.09, green: 0.20, blue: 0.48, alpha: 1)
NSGradient(starting: lighter, ending: navy)!.draw(in: NSRect(origin: .zero, size: size), relativeCenterPosition: NSPoint(x: 0, y: 0.2))

// Caption.
let paragraph = NSMutableParagraphStyle(); paragraph.alignment = .center
let headAttrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size.width * 0.034, weight: .bold),
                                                .foregroundColor: NSColor.white, .paragraphStyle: paragraph]
let subAttrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: size.width * 0.017, weight: .medium),
                                               .foregroundColor: NSColor(red: 0.98, green: 0.80, blue: 0.36, alpha: 1), .paragraphStyle: paragraph]
let top = size.height * 0.92
headline.draw(in: NSRect(x: 0, y: top - size.width * 0.045, width: size.width, height: size.width * 0.05), withAttributes: headAttrs)
subline.draw(in: NSRect(x: 0, y: top - size.width * 0.075, width: size.width, height: size.width * 0.03), withAttributes: subAttrs)

// The capture, scaled to fit under the caption, with rounded corners and a soft shadow.
let area = NSRect(x: size.width * 0.07, y: size.height * 0.05, width: size.width * 0.86, height: size.height * 0.70)
let scale = min(area.width / shot.size.width, area.height / shot.size.height)
let w = shot.size.width * scale, h = shot.size.height * scale
let frame = NSRect(x: area.midX - w / 2, y: area.midY - h / 2, width: w, height: h)
let path = NSBezierPath(roundedRect: frame, xRadius: size.width * 0.012, yRadius: size.width * 0.012)
let shadow = NSShadow(); shadow.shadowColor = .black.withAlphaComponent(0.5); shadow.shadowBlurRadius = 40; shadow.shadowOffset = NSSize(width: 0, height: -16)
NSGraphicsContext.saveGraphicsState(); shadow.set(); NSColor.black.setFill(); path.fill(); NSGraphicsContext.restoreGraphicsState()
NSGraphicsContext.saveGraphicsState(); path.addClip(); shot.draw(in: frame); NSGraphicsContext.restoreGraphicsState()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
print("wrote \(out) \(Int(size.width))×\(Int(size.height))")
