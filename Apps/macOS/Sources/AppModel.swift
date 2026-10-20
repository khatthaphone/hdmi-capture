import AppKit
import AVFoundation
import CaptureCore
import Observation

/// App-wide state: device discovery, permissions, preferences, the per-card sessions, and which
/// card the main window and the speakers follow. Views talk to a `ViewerModel`.
@MainActor
@Observable
final class AppModel {
    enum Permission: Equatable {
        case unknown, needed, denied, granted
    }

    private(set) var permission: Permission = .unknown
    let catalog: DeviceCatalog
    /// One session per card that something is showing (main window, card windows, grid tiles).
    private(set) var sessions: [String: DeviceSession] = [:]
    /// The card the main window shows (automatic pick or the user's choice).
    private(set) var mainDeviceID: String?
    /// The card whose sound is playing.
    private(set) var audioFocusID: String?
    /// The viewer in the key window; menus and Settings act on it.
    private(set) var activeViewer: ViewerModel?

    var prefs: ViewerPrefs {
        didSet {
            savePrefs()
            catalog.includeNonExternalCameras = prefs.includeNonExternalCameras
            if prefs.menuBarOnly != oldValue.menuBarOnly || prefs.showMenuBarItem != oldValue.showMenuBarItem {
                applyActivationPolicy()
            }
            if prefs.replayEnabled != oldValue.replayEnabled || prefs.replaySeconds != oldValue.replaySeconds {
                sessions.values.forEach { $0.configureReplay(enabled: prefs.replayEnabled, seconds: prefs.replaySeconds) }
            }
        }
    }

    /// Live streaming (Pro); one stream at a time.
    let streamer = Streamer()
    /// Drives the Go Live sheet.
    var isShowingStreamSetup = false

    /// Built-in and user profiles (refreshed after every change).
    private(set) var profiles: [Profile] = []
    @ObservationIgnored let profileStore = ProfileStore()

    @ObservationIgnored private(set) var mainViewer: ViewerModel?
    @ObservationIgnored private var viewers: [WeakViewer] = []
    @ObservationIgnored private let store = PresetStore()
    @ObservationIgnored private var manualVideoID: String?
    @ObservationIgnored private var names: [String: String] = [:]
    @ObservationIgnored private var observers: [NSObjectProtocol] = []

    /// Viewer that menus, Settings and the menu-bar panel act on.
    var currentViewer: ViewerModel? { activeViewer ?? mainViewer }

    init() {
        var prefs = Self.loadPrefs()
        #if DEBUG
        // QA overrides from launch arguments; applied in memory only (init doesn't trigger savePrefs).
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: "qaBuiltInCameras") { prefs.includeNonExternalCameras = true }
        if defaults.bool(forKey: "qaShowStats") { prefs.showStats = true }
        #endif
        self.prefs = prefs
        catalog = DeviceCatalog(provider: AVDeviceProvider(), includeNonExternalCameras: prefs.includeNonExternalCameras)
        catalog.onChange = { [weak self] in self?.reconcile() }
        profiles = profileStore.all
        observeSystemEvents()
        Task { await requestPermissions() }
    }

    // MARK: Viewers

    func register(_ viewer: ViewerModel) {
        viewers.removeAll { $0.viewer == nil || $0.viewer === viewer }
        viewers.append(WeakViewer(viewer))
        if viewer.source == .automatic {
            mainViewer = viewer
            applyLaunchArguments(to: viewer)
        }
        reconcile()
    }

    func unregister(_ viewer: ViewerModel) {
        viewers.removeAll { $0.viewer == nil || $0.viewer === viewer }
        if activeViewer === viewer { activeViewer = nil }
        reconcile()
    }

    func viewerBecameActive(_ viewer: ViewerModel) {
        activeViewer = viewer
        if prefs.audioFollowsFocus, let id = viewer.deviceID { setAudioFocus(id) }
    }

    func setAudioFocus(_ deviceID: String) {
        audioFocusID = deviceID
        applyAudioFocus()
    }

    /// Name for a card even while it's unplugged.
    func knownName(for deviceID: String) -> String {
        catalog.video(deviceID)?.name ?? names[deviceID] ?? "Capture card"
    }

    // MARK: Profiles

    /// Saves the card's current settings as a new user profile.
    @discardableResult
    func saveProfile(from session: DeviceSession, name: String? = nil) -> Profile {
        let count = profileStore.userProfiles.count + 1
        let profile = profileStore.add(name: name ?? "\(session.deviceName) \(count)", settings: ProfileSettings(preset: session.preset))
        session.preset.profileID = profile.id
        profiles = profileStore.all
        return profile
    }

    func updateProfile(_ profile: Profile) {
        profileStore.update(profile)
        profiles = profileStore.all
    }

    func deleteProfile(_ id: UUID) {
        profileStore.delete(id)
        profiles = profileStore.all
    }

    func exportProfiles(to url: URL) throws {
        try ProfileStore.export(profileStore.userProfiles).write(to: url)
    }

    func importProfiles(from url: URL) throws -> Int {
        let imported = try profileStore.importProfiles(from: Data(contentsOf: url))
        profiles = profileStore.all
        return imported.count
    }

    // MARK: Streaming (Pro — callers go through EntitlementStore.gate)

    static let streamKeyAccount = "default"

    /// Starts streaming the card in the active window with the saved settings and Keychain key.
    func startStreaming(urlOverride: URL? = nil) {
        guard let viewer = currentViewer, let session = viewer.session, let info = session.liveInfo, session.state == .live else {
            currentViewer?.showToast("Nothing live to stream")
            return
        }
        let key = StreamKeyStore.key(for: Self.streamKeyAccount) ?? ""
        guard let url = urlOverride ?? prefs.stream.publishURL(key: key) else {
            isShowingStreamSetup = true
            return
        }
        let settings = prefs.stream
        Task {
            await streamer.start(device: session, url: url, settings: settings,
                                 sourceSize: CGSize(width: info.format.width, height: info.format.height), frameRate: info.frameRate)
            if case let .failed(message) = streamer.state { viewer.showToast("Couldn't go live: \(message)") }
        }
    }

    func stopStreaming() {
        Task { await streamer.stop() }
    }

    // MARK: Actions

    /// Main window: show this card (sticks until the user picks another).
    func selectVideo(_ id: String) {
        manualVideoID = id
        store.lastVideoDeviceID = id
        reconcile()
    }

    func retry() {
        sessions.values.forEach { $0.forceRestart() }
        Task { await requestPermissions() }
    }

    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Camera") {
            NSWorkspace.shared.open(url)
        }
    }

    func outputDevices() -> [AudioOutputDevice] {
        AudioDevices.outputDevices()
    }

    func showRecordingsInFinder() {
        let folder = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("HDMI Captain", isDirectory: true)
        if let url = sessions.values.compactMap(\.lastRecordingURL).max(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            NSWorkspace.shared.open(folder)
        }
    }

    /// Dock icon + menus normally; accessory app (menu-bar item only) when requested.
    func applyActivationPolicy() {
        let accessory = prefs.menuBarOnly && prefs.showMenuBarItem
        NSApp.setActivationPolicy(accessory ? .accessory : .regular)
        if !accessory { NSApp.activate() }
    }

    /// With a menu-bar item the app keeps running (and capturing) after the window closes.
    var keepsRunningWithoutWindow: Bool { prefs.showMenuBarItem }

    // MARK: Permissions

    private func requestPermissions() async {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            break
        case .notDetermined:
            permission = .needed
            guard await AVCaptureDevice.requestAccess(for: .video) else {
                permission = .denied
                return
            }
        default:
            permission = .denied
            return
        }
        if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            _ = await AVCaptureDevice.requestAccess(for: .audio)
        }
        permission = .granted
        catalog.refresh()
    }

    // MARK: Device policy

    /// Brings sessions in line with connected cards and open viewers. Called on every hot-plug,
    /// viewer change and preset change that affects capture.
    func reconcile() {
        guard permission == .granted else { return }
        catalog.videoDevices.forEach { names[$0.id] = $0.name }

        if let manualVideoID, catalog.video(manualVideoID) != nil {
            mainDeviceID = manualVideoID
        } else {
            mainDeviceID = DeviceSelection.pickVideo(
                available: catalog.videoDevices,
                connectionOrder: catalog.connectionOrder,
                remembered: store.lastVideoDeviceID,
                current: mainDeviceID
            )
        }
        if let mainDeviceID { store.lastVideoDeviceID = mainDeviceID }

        // Cards that something shows: the main window's, plus every pinned window/tile.
        var needed = Set<String>()
        if let mainDeviceID { needed.insert(mainDeviceID) }
        for viewer in viewers.compactMap(\.viewer) {
            if case let .pinned(id) = viewer.source { needed.insert(id) }
        }

        for id in needed {
            let device = catalog.video(id)
            if sessions[id] == nil {
                guard let device else { continue }
                sessions[id] = makeSession(for: device)
            }
            sessions[id]?.update(device: device, catalog: catalog)
        }
        for (id, session) in sessions where !needed.contains(id) {
            session.shutdown()
            sessions[id] = nil
        }

        if audioFocusID.map({ !needed.contains($0) }) ?? true {
            audioFocusID = activeViewer?.deviceID ?? mainDeviceID
        }
        applyAudioFocus()
        viewers.compactMap(\.viewer).forEach { $0.sync() }
    }

    private func makeSession(for device: CaptureDeviceInfo) -> DeviceSession {
        let session = DeviceSession(device: device, store: store)
        session.onNeedsReconcile = { [weak self] in self?.reconcile() }
        session.onMessage = { [weak self] message in self?.broadcast(message, about: device.id) }
        session.recordingCodec = { [weak self] in self?.prefs.recordingCodec ?? .hevc }
        session.configureReplay(enabled: prefs.replayEnabled, seconds: prefs.replaySeconds)
        return session
    }

    private func applyAudioFocus() {
        for (id, session) in sessions {
            session.audioFocused = id == audioFocusID
        }
    }

    private func broadcast(_ message: String, about deviceID: String) {
        let targets = viewers.compactMap(\.viewer).filter { $0.deviceID == deviceID }
        (targets.isEmpty ? [currentViewer].compactMap { $0 } : targets).forEach { $0.showToast(message) }
    }

    // MARK: System events

    private func observeSystemEvents() {
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.screensDidWakeNotification, NSWorkspace.sessionDidBecomeActiveNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.sessions.values.forEach { $0.revalidate() }
                }
            })
        }
    }

    // MARK: Test patterns (shared cache)

    private static var patternCache: [TestPattern: CVPixelBuffer] = [:]

    static func patternBuffer(_ pattern: TestPattern) -> CVPixelBuffer? {
        if let cached = patternCache[pattern] { return cached }
        let buffer = pattern.makePixelBuffer()
        patternCache[pattern] = buffer
        return buffer
    }

    /// `-testPattern grid` starts on a pattern; DEBUG `-qaAction …` runs an unattended check (see CLAUDE.md).
    private func applyLaunchArguments(to viewer: ViewerModel) {
        if let name = UserDefaults.standard.string(forKey: "testPattern") {
            viewer.testPattern = TestPattern(rawValue: name)
        }
        #if DEBUG
        guard let action = UserDefaults.standard.string(forKey: "qaAction") else { return }
        Task {
            try? await Task.sleep(for: .seconds(1.5))
            switch action {
            case "screenshot": viewer.saveScreenshot()
            case "copy": viewer.copyFrame()
            case "actualPixels": viewer.setWindowSize(scale: 1, actualPixels: true)
            case "size50": viewer.setWindowSize(scale: 0.5)
            case "record3s":
                viewer.toggleRecording()
                try? await Task.sleep(for: .seconds(3))
                viewer.toggleRecording()
            case "stream10s":
                // `-qaStreamURL rtmp://127.0.0.1:1935/live/test` (e.g. an `ffmpeg -listen 1` server).
                guard let url = UserDefaults.standard.string(forKey: "qaStreamURL").flatMap(URL.init(string:)) else { break }
                try? await Task.sleep(for: .seconds(2))
                startStreaming(urlOverride: url)
                try? await Task.sleep(for: .seconds(10))
                stopStreaming()
            default: break
            }
        }
        #endif
    }

    // MARK: Persistence

    private static func loadPrefs() -> ViewerPrefs {
        guard let data = UserDefaults.standard.data(forKey: "viewerPrefs"),
              let prefs = try? JSONDecoder().decode(ViewerPrefs.self, from: data)
        else { return ViewerPrefs() }
        return prefs
    }

    private func savePrefs() {
        if let data = try? JSONEncoder().encode(prefs) {
            UserDefaults.standard.set(data, forKey: "viewerPrefs")
        }
    }
}

private struct WeakViewer {
    weak var viewer: ViewerModel?
    init(_ viewer: ViewerModel) { self.viewer = viewer }
}
