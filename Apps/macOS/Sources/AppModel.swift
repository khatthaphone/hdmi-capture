import AppKit
import AVFoundation
import CaptureCore
import Observation

/// Window/app-level preferences (per-device settings live in `DevicePreset`).
struct ViewerPrefs: Codable, Equatable {
    var floatOnTop = false
    var allSpaces = false
    var cleanMode = false
    var lockAspect = true
    var showStats = false
    var includeNonExternalCameras = false

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
    }
}

/// Coordinates device discovery, the capture engine, the renderer and persisted settings.
@MainActor
@Observable
final class AppModel {
    enum Phase: Equatable {
        case permissionNeeded
        case permissionDenied
        case scanning
        case connecting(String)
        case live
        case recovering(String)
        case failed(String)
    }

    struct Stats: Equatable {
        var inputFPS = 0.0
        var renderFPS = 0.0
        var dropped: UInt64 = 0
        var frameSize: CGSize?
    }

    private(set) var phase: Phase = .scanning
    private(set) var liveInfo: LiveInfo?
    private(set) var selectedVideoID: String?
    private(set) var activeAudioID: String?
    private(set) var formats: [FormatDescriptor] = []
    private(set) var stats = Stats()
    /// What the card is delivering (picture vs. no-signal/HDCP black/vendor screen).
    private(set) var signal: SignalContent = .picture
    /// Frames stopped arriving briefly while live; the last frame stays on screen.
    private(set) var signalInterrupted = false
    /// A frame has been shown since the current device was selected.
    private(set) var hasFrame = false

    var preset = DevicePreset() {
        didSet { presetChanged(from: oldValue) }
    }

    var prefs: ViewerPrefs {
        didSet {
            savePrefs()
            catalog.includeNonExternalCameras = prefs.includeNonExternalCameras
        }
    }

    let catalog: DeviceCatalog
    @ObservationIgnored let renderer = MetalRenderer()
    @ObservationIgnored weak var window: NSWindow?
    @ObservationIgnored private let engine = CaptureEngine()
    @ObservationIgnored private let signalMonitor = SignalMonitor()
    @ObservationIgnored private var lastStatsSample: CFTimeInterval = 0
    @ObservationIgnored private let store = PresetStore()
    @ObservationIgnored private var authorized = false
    @ObservationIgnored private var manualVideoID: String?
    @ObservationIgnored private var runningConfig: CaptureEngine.Config?
    @ObservationIgnored private var loadingPreset = false
    @ObservationIgnored private var lastSample: (stats: CaptureStats, presented: UInt64, time: CFTimeInterval)?
    @ObservationIgnored private var timers: [Timer] = []
    @ObservationIgnored private var observers: [NSObjectProtocol] = []

    var aspect: CGSize? {
        liveInfo.map { CGSize(width: $0.format.width, height: $0.format.height) }
    }

    var formatOptions: [FormatPreference] {
        FormatSelector.options(from: formats)
    }

    /// Polled by meters; reading doesn't trigger SwiftUI observation.
    func audioLevels() -> AudioLevels {
        engine.audio.currentLevels()
    }

    func outputDevices() -> [AudioOutputDevice] {
        AudioDevices.outputDevices()
    }

    init() {
        prefs = Self.loadPrefs()
        catalog = DeviceCatalog(provider: AVDeviceProvider(), includeNonExternalCameras: Self.loadPrefs().includeNonExternalCameras)
        if let renderer { engine.addSink(renderer) }
        engine.addSink(signalMonitor)
        signalMonitor.onChange = { [weak self] content in
            MainActor.assumeIsolated { self?.signal = content }
        }
        engine.stateHandler = { [weak self] state in
            MainActor.assumeIsolated { self?.apply(state) }
        }
        catalog.onChange = { [weak self] in self?.reconcile() }
        observeSystemEvents()
        startStatsTimer()
        if renderer == nil { phase = .failed("Metal is not available on this Mac.") }
        Task { await requestPermissions() }
    }

    // MARK: Actions

    func selectVideo(_ id: String) {
        manualVideoID = id
        store.lastVideoDeviceID = id
        reconcile()
    }

    func retry() {
        runningConfig = nil
        Task { await requestPermissions() }
    }

    func toggleFullScreen() {
        (window ?? NSApp.keyWindow)?.toggleFullScreen(nil)
    }

    func openPrivacySettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Camera") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: Permissions

    private func requestPermissions() async {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            break
        case .notDetermined:
            phase = .permissionNeeded
            guard await AVCaptureDevice.requestAccess(for: .video) else {
                phase = .permissionDenied
                return
            }
        default:
            phase = .permissionDenied
            return
        }
        if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            _ = await AVCaptureDevice.requestAccess(for: .audio)
        }
        authorized = true
        if phase == .permissionNeeded || phase == .permissionDenied { phase = .scanning }
        catalog.refresh()
    }

    // MARK: Device policy

    /// Brings the engine in line with the connected devices and current preset. Called on every hot-plug.
    private func reconcile() {
        guard authorized, renderer != nil else { return }
        let pick: String?
        if let manualVideoID, catalog.video(manualVideoID) != nil {
            pick = manualVideoID
        } else {
            pick = DeviceSelection.pickVideo(
                available: catalog.videoDevices,
                connectionOrder: catalog.connectionOrder,
                remembered: store.lastVideoDeviceID,
                current: selectedVideoID
            )
        }
        guard let pick, let device = catalog.video(pick) else {
            selectedVideoID = nil
            runningConfig = nil
            liveInfo = nil
            formats = []
            engine.stop()
            renderer?.clear()
            signalMonitor.reset()
            hasFrame = false
            phase = .scanning
            return
        }
        if pick != selectedVideoID {
            selectedVideoID = pick
            renderer?.clear()
            signalMonitor.reset()
            hasFrame = false
            store.lastVideoDeviceID = pick
            formats = CaptureEngine.formats(forDeviceID: pick)
            loadingPreset = true
            preset = store.preset(for: pick)
            loadingPreset = false
        }
        activeAudioID = resolveAudio(for: device)
        applyOutputSettings()
        let config = CaptureEngine.Config(videoDeviceID: pick, audioDeviceID: activeAudioID, format: preset.format)
        if config != runningConfig {
            runningConfig = config
            engine.start(config)
        }
    }

    private func resolveAudio(for video: CaptureDeviceInfo) -> String? {
        switch preset.audio {
        case .none:
            return nil
        case let .device(id) where catalog.audio(id) != nil:
            return id
        case .device, .automatic:
            return DeviceSelection.pairAudio(for: video, among: catalog.audioDevices)
        }
    }

    private func presetChanged(from old: DevicePreset) {
        guard !loadingPreset, let selectedVideoID else { return }
        store.save(preset, for: selectedVideoID)
        if preset.format != old.format || preset.audio != old.audio {
            reconcile()
        } else {
            applyOutputSettings()
        }
    }

    private func applyOutputSettings() {
        renderer?.setOptions(RenderOptions(scale: preset.scale, filter: preset.filter, colorRange: preset.colorRange))
        engine.audio.update(AudioPipeline.Settings(
            delayMs: preset.audioDelayMs,
            channelMode: preset.channelMode,
            volume: preset.volume,
            muted: preset.muted,
            outputDeviceUID: preset.outputDeviceUID
        ))
    }

    private func apply(_ state: CaptureState) {
        switch state {
        case .idle:
            if selectedVideoID == nil, authorized { phase = .scanning }
        case let .connecting(name):
            phase = .connecting(name)
        case let .live(info):
            liveInfo = info
            phase = .live
        case let .recovering(reason):
            phase = .recovering(reason)
        case let .failed(message):
            phase = .failed(message)
        }
    }

    // MARK: System events

    private func observeSystemEvents() {
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.screensDidWakeNotification, NSWorkspace.sessionDidBecomeActiveNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.engine.revalidate()
                    self?.renderer?.redraw()
                }
            })
        }
    }

    private func startStatsTimer() {
        let timer = Timer(timeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.checkFrameFlow()
                self?.sampleStats()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        timers.append(timer)
    }

    /// Detects short dropouts (before the engine's 3 s stall restart) so the UI can show a banner.
    private func checkFrameFlow() {
        let gap = CACurrentMediaTime() - engine.currentStats().lastFrameTime
        let interrupted = phase == .live && gap > 0.5
        if interrupted != signalInterrupted { signalInterrupted = interrupted }
        let frame = renderer?.lastFrameSize != nil
        if frame != hasFrame { hasFrame = frame }
    }

    private func sampleStats() {
        let now = CACurrentMediaTime()
        guard now - lastStatsSample >= 1 else { return }
        lastStatsSample = now
        let current = engine.currentStats()
        let presented = renderer?.presentedFrames ?? 0
        defer { lastSample = (current, presented, now) }
        guard prefs.showStats, let last = lastSample, now > last.time else { return }
        let dt = now - last.time
        stats = Stats(
            inputFPS: Double(current.framesReceived &- last.stats.framesReceived) / dt,
            renderFPS: Double(presented &- last.presented) / dt,
            dropped: current.framesDropped,
            frameSize: renderer?.lastFrameSize
        )
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
