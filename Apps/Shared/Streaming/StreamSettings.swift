import Foundation
import Security

/// Where and how to stream. The stream key is not stored here — it lives in the Keychain.
struct StreamSettings: Codable, Equatable {
    enum Service: String, Codable, CaseIterable, Identifiable {
        case youtube, twitch, custom
        var id: String { rawValue }

        var title: String {
            switch self {
            case .youtube: "YouTube"
            case .twitch: "Twitch"
            case .custom: "Custom RTMP / SRT"
            }
        }

        /// Default ingest server (stream key is appended as the last path component).
        var defaultServer: String {
            switch self {
            case .youtube: "rtmp://a.rtmp.youtube.com/live2"
            case .twitch: "rtmp://live.twitch.tv/app"
            case .custom: ""
            }
        }
    }

    enum Resolution: String, Codable, CaseIterable, Identifiable {
        case source, p1080, p720
        var id: String { rawValue }

        var title: String {
            switch self {
            case .source: "Same as source"
            case .p1080: "1080p"
            case .p720: "720p"
            }
        }

        /// Output size for a given source size (keeps the source's aspect).
        func size(for source: CGSize) -> CGSize {
            let height: CGFloat
            switch self {
            case .source: return source
            case .p1080: height = 1080
            case .p720: height = 720
            }
            guard source.height > height else { return source }
            let width = (source.width * height / source.height / 2).rounded() * 2
            return CGSize(width: width, height: height)
        }
    }

    var service: Service = .youtube
    var server = Service.youtube.defaultServer
    var resolution: Resolution = .source
    /// Video bitrate in kbit/s.
    var videoBitrate = 6000

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = StreamSettings()
        service = try c.decodeIfPresent(Service.self, forKey: .service) ?? d.service
        server = try c.decodeIfPresent(String.self, forKey: .server) ?? d.server
        resolution = try c.decodeIfPresent(Resolution.self, forKey: .resolution) ?? d.resolution
        videoBitrate = try c.decodeIfPresent(Int.self, forKey: .videoBitrate) ?? d.videoBitrate
    }

    /// Full publish URL: RTMP appends the key as the stream name; SRT passes it as `streamid`.
    func publishURL(key: String) -> URL? {
        let base = server.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !base.isEmpty, !key.isEmpty else { return nil }
        if base.lowercased().hasPrefix("srt://") {
            guard var components = URLComponents(string: base) else { return nil }
            var items = components.queryItems ?? []
            items.removeAll { $0.name == "streamid" }
            items.append(URLQueryItem(name: "streamid", value: key))
            components.queryItems = items
            return components.url
        }
        let trimmed = base.hasSuffix("/") ? String(base.dropLast()) : base
        return URL(string: "\(trimmed)/\(key)")
    }

    /// The URL with the key replaced, safe for logs and UI.
    func redactedURL() -> String {
        publishURL(key: "••••")?.absoluteString ?? server
    }
}

/// Stream keys live only in the Keychain (never in UserDefaults or logs).
enum StreamKeyStore {
    private static let service = "HDMI Captain stream key"

    static func key(for account: String) -> String? {
        var item: CFTypeRef?
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: account,
            kSecReturnData: true, kSecMatchLimit: kSecMatchLimitOne,
        ]
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    @discardableResult
    static func setKey(_ key: String?, for account: String) -> Bool {
        let query: [CFString: Any] = [kSecClass: kSecClassGenericPassword, kSecAttrService: service, kSecAttrAccount: account]
        SecItemDelete(query as CFDictionary)
        guard let key, !key.isEmpty else { return true }
        var add = query
        add[kSecValueData] = Data(key.utf8)
        add[kSecAttrAccessible] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }
}
