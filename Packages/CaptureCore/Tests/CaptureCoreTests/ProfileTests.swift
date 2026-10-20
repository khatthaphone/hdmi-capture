import Foundation
import Testing
@testable import CaptureCore

struct ProfileTests {
    private func makeStore() throws -> ProfileStore {
        ProfileStore(defaults: try #require(UserDefaults(suiteName: "ProfileTests-\(UUID())")))
    }

    @Test func applyCopiesSettingsButKeepsDeviceSpecificOnes() {
        var preset = DevicePreset()
        preset.audio = .device("usb-audio")
        preset.outputDeviceUID = "airpods"
        preset.volume = 0.4
        let retro = Profile.builtIns.first { $0.name == "Retro" }!
        retro.settings.apply(to: &preset)
        #expect(preset.scale == .integer && preset.filter == .nearest)
        #expect(preset.audio == .device("usb-audio") && preset.outputDeviceUID == "airpods" && preset.volume == 0.4)
        #expect(ProfileSettings(preset: preset) == retro.settings)
    }

    @Test func builtInIDsAreStableAndUnique() {
        #expect(Set(Profile.builtIns.map(\.id)).count == Profile.builtIns.count)
        #expect(Profile.builtIns.allSatisfy { $0.isBuiltIn })
    }

    @Test func addUpdateDelete() throws {
        let store = try makeStore()
        let mine = store.add(name: "Switch 2", symbol: "gamecontroller.fill", settings: ProfileSettings(audioDelayMs: 40))
        #expect(store.all.count == Profile.builtIns.count + 1)
        var renamed = mine
        renamed.name = "Switch"
        store.update(renamed)
        #expect(store.profile(mine.id)?.name == "Switch")
        store.update(Profile.builtIns[0]) // built-ins are immutable
        #expect(store.userProfiles.count == 1)
        store.delete(mine.id)
        #expect(store.userProfiles.isEmpty)
    }

    @Test func exportDeleteImportRoundTrips() throws {
        let store = try makeStore()
        let mine = store.add(name: "PS5", symbol: "playstation.logo", settings: ProfileSettings(scale: .fill, audioDelayMs: 60))
        let data = try ProfileStore.export([mine])
        store.delete(mine.id)
        let imported = try store.importProfiles(from: data)
        #expect(imported.count == 1)
        #expect(imported[0].name == "PS5" && imported[0].settings == mine.settings)
        #expect(imported[0].id != mine.id)
        #expect(!imported[0].isBuiltIn)
    }

    @Test func importRejectsGarbage() throws {
        let store = try makeStore()
        #expect(throws: (any Error).self) { try store.importProfiles(from: Data("nope".utf8)) }
    }
}
