import AVFoundation
import CaptureCore
import Observation

/// Everything tied to one capture card: the engine, audio, signal monitor, recorder and its saved
/// preset. Shared by every window or grid tile that shows the card; each view attaches its own renderer.
@MainActor
@Observable
final class DeviceSession {
    enum State: Equatable {
        case idle
        case connecting(String)
        case live
        case recovering(String)
        case failed(String)
        /// The card is not connected; capture resumes when it comes back.
        case absent
    }

    let deviceID: String
    private(set) var deviceName: String
    private(set) var state: State = .idle
    private(set) var liveInfo: LiveInfo?
    private(set) var formats: [FormatDescriptor]
    private(set) var activeAudioID: String?
    private(set) var signal: SignalContent = .picture
    /// When the current recording started; nil when not recording.
    private(set) var recordingSince: Date?
    private(set) var lastRecordingURL: URL?

    var preset: DevicePreset {
        didSet { presetChanged(from: oldValue) }
    }

    /// Only the session with audio focus is audible.
    var audioFocused = true {
        didSet { if oldValue != audioFocused { applyAudio() } }
    }

    /// Asks the app to re-resolve devices (format or audio input changed).
    @ObservationIgnored var onNeedsReconcile: (() -> Void)?
    /// Short user-facing messages for whichever views show this card.
    @ObservationIgnored var onMessage: ((String) -> Void)?
    /// Recording codec, read when a recording starts.
    @ObservationIgnored var recordingCodec: () -> Recorder.Codec = { .hevc }

    @ObservationIgnored let engine = CaptureEngine()
    @ObservationIgnored let signalMonitor = SignalMonitor()
    @ObservationIgnored let recorder = Recorder()
    @ObservationIgnored private let store: PresetStore
    @ObservationIgnored private var runningConfig: CaptureEngine.Config?
    @ObservationIgnored private var recordingSize: (width: Int, height: Int)?
    @ObservationIgnored private var renderers: [WeakRenderer] = []
    @ObservationIgnored private var lut: CubeLUT?
    /// Why the saved LUT couldn't be loaded, if it couldn't.
    private(set) var lutError: String?

    init(device: CaptureDeviceInfo, store: PresetStore) {
        deviceID = device.id
        deviceName = device.name
        self.store = store
        preset = store.preset(for: device.id)
        formats = CaptureEngine.formats(forDeviceID: device.id)
        loadLUT()
        engine.addSink(signalMonitor)
        engine.addSink(recorder)
        engine.addAudioSink(recorder)
        engine.stateHandler = { [weak self] state in
            MainActor.assumeIsolated { self?.apply(state) }
        }
        signalMonitor.onChange = { [weak self] content in
            MainActor.assumeIsolated { self?.signal = content }
        }
        recorder.onStateChange = { [weak self] state in
            MainActor.assumeIsolated { self?.recorderChanged(state) }
        }
        applyAudio()
    }

    // MARK: Renderers

    func attach(_ renderer: MetalRenderer) {
        renderers.removeAll { $0.renderer == nil || $0.renderer === renderer }
        renderers.append(WeakRenderer(renderer))
        renderer.setOptions(renderOptions)
        renderer.setLUT(lut)
        engine.addSink(renderer)
    }

    func detach(_ renderer: MetalRenderer) {
        engine.removeSink(renderer)
        renderers.removeAll { $0.renderer == nil || $0.renderer === renderer }
    }

    var renderOptions: RenderOptions {
        RenderOptions(scale: preset.scale, filter: preset.filter, colorRange: preset.colorRange,
                      adjustments: preset.adjustments, lutIntensity: lut == nil ? 0 : preset.lutIntensity)
    }

    // MARK: Lifecycle

    /// Starts or reconfigures capture for the card as it currently appears in `catalog`.
    func update(device: CaptureDeviceInfo?, catalog: DeviceCatalog) {
        guard let device else {
            if state != .absent {
                stopRecording(reason: "Recording saved: the device was disconnected")
                engine.stop()
                signalMonitor.reset()
                runningConfig = nil
                state = .absent
            }
            return
        }
        deviceName = device.name
        if formats.isEmpty { formats = CaptureEngine.formats(forDeviceID: device.id) }
        activeAudioID = resolveAudio(for: device, catalog: catalog)
        applyAudio()
        let config = CaptureEngine.Config(videoDeviceID: device.id, audioDeviceID: activeAudioID, format: preset.format)
        if config != runningConfig {
            runningConfig = config
            engine.start(config)
        }
    }

    /// Next `update` restarts the engine even if nothing changed.
    func forceRestart() {
        runningConfig = nil
    }

    func shutdown() {
        stopRecording()
        engine.stop()
        runningConfig = nil
        state = .idle
    }

    func revalidate() {
        engine.revalidate()
        renderers.compactMap(\.renderer).forEach { $0.redraw() }
    }

    func audioLevels() -> AudioLevels {
        engine.audio.currentLevels()
    }

    func currentStats() -> CaptureStats {
        engine.currentStats()
    }

    // MARK: Recording (Pro — callers go through EntitlementStore.gate)

    var isRecording: Bool { recordingSince != nil }

    func toggleRecording() {
        isRecording ? stopRecording() : startRecording()
    }

    func startRecording() {
        guard state == .live, let info = liveInfo else {
            onMessage?("Nothing live to record")
            return
        }
        let folder = FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("HDMI Captain", isDirectory: true)
        recordingSize = (info.format.width, info.format.height)
        recorder.start(
            url: Recorder.fileURL(in: folder),
            settings: Recorder.Settings(codec: recordingCodec()),
            frameRate: info.frameRate,
            includeAudio: activeAudioID != nil
        )
    }

    func stopRecording(reason: String? = nil) {
        guard isRecording else { return }
        recordingSize = nil
        if let reason { onMessage?(reason) }
        recorder.stop()
    }

    // MARK: Private

    private func resolveAudio(for video: CaptureDeviceInfo, catalog: DeviceCatalog) -> String? {
        switch preset.audio {
        case .none:
            return nil
        case let .device(id) where catalog.audio(id) != nil:
            return id
        case .device, .automatic:
            return DeviceSelection.pairAudio(for: video, among: catalog.audioDevices)
        }
    }

    // MARK: Profiles

    func applyProfile(_ profile: Profile) {
        var updated = preset
        profile.settings.apply(to: &updated)
        updated.profileID = profile.id
        preset = updated
        onMessage?("Profile: \(profile.name)")
    }

    // MARK: LUT

    /// Stores a user-chosen .cube file for this card (sandbox: security-scoped bookmark).
    func setLUT(url: URL?) {
        guard let url else {
            preset.lutBookmark = nil
            preset.lutName = nil
            return
        }
        do {
            _ = try CubeLUT(contentsOf: url) // validate before saving
            preset.lutBookmark = try url.bookmarkData(options: [.withSecurityScope, .securityScopeAllowOnlyReadAccess])
            preset.lutName = url.deletingPathExtension().lastPathComponent
        } catch {
            lutError = error.localizedDescription
            onMessage?("Can't use this LUT: \(error.localizedDescription)")
        }
    }

    private func loadLUT() {
        lut = nil
        lutError = nil
        guard let bookmark = preset.lutBookmark else { return }
        do {
            var stale = false
            let url = try URL(resolvingBookmarkData: bookmark, options: .withSecurityScope, bookmarkDataIsStale: &stale)
            let accessing = url.startAccessingSecurityScopedResource()
            defer { if accessing { url.stopAccessingSecurityScopedResource() } }
            lut = try CubeLUT(contentsOf: url)
        } catch {
            lutError = error.localizedDescription
        }
    }

    private func presetChanged(from old: DevicePreset) {
        store.save(preset, for: deviceID)
        if preset.lutBookmark != old.lutBookmark {
            loadLUT()
            renderers.compactMap(\.renderer).forEach { $0.setLUT(lut) }
        }
        if preset.format != old.format || preset.audio != old.audio {
            onNeedsReconcile?()
        }
        let options = renderOptions
        renderers.compactMap(\.renderer).forEach { $0.setOptions(options) }
        applyAudio()
    }

    private func applyAudio() {
        engine.audio.update(AudioPipeline.Settings(
            delayMs: preset.audioDelayMs,
            channelMode: preset.channelMode,
            volume: preset.volume,
            muted: preset.muted || !audioFocused,
            outputDeviceUID: preset.outputDeviceUID
        ))
    }

    private func apply(_ engineState: CaptureState) {
        switch engineState {
        case .idle:
            stopRecording()
            if state != .absent { state = .idle }
        case let .connecting(name):
            state = .connecting(name)
        case let .live(info):
            // A new mode mid-recording would corrupt the track; close the file instead.
            if let size = recordingSize, size != (info.format.width, info.format.height) {
                stopRecording(reason: "Recording stopped: the video mode changed")
            }
            liveInfo = info
            state = .live
        case let .recovering(reason):
            if reason == "Device disconnected" { stopRecording(reason: "Recording saved: the device was disconnected") }
            state = .recovering(reason)
        case let .failed(message):
            stopRecording()
            state = .failed(message)
        }
    }

    private func recorderChanged(_ recorderState: Recorder.State) {
        switch recorderState {
        case let .recording(_, startedAt):
            recordingSince = startedAt
        case let .finished(url):
            recordingSince = nil
            lastRecordingURL = url
            onMessage?("Saved \(url.lastPathComponent)")
        case .idle:
            recordingSince = nil
        case let .failed(message):
            recordingSince = nil
            onMessage?("Recording failed: \(message)")
        }
    }
}

private struct WeakRenderer {
    weak var renderer: MetalRenderer?
    init(_ renderer: MetalRenderer) { self.renderer = renderer }
}
