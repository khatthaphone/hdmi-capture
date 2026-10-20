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
    }
}

public final class PresetStore {
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    public func preset(for deviceID: String) -> DevicePreset {
        guard let data = defaults.data(forKey: key(deviceID)),
              let preset = try? JSONDecoder().decode(DevicePreset.self, from: data)
        else { return DevicePreset() }
        return preset
    }

    public func save(_ preset: DevicePreset, for deviceID: String) {
        if let data = try? JSONEncoder().encode(preset) {
            defaults.set(data, forKey: key(deviceID))
        }
    }

    public var lastVideoDeviceID: String? {
        get { defaults.string(forKey: "lastVideoDeviceID") }
        set { defaults.set(newValue, forKey: "lastVideoDeviceID") }
    }

    private func key(_ deviceID: String) -> String { "preset.\(deviceID)" }
}
