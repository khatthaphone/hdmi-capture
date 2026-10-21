#if os(macOS)
import AppKit
#else
import UIKit
#endif
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
    /// Video works without it, but there's no sound; the viewer shows a banner.
    private(set) var microphoneDenied = false
    /// Connection Help sheet (no signal, HDCP, no sound, card not found).
    var isShowingHelp = false
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
            #if os(macOS)
            if prefs.menuBarOnly != oldValue.menuBarOnly || prefs.showMenuBarItem != oldValue.showMenuBarItem {
                applyActivationPolicy()
            }
            #endif
            if prefs.recentEnabled != oldValue.recentEnabled || prefs.recentMinutes != oldValue.recentMinutes {
                sessions.values.forEach { $0.configureRecent(enabled: prefs.recentEnabled, minutes: prefs.recentMinutes) }
            }
        }
    }

    #if os(macOS)
    /// Live streaming (Pro); one stream at a time.
    let streamer = Streamer()
    /// Drives the Go Live sheet.
    var isShowingStreamSetup = false
    /// Video › Latency Test… (task 035).
    var isShowingLatencyTest = false
    #endif

    #if os(iOS)
    /// iPad Settings sheet (⋯ menu, ⌘,).
    var isShowingSettings = false
    #endif

    /// Built-in and user profiles (refreshed after every change).
    private(set) var profiles: [Profile] = []
    @ObservationIgnored let profileStore = ProfileStore()

    @ObservationIgnored private(set) var mainViewer: ViewerModel?
    @ObservationIgnored private var viewers: [WeakViewer] = []
    @ObservationIgnored private let store = PresetStore()
    @ObservationIgnored private var manualVideoID: String?
    @ObservationIgnored private var names: [String: String] = [:]
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    #if DEBUG
    @ObservationIgnored private var soak: QASoak?
    #endif

    /// Viewer that menus, Settings and the menu-bar panel act on.
    var currentViewer: ViewerModel? { activeViewer ?? mainViewer }

    init() {
        var prefs = Self.loadPrefs()
        #if DEBUG
        // QA overrides from launch arguments; applied in memory only (init doesn't trigger savePrefs).
        let defaults = UserDefaults.standard
        if defaults.bool(forKey: "qaBuiltInCameras") { prefs.includeNonExternalCameras = true }
        if defaults.object(forKey: "qaShowStats") != nil { prefs.showStats = defaults.bool(forKey: "qaShowStats") }
        #endif
        self.prefs = prefs
        catalog = DeviceCatalog(provider: AVDeviceProvider(), includeNonExternalCameras: prefs.includeNonExternalCameras)
        catalog.onChange = { [weak self] in self?.reconcile() }
        profiles = profileStore.all
        // Leftover Save Recent segments from a previous run (crash or force quit).
        try? FileManager.default.removeItem(at: DeviceSession.recentRoot)
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

    #if os(macOS)
    // MARK: Streaming (Pro — callers go through EntitlementStore.gate)

    static let streamKeyAccount = "default"

    /// Starts streaming the card in the active window with the saved settings and Keychain key.
    func startStreaming(urlOverride: URL? = nil) {
        guard let viewer = currentViewer, let session = viewer.session, let info = session.liveInfo, session.state == .live else {
            currentViewer?.showToast("Connect a capture card to go live", .warning)
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
            if case let .failed(message) = streamer.state { viewer.showToast("Couldn't go live: \(message)", .error) }
        }
    }

    func stopStreaming() {
        Task { await streamer.stop() }
    }
    #endif

    // MARK: Actions

    /// Main window: show this card (sticks until the user picks another).
    func selectVideo(_ id: String) {
        manualVideoID = id
        store.lastVideoDeviceID = id
        reconcile()
    }

    func retry() {
        sessions.values.forEach { $0.forceRestart() }
        Task { await requestPermissions(prompting: true) }
    }

    func openPrivacySettings(microphone: Bool = false) {
        #if os(macOS)
        let pane = microphone ? "Privacy_Microphone" : "Privacy_Camera"
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
        #else
        if let url = URL(string: UIApplication.openSettingsURLString) {
            UIApplication.shared.open(url)
        }
        #endif
    }

    #if os(macOS)
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
    #endif

    // MARK: Permissions

    /// First run: the welcome screen explains camera/microphone access before any system prompt;
    /// `continueFromWelcome()` asks. Later launches go straight through.
    private func requestPermissions(prompting: Bool = false) async {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            break
        case .notDetermined:
            permission = .needed
            guard prompting else { return }
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
        microphoneDenied = AVCaptureDevice.authorizationStatus(for: .audio) == .denied
        permission = .granted
        catalog.refresh()
    }

    /// The welcome screen's Continue: shows the system camera (then microphone) prompts.
    func continueFromWelcome() {
        Task { await requestPermissions(prompting: true) }
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
        session.onMessage = { [weak self] message, kind in self?.broadcast(message, kind, about: device.id) }
        session.recordingCodec = { [weak self] in self?.prefs.recordingCodec ?? .hevc }
        session.configureRecent(enabled: prefs.recentEnabled, minutes: prefs.recentMinutes)
        return session
    }

    private func applyAudioFocus() {
        for (id, session) in sessions {
            session.audioFocused = id == audioFocusID
        }
    }

    private func broadcast(_ message: String, _ kind: Feedback.Kind, about deviceID: String) {
        let targets = viewers.compactMap(\.viewer).filter { $0.deviceID == deviceID }
        (targets.isEmpty ? [currentViewer].compactMap { $0 } : targets).forEach { $0.showToast(message, kind) }
    }

    // MARK: System events

    private func observeSystemEvents() {
        #if os(macOS)
        let center = NSWorkspace.shared.notificationCenter
        let names = [NSWorkspace.didWakeNotification, NSWorkspace.screensDidWakeNotification, NSWorkspace.sessionDidBecomeActiveNotification]
        #else
        // Coming back to the foreground: make sure every session is still running.
        let center = NotificationCenter.default
        let names = [UIApplication.didBecomeActiveNotification]
        #endif
        for name in names {
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
        // `-qaPinControls YES` keeps the hover control bar on screen (combine with record3s etc.).
        if UserDefaults.standard.bool(forKey: "qaPinControls") { viewer.controlsPinned = true }
        // `-qaSoakMinutes 30` logs fps, drops, memory and thermal state once a minute (QASoak).
        let soakMinutes = UserDefaults.standard.integer(forKey: "qaSoakMinutes")
        if soakMinutes > 0, soak == nil {
            soak = QASoak(app: self, minutes: soakMinutes)
            soak?.start()
        }
        guard let action = UserDefaults.standard.string(forKey: "qaAction") else { return }
        Task {
            try? await Task.sleep(for: .seconds(1.5))
            switch action {
            case "screenshot": viewer.saveScreenshot()
            case "copy": viewer.copyFrame()
            #if os(macOS)
            case "actualPixels": viewer.setWindowSize(scale: 1, actualPixels: true)
            case "size50": viewer.setWindowSize(scale: 0.5)
            #endif
            case "fullScreen": viewer.toggleFullScreen()
            case "help": isShowingHelp = true
            #if os(iOS)
            case "settings": isShowingSettings = true
            #endif
            case "record3s":
                viewer.toggleRecording()
                try? await Task.sleep(for: .seconds(3))
                viewer.toggleRecording()
            case "lut":
                // QA (LUT persistence): writes a warm 2³ .cube to Documents and applies it to the card.
                let url = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("QA Warm.cube")
                var cube = "TITLE \"QA Warm\"\nLUT_3D_SIZE 2\n"
                for b in 0...1 { for g in 0...1 { for r in 0...1 {
                    cube += String(format: "%.3f %.3f %.3f\n", min(1, Double(r) * 1.0 + 0.08), Double(g) * 0.9, Double(b) * 0.6)
                } } }
                try? cube.write(to: url, atomically: true, encoding: .utf8)
                viewer.session?.setLUT(url: url)
            case "lutOff": viewer.session?.setLUT(url: nil)
            case "record20s":
                // iPad QA: record across a trip to the background (see task 021).
                viewer.toggleRecording()
                try? await Task.sleep(for: .seconds(20))
                viewer.toggleRecording()
            case "recent15":
                // Keep Recent on for ~25 s, save the last 15 s, then turn it back off.
                prefs.recentEnabled = true
                try? await Task.sleep(for: .seconds(25))
                viewer.session?.saveRecent(seconds: 15)
                try? await Task.sleep(for: .seconds(6))
                prefs.recentEnabled = false
            #if os(macOS)
            case "stream10s":
                // `-qaStreamURL rtmp://127.0.0.1:1935/live/test` (e.g. an `ffmpeg -listen 1` server).
                guard let url = UserDefaults.standard.string(forKey: "qaStreamURL").flatMap(URL.init(string:)) else { break }
                try? await Task.sleep(for: .seconds(2))
                startStreaming(urlOverride: url)
                try? await Task.sleep(for: .seconds(10))
                stopStreaming()
            #endif
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
