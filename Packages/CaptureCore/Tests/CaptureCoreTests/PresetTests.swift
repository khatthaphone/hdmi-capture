import Foundation
import Testing
@testable import CaptureCore

struct PresetTests {
    @Test func roundTripsThroughStore() throws {
        let defaults = try #require(UserDefaults(suiteName: "PresetTests-\(UUID())"))
        let store = PresetStore(defaults: defaults)
        var preset = DevicePreset()
        preset.format = .pinned(width: 1920, height: 1080, frameRate: 30)
        preset.audio = .device("audio-1")
        preset.scale = .integer
        preset.muted = true
        store.save(preset, for: "dev")
        #expect(store.preset(for: "dev") == preset)
        #expect(store.preset(for: "other") == DevicePreset())
    }

    @Test func decodesOlderPresetWithMissingKeys() throws {
        let json = #"{"volume": 0.5}"#.data(using: .utf8)!
        let preset = try JSONDecoder().decode(DevicePreset.self, from: json)
        #expect(preset.volume == 0.5)
        #expect(preset.format == .auto)
        #expect(preset.scale == .fit)
    }
}
