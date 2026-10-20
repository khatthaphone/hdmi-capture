import Foundation
import Testing
@testable import CaptureCore

struct PresetTests {
    @Test func findsPresetAfterUniqueIDChange() throws {
        let defaults = try #require(UserDefaults(suiteName: "PresetTests-\(UUID())"))
        let store = PresetStore(defaults: defaults)
        let card = CaptureDeviceInfo(id: "0x1000001de1f105", name: "UGREEN-25854", modelID: "UVC Camera VendorID_7655 ProductID_61701", kind: .external)
        var preset = DevicePreset()
        preset.muted = true
        store.save(preset, for: card.id, stableKey: card.stableKey)

        // Same card after re-enumeration: new uniqueID, same model and name.
        let again = CaptureDeviceInfo(id: "0x21000001de1f105", name: card.name, modelID: card.modelID, kind: .external)
        #expect(store.preset(for: again.id, stableKey: again.stableKey).muted)
        // A different card doesn't inherit it.
        let other = CaptureDeviceInfo(id: "0x100000345f2131", name: "UGREEN HDMI Capture", modelID: "UVC Camera VendorID_13407 ProductID_8497", kind: .external)
        #expect(!store.preset(for: other.id, stableKey: other.stableKey).muted)
    }

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
