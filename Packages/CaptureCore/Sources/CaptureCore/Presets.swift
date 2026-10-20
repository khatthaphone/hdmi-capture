import Foundation

/// Per-device settings, remembered by video device `uniqueID`.
public struct DevicePreset: Codable, Equatable, Sendable {
    public enum AudioSelection: Codable, Hashable, Sendable {
        case automatic
        case none
        case device(String)
    }

    public var format: FormatPreference = .auto
    public var audio: AudioSelection = .automatic
    public var volume: Float = 1
    public var muted = false
    public var scale: ScaleMode = .fit
    public var filter: FilterMode = .linear
    public var colorRange: ColorRange = .auto
    public var audioDelayMs = 0
    public var channelMode: ChannelMode = .auto
    /// Core Audio output device UID; nil = system default.
    public var outputDeviceUID: String?
    public var adjustments = ImageAdjustments()
    /// Security-scoped bookmark to a .cube file chosen by the user.
    public var lutBookmark: Data?
    public var lutName: String?
    public var lutIntensity: Double = 1
    /// The profile last applied to this card (shown in menus; settings live in the fields above).
    public var profileID: UUID?

    public init() {}

    // Tolerates missing keys so older saved presets keep decoding as fields are added.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = DevicePreset()
        format = try c.decodeIfPresent(FormatPreference.self, forKey: .format) ?? defaults.format
        audio = try c.decodeIfPresent(AudioSelection.self, forKey: .audio) ?? defaults.audio
        volume = try c.decodeIfPresent(Float.self, forKey: .volume) ?? defaults.volume
        muted = try c.decodeIfPresent(Bool.self, forKey: .muted) ?? defaults.muted
        scale = try c.decodeIfPresent(ScaleMode.self, forKey: .scale) ?? defaults.scale
        filter = try c.decodeIfPresent(FilterMode.self, forKey: .filter) ?? defaults.filter
        colorRange = try c.decodeIfPresent(ColorRange.self, forKey: .colorRange) ?? defaults.colorRange
        audioDelayMs = try c.decodeIfPresent(Int.self, forKey: .audioDelayMs) ?? defaults.audioDelayMs
        channelMode = try c.decodeIfPresent(ChannelMode.self, forKey: .channelMode) ?? defaults.channelMode
        outputDeviceUID = try c.decodeIfPresent(String.self, forKey: .outputDeviceUID)
        adjustments = try c.decodeIfPresent(ImageAdjustments.self, forKey: .adjustments) ?? defaults.adjustments
        lutBookmark = try c.decodeIfPresent(Data.self, forKey: .lutBookmark)
        lutName = try c.decodeIfPresent(String.self, forKey: .lutName)
        lutIntensity = try c.decodeIfPresent(Double.self, forKey: .lutIntensity) ?? defaults.lutIntensity
        profileID = try c.decodeIfPresent(UUID.self, forKey: .profileID)
    }
}

public final class PresetStore {
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    /// The card's settings. `stableKey` (see `CaptureDeviceInfo.stableKey`) finds them again when
    /// the card comes back with a different `uniqueID` (re-enumeration, iPadOS reconnects).
    public func preset(for deviceID: String, stableKey: String? = nil) -> DevicePreset {
        for key in [key(deviceID)] + (stableKey.map { [self.key($0)] } ?? []) {
            if let data = defaults.data(forKey: key), let preset = try? JSONDecoder().decode(DevicePreset.self, from: data) {
                return preset
            }
        }
        return DevicePreset()
    }

    public func save(_ preset: DevicePreset, for deviceID: String, stableKey: String? = nil) {
        guard let data = try? JSONEncoder().encode(preset) else { return }
        defaults.set(data, forKey: key(deviceID))
        if let stableKey { defaults.set(data, forKey: key(stableKey)) }
    }

    public var lastVideoDeviceID: String? {
        get { defaults.string(forKey: "lastVideoDeviceID") }
        set { defaults.set(newValue, forKey: "lastVideoDeviceID") }
    }

    private func key(_ deviceID: String) -> String { "preset.\(deviceID)" }
}
