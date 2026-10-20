import CaptureCore
import Foundation

/// Window/app-level preferences (per-device settings live in `DevicePreset`).
struct ViewerPrefs: Codable, Equatable {
    var floatOnTop = false
    var allSpaces = false
    var cleanMode = false
    var lockAspect = true
    var showStats = false
    var includeNonExternalCameras = false
    var guides = GuideSettings()
    var showMenuBarItem = true
    var recordingCodec: Recorder.Codec = .hevc
    var showRecordingIndicator = true
    /// No Dock icon or app menu; control from the menu-bar item.
    var menuBarOnly = false
    /// Sound follows the focused window or grid tile when several cards are open.
    var audioFollowsFocus = true

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = ViewerPrefs()
        floatOnTop = try c.decodeIfPresent(Bool.self, forKey: .floatOnTop) ?? d.floatOnTop
        allSpaces = try c.decodeIfPresent(Bool.self, forKey: .allSpaces) ?? d.allSpaces
        cleanMode = try c.decodeIfPresent(Bool.self, forKey: .cleanMode) ?? d.cleanMode
        lockAspect = try c.decodeIfPresent(Bool.self, forKey: .lockAspect) ?? d.lockAspect
        showStats = try c.decodeIfPresent(Bool.self, forKey: .showStats) ?? d.showStats
        includeNonExternalCameras = try c.decodeIfPresent(Bool.self, forKey: .includeNonExternalCameras) ?? d.includeNonExternalCameras
        guides = try c.decodeIfPresent(GuideSettings.self, forKey: .guides) ?? d.guides
        showMenuBarItem = try c.decodeIfPresent(Bool.self, forKey: .showMenuBarItem) ?? d.showMenuBarItem
        menuBarOnly = try c.decodeIfPresent(Bool.self, forKey: .menuBarOnly) ?? d.menuBarOnly
        recordingCodec = try c.decodeIfPresent(Recorder.Codec.self, forKey: .recordingCodec) ?? d.recordingCodec
        showRecordingIndicator = try c.decodeIfPresent(Bool.self, forKey: .showRecordingIndicator) ?? d.showRecordingIndicator
        audioFollowsFocus = try c.decodeIfPresent(Bool.self, forKey: .audioFollowsFocus) ?? d.audioFollowsFocus
    }
}
