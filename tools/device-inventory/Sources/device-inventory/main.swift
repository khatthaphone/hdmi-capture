import CaptureCore
import Foundation

// Lists external capture devices and their formats. `--json` for machine-readable output.
struct VideoEntry: Encodable {
    var device: DeviceEntry
    var formats: [FormatDescriptor]
    var autoChoice: String?
}

struct DeviceEntry: Encodable {
    var name, id, modelID, manufacturer, transport, kind: String
    init(_ d: CaptureDeviceInfo) {
        (name, id, modelID, manufacturer, transport, kind) = (d.name, d.id, d.modelID, d.manufacturer, d.transport, d.kind.rawValue)
    }
}

struct Inventory: Encodable {
    var video: [VideoEntry]
    var audio: [DeviceEntry]
}

let includeAll = CommandLine.arguments.contains("--all")
let provider = AVDeviceProvider()
let video = provider.videoDevices(includeNonExternal: includeAll)
let audio = provider.audioDevices()

let entries = video.map { device in
    let formats = CaptureEngine.formats(forDeviceID: device.id)
    let choice = FormatSelector.select(from: formats).map { "\($0.format.width)x\($0.format.height) \($0.format.fourCC) @ \($0.frameRate)" }
    return VideoEntry(device: DeviceEntry(device), formats: formats, autoChoice: choice)
}

if CommandLine.arguments.contains("--json") {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    print(String(decoding: try encoder.encode(Inventory(video: entries, audio: audio.map(DeviceEntry.init))), as: UTF8.self))
} else {
    for entry in entries {
        let d = entry.device
        print("🎥 \(d.name) [\(d.kind)] id=\(d.id) model=\(d.modelID) transport=\(d.transport)")
        for f in entry.formats {
            let rates = f.frameRateRanges.map { $0.min == $0.max ? String(format: "%.2f", $0.max) : String(format: "%.2f–%.2f", $0.min, $0.max) }
            print("   \(f.width)x\(f.height) \(f.fourCC)  fps: \(rates.joined(separator: ", "))")
        }
        print("   → auto: \(entry.autoChoice ?? "none")")
        if let paired = DeviceSelection.pairAudio(for: video.first { $0.id == d.id }!, among: audio) {
            print("   → audio pair: \(audio.first { $0.id == paired }!.name)")
        }
    }
    for a in audio {
        print("🎙️ \(a.name) [\(a.kind)] id=\(a.id) model=\(a.modelID) transport=\(a.transport)")
    }
}
