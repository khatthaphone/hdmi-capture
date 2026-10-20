import Foundation

/// The parts of a card's settings a profile captures (not device-specific things like audio input or output device).
public struct ProfileSettings: Codable, Equatable, Sendable {
    public var format: FormatPreference = .auto
    public var scale: ScaleMode = .fit
    public var filter: FilterMode = .linear
    public var colorRange: ColorRange = .auto
    public var adjustments = ImageAdjustments()
    public var audioDelayMs = 0
    public var channelMode: ChannelMode = .auto

    public init(format: FormatPreference = .auto, scale: ScaleMode = .fit, filter: FilterMode = .linear,
                colorRange: ColorRange = .auto, adjustments: ImageAdjustments = ImageAdjustments(),
                audioDelayMs: Int = 0, channelMode: ChannelMode = .auto) {
        self.format = format
        self.scale = scale
        self.filter = filter
        self.colorRange = colorRange
        self.adjustments = adjustments
        self.audioDelayMs = audioDelayMs
        self.channelMode = channelMode
    }

    public init(preset: DevicePreset) {
        self.init(format: preset.format, scale: preset.scale, filter: preset.filter, colorRange: preset.colorRange,
                  adjustments: preset.adjustments, audioDelayMs: preset.audioDelayMs, channelMode: preset.channelMode)
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = ProfileSettings()
        format = try c.decodeIfPresent(FormatPreference.self, forKey: .format) ?? d.format
        scale = try c.decodeIfPresent(ScaleMode.self, forKey: .scale) ?? d.scale
        filter = try c.decodeIfPresent(FilterMode.self, forKey: .filter) ?? d.filter
        colorRange = try c.decodeIfPresent(ColorRange.self, forKey: .colorRange) ?? d.colorRange
        adjustments = try c.decodeIfPresent(ImageAdjustments.self, forKey: .adjustments) ?? d.adjustments
        audioDelayMs = try c.decodeIfPresent(Int.self, forKey: .audioDelayMs) ?? d.audioDelayMs
        channelMode = try c.decodeIfPresent(ChannelMode.self, forKey: .channelMode) ?? d.channelMode
    }

    public func apply(to preset: inout DevicePreset) {
        preset.format = format
        preset.scale = scale
        preset.filter = filter
        preset.colorRange = colorRange
        preset.adjustments = adjustments
        preset.audioDelayMs = audioDelayMs
        preset.channelMode = channelMode
    }
}

/// A named bundle of settings, e.g. "Retro" for an old console or "Camera" for a mirrorless body.
public struct Profile: Codable, Equatable, Identifiable, Sendable {
    public var id: UUID
    public var name: String
    /// SF Symbol name.
    public var symbol: String
    public var settings: ProfileSettings
    public var isBuiltIn: Bool

    public init(id: UUID = UUID(), name: String, symbol: String, settings: ProfileSettings, isBuiltIn: Bool = false) {
        self.id = id
        self.name = name
        self.symbol = symbol
        self.settings = settings
        self.isBuiltIn = isBuiltIn
    }

    public static let builtIns: [Profile] = [
        Profile(id: UUID(uuidString: "00000000-0000-0000-0000-00000000B001")!, name: "Modern Console", symbol: "gamecontroller",
                settings: ProfileSettings(), isBuiltIn: true),
        Profile(id: UUID(uuidString: "00000000-0000-0000-0000-00000000B002")!, name: "Retro", symbol: "arcade.stick",
                settings: ProfileSettings(scale: .integer, filter: .nearest), isBuiltIn: true),
        Profile(id: UUID(uuidString: "00000000-0000-0000-0000-00000000B003")!, name: "Camera", symbol: "camera",
                settings: ProfileSettings(adjustments: ImageAdjustments.Preset.cinema.values), isBuiltIn: true),
        Profile(id: UUID(uuidString: "00000000-0000-0000-0000-00000000B004")!, name: "Desktop", symbol: "desktopcomputer",
                settings: ProfileSettings(format: .pinned(width: 1920, height: 1080, frameRate: nil), filter: .linear,
                                          adjustments: ImageAdjustments(sharpness: 0.25)), isBuiltIn: true),
    ]
}

/// Built-in plus user profiles, persisted in UserDefaults; JSON import/export for sharing.
public final class ProfileStore {
    private let defaults: UserDefaults
    private let key = "userProfiles"

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    public var userProfiles: [Profile] {
        get {
            guard let data = defaults.data(forKey: key), let profiles = try? JSONDecoder().decode([Profile].self, from: data) else { return [] }
            return profiles
        }
        set {
            if let data = try? JSONEncoder().encode(newValue.filter { !$0.isBuiltIn }) {
                defaults.set(data, forKey: key)
            }
        }
    }

    public var all: [Profile] { Profile.builtIns + userProfiles }

    public func profile(_ id: UUID?) -> Profile? {
        all.first { $0.id == id }
    }

    @discardableResult
    public func add(name: String, symbol: String = "star", settings: ProfileSettings) -> Profile {
        let profile = Profile(name: name, symbol: symbol, settings: settings)
        userProfiles.append(profile)
        return profile
    }

    public func update(_ profile: Profile) {
        guard !profile.isBuiltIn else { return }
        userProfiles = userProfiles.map { $0.id == profile.id ? profile : $0 }
    }

    public func delete(_ id: UUID) {
        userProfiles.removeAll { $0.id == id }
    }

    public static func export(_ profiles: [Profile]) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(profiles.map { var p = $0; p.isBuiltIn = false; return p })
    }

    /// Imports profiles as new user profiles (fresh ids, so re-importing never clobbers).
    @discardableResult
    public func importProfiles(from data: Data) throws -> [Profile] {
        let imported = try JSONDecoder().decode([Profile].self, from: data).map {
            Profile(name: $0.name, symbol: $0.symbol, settings: $0.settings)
        }
        userProfiles += imported
        return imported
    }
}
