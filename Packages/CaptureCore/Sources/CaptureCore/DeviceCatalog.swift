import Foundation
import Observation

public struct CaptureDeviceInfo: Hashable, Sendable, Identifiable {
    public enum Kind: String, Sendable {
        case external, continuity, builtIn, other
    }

    /// `AVCaptureDevice.uniqueID`.
    public var id: String
    public var name: String
    public var modelID: String
    public var manufacturer: String
    /// Transport type as four-character code, e.g. "usb ", "bltn".
    public var transport: String
    public var kind: Kind

    public init(id: String, name: String, modelID: String = "", manufacturer: String = "", transport: String = "", kind: Kind) {
        self.id = id
        self.name = name
        self.modelID = modelID
        self.manufacturer = manufacturer
        self.transport = transport
        self.kind = kind
    }

    public var isUSB: Bool { transport == "usb " }
    /// Identity that survives a changed `uniqueID`: same model and name means the same kind of card.
    public var stableKey: String { "model:\(modelID)|\(name)" }
}

/// Source of devices; real implementation wraps AVFoundation, tests inject fakes.
public protocol DeviceProviding: AnyObject {
    func videoDevices(includeNonExternal: Bool) -> [CaptureDeviceInfo]
    func audioDevices() -> [CaptureDeviceInfo]
    func startObserving(_ onChange: @escaping @MainActor () -> Void)
}

/// Pure selection and pairing policy (spec §5.1).
public enum DeviceSelection {
    public static func pickVideo(
        available: [CaptureDeviceInfo],
        connectionOrder: [String],
        remembered: String?,
        current: String?
    ) -> String? {
        let byID = Dictionary(available.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        let hasExternal = available.contains { $0.kind == .external }

        // Keep watching what we're on, unless it's a fallback camera and real capture hardware appeared.
        if let current, let device = byID[current], device.kind == .external || !hasExternal {
            return current
        }
        if let remembered, let device = byID[remembered], device.kind == .external || !hasExternal {
            return remembered
        }
        if let newest = connectionOrder.last(where: { byID[$0]?.kind == .external }) {
            return newest
        }
        return available.first(where: { $0.kind == .external })?.id ?? available.first?.id
    }

    /// Finds the audio half of an HDMI dongle. Returns nil rather than guess,
    /// so we never route the Mac's own microphone to the speakers.
    public static func pairAudio(for video: CaptureDeviceInfo, among audio: [CaptureDeviceInfo]) -> String? {
        let candidates = audio.filter { $0.kind != .builtIn }
        guard !candidates.isEmpty else { return nil }

        if let ids = usbIDs(from: video.modelID),
           let match = candidates.first(where: { usbIDs(from: $0.modelID) == ids }) {
            return match.id
        }
        let videoName = normalized(video.name)
        if let match = candidates.first(where: { normalized($0.name) == videoName }) {
            return match.id
        }
        let videoTokens = significantTokens(video.name)
        if !videoTokens.isEmpty,
           let match = candidates.first(where: { !significantTokens($0.name).isDisjoint(with: videoTokens) }) {
            return match.id
        }
        let usbAudio = candidates.filter(\.isUSB)
        if video.kind == .external, video.isUSB, usbAudio.count == 1 {
            return usbAudio[0].id
        }
        return nil
    }

    /// Extracts (vendor, product) from AVFoundation model IDs. Video devices look like
    /// "UVC Camera VendorID_13407 ProductID_8497"; audio ones like "USB Digital Audio:345F:2131".
    public static func usbIDs(from modelID: String) -> [Int]? {
        if let v = modelID.firstMatch(of: #/VendorID_(\d+)\s+ProductID_(\d+)/#),
           let vendor = Int(v.1), let product = Int(v.2) {
            return [vendor, product]
        }
        if let a = modelID.firstMatch(of: #/:([0-9A-Fa-f]{4}):([0-9A-Fa-f]{4})$/#),
           let vendor = Int(a.1, radix: 16), let product = Int(a.2, radix: 16) {
            return [vendor, product]
        }
        return nil
    }

    private static let genericTokens: Set<String> = [
        "usb", "video", "audio", "capture", "camera", "hd", "hdmi", "digital", "device", "the", "uvc", "uac", "input", "interface",
    ]

    private static func normalized(_ name: String) -> String {
        name.lowercased().filter { $0.isLetter || $0.isNumber }
    }

    private static func significantTokens(_ name: String) -> Set<String> {
        let tokens = name.lowercased().split { !($0.isLetter || $0.isNumber) }.map(String.init)
        return Set(tokens.filter { $0.count >= 3 && !genericTokens.contains($0) && !$0.allSatisfy(\.isNumber) })
    }
}

/// Live list of capture devices with hot-plug updates.
@MainActor
@Observable
public final class DeviceCatalog {
    public private(set) var videoDevices: [CaptureDeviceInfo] = []
    public private(set) var audioDevices: [CaptureDeviceInfo] = []
    /// Video device IDs in the order they appeared; last = most recently connected.
    public private(set) var connectionOrder: [String] = []

    /// Also list built-in / Continuity cameras (handy for testing without a dongle).
    public var includeNonExternalCameras: Bool {
        didSet { if oldValue != includeNonExternalCameras { refresh() } }
    }

    @ObservationIgnored public var onChange: (() -> Void)?
    @ObservationIgnored private let provider: DeviceProviding

    public init(provider: DeviceProviding, includeNonExternalCameras: Bool = false) {
        self.provider = provider
        self.includeNonExternalCameras = includeNonExternalCameras
        provider.startObserving { [weak self] in self?.refresh() }
        refresh()
    }

    public func refresh() {
        videoDevices = provider.videoDevices(includeNonExternal: includeNonExternalCameras)
        audioDevices = provider.audioDevices()
        let present = Set(videoDevices.map(\.id))
        connectionOrder.removeAll { !present.contains($0) }
        for device in videoDevices where !connectionOrder.contains(device.id) {
            connectionOrder.append(device.id)
        }
        onChange?()
    }

    public func video(_ id: String?) -> CaptureDeviceInfo? {
        videoDevices.first { $0.id == id }
    }

    public func audio(_ id: String?) -> CaptureDeviceInfo? {
        audioDevices.first { $0.id == id }
    }
}
