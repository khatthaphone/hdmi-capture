import AppKit
import CaptureCore
import Observation

/// One place video is shown: the main window, a per-card window, or a grid tile.
/// Owns its renderer and view state; the capture itself lives in the shared `DeviceSession`.
@MainActor
@Observable
final class ViewerModel {
    enum Source: Hashable {
        /// Main window: follows the automatic/manual device choice.
        case automatic
        /// Always shows this card (per-card window or grid tile).
        case pinned(String)
    }

    enum Phase: Equatable {
        case permissionNeeded
        case permissionDenied
        case scanning
        /// A pinned card that isn't connected right now.
        case waiting(String)
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
        var captureLatency: LatencyTracker.Summary?
        var displayLatency: LatencyTracker.Summary?
    }

    let source: Source
    @ObservationIgnored unowned let app: AppModel
    @ObservationIgnored let renderer = MetalRenderer()
    @ObservationIgnored weak var window: NSWindow? {
        didSet { if window !== oldValue { observeWindow() } }
    }

    private(set) var stats = Stats()
    /// Frames stopped arriving briefly while live; the last frame stays on screen.
    private(set) var signalInterrupted = false
    /// A frame has been shown since the current device was attached.
    private(set) var hasFrame = false
    /// Short confirmation shown at the bottom of the view.
    private(set) var toast: String?
    /// More than a couple of dropped frames per second (often shared USB 2.0 bandwidth).
    private(set) var isDroppingFrames = false

    /// A generated test pattern shown instead of the live picture.
    var testPattern: TestPattern? {
        didSet {
            guard testPattern != oldValue else { return }
            renderer?.showStill(testPattern.flatMap(AppModel.patternBuffer))
        }
    }

    @ObservationIgnored private var attachedID: String?
    @ObservationIgnored private var toastTask: Task<Void, Never>?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var lastStatsSample: CFTimeInterval = 0
    @ObservationIgnored private var lastSample: (stats: CaptureStats, presented: UInt64, time: CFTimeInterval)?
    @ObservationIgnored private var keyObserver: NSObjectProtocol?

    init(app: AppModel, source: Source) {
        self.app = app
        self.source = source
    }

    // MARK: Lifecycle (driven by the view's appear/disappear)

    func activate() {
        app.register(self)
        sync()
        guard timer == nil else { return }
        let timer = Timer(timeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.checkFrameFlow()
                self?.sampleStats()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    func deactivate() {
        timer?.invalidate()
        timer = nil
        if let renderer, let attachedID { app.sessions[attachedID]?.detach(renderer) }
        attachedID = nil
        app.unregister(self)
    }

    /// Attaches the renderer to the right session after the app re-resolves devices.
    func sync() {
        guard let renderer else { return }
        let id = session?.deviceID
        guard id != attachedID else { return }
        if let attachedID { app.sessions[attachedID]?.detach(renderer) }
        renderer.clear()
        hasFrame = false
        attachedID = id
        session?.attach(renderer)
    }

    // MARK: State

    var deviceID: String? {
        switch source {
        case .automatic: app.mainDeviceID
        case let .pinned(id): id
        }
    }

    var session: DeviceSession? {
        deviceID.flatMap { app.sessions[$0] }
    }

    var phase: Phase {
        if renderer == nil { return .failed("Metal is not available on this Mac.") }
        switch app.permission {
        case .needed: return .permissionNeeded
        case .denied: return .permissionDenied
        case .unknown: return .scanning
        case .granted: break
        }
        guard let session else {
            if case let .pinned(id) = source { return .waiting(app.knownName(for: id)) }
            return .scanning
        }
        switch session.state {
        case .idle: return .connecting(session.deviceName)
        case let .connecting(name): return .connecting(name)
        case .live: return .live
        case let .recovering(reason): return .recovering(reason)
        case let .failed(message): return .failed(message)
        case .absent: return .waiting(session.deviceName)
        }
    }

    var liveInfo: LiveInfo? { session?.liveInfo }
    var signal: SignalContent { session?.signal ?? .picture }

    /// The card's settings; editing them without a card is a no-op.
    var preset: DevicePreset {
        get { session?.preset ?? DevicePreset() }
        set { session?.preset = newValue }
    }

    var hasAudioFocus: Bool { session?.audioFocused ?? false }

    /// Pixel size of what is on screen (test pattern or live source).
    var sourceSize: CGSize? {
        if testPattern != nil { return CGSize(width: 1920, height: 1080) }
        return liveInfo.map { CGSize(width: $0.format.width, height: $0.format.height) }
    }

    var aspect: CGSize? { sourceSize }

    var formatOptions: [FormatPreference] {
        FormatSelector.options(from: session?.formats ?? [])
    }

    func audioLevels() -> AudioLevels {
        session?.audioLevels() ?? AudioLevels()
    }

    // MARK: Actions

    func toggleFullScreen() {
        (window ?? NSApp.keyWindow)?.toggleFullScreen(nil)
    }

    /// Sizes the window relative to the source: `scale` × source pixels in points, or 1:1 device
    /// pixels when `actualPixels` is set. Clamped to the screen, keeping the window's center.
    func setWindowSize(scale: Double, actualPixels: Bool = false) {
        guard let window = window ?? NSApp.keyWindow, let source = sourceSize, !window.styleMask.contains(.fullScreen) else { return }
        let pointsPerPixel = actualPixels ? 1 / window.backingScaleFactor : scale
        var size = NSSize(width: (source.width * pointsPerPixel).rounded(), height: (source.height * pointsPerPixel).rounded())
        if let visible = (window.screen ?? NSScreen.main)?.visibleFrame {
            let fit = min(1, visible.width / size.width, visible.height / size.height)
            size = NSSize(width: (size.width * fit).rounded(), height: (size.height * fit).rounded())
            if fit < 1 { showToast("Limited to screen size") }
        }
        let frame = window.frameRect(forContentRect: NSRect(origin: .zero, size: size))
        var origin = NSPoint(x: window.frame.midX - frame.width / 2, y: window.frame.midY - frame.height / 2)
        if let visible = (window.screen ?? NSScreen.main)?.visibleFrame {
            origin.x = min(max(origin.x, visible.minX), visible.maxX - frame.width)
            origin.y = min(max(origin.y, visible.minY), visible.maxY - frame.height)
        }
        window.setFrame(NSRect(origin: origin, size: frame.size), display: true, animate: true)
    }

    /// Saves the current source frame at full resolution to ~/Pictures/HDMI Captain.
    func saveScreenshot() {
        guard let frame = renderer?.currentFrame else {
            showToast("Nothing to capture yet")
            return
        }
        Task.detached(priority: .userInitiated) {
            let folder = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("HDMI Captain", isDirectory: true)
            let message: String
            do {
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                guard let data = FrameSnapshot.pngData(from: frame) else { throw CocoaError(.fileWriteUnknown) }
                let url = FrameSnapshot.fileURL(in: folder)
                try data.write(to: url)
                message = "Saved \(url.lastPathComponent)"
            } catch {
                message = "Couldn't save screenshot: \(error.localizedDescription)"
            }
            await MainActor.run { self.showToast(message) }
        }
    }

    func copyFrame() {
        guard let frame = renderer?.currentFrame, let image = FrameSnapshot.cgImage(from: frame) else {
            showToast("Nothing to copy yet")
            return
        }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects([NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))])
        showToast("Frame copied")
    }

    func toggleRecording() {
        guard testPattern == nil else {
            showToast("Turn off the test pattern to record")
            return
        }
        guard let session else {
            showToast("Nothing live to record")
            return
        }
        session.toggleRecording()
    }

    func showToast(_ message: String) {
        toast = message
        toastTask?.cancel()
        toastTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2.5))
            guard !Task.isCancelled else { return }
            self?.toast = nil
        }
    }

    // MARK: Private

    private func observeWindow() {
        if let keyObserver { NotificationCenter.default.removeObserver(keyObserver) }
        keyObserver = nil
        guard let window else { return }
        keyObserver = NotificationCenter.default.addObserver(forName: NSWindow.didBecomeKeyNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.app.viewerBecameActive(self)
            }
        }
        if window.isKeyWindow { app.viewerBecameActive(self) }
    }

    /// Detects short dropouts (before the engine's 3 s stall restart) so the UI can show a banner.
    private func checkFrameFlow() {
        if let session {
            let gap = CACurrentMediaTime() - session.currentStats().lastFrameTime
            let interrupted = phase == .live && gap > 0.5
            if interrupted != signalInterrupted { signalInterrupted = interrupted }
        } else if signalInterrupted {
            signalInterrupted = false
        }
        let frame = renderer?.lastFrameSize != nil
        if frame != hasFrame { hasFrame = frame }
    }

    private func sampleStats() {
        let now = CACurrentMediaTime()
        guard now - lastStatsSample >= 1, let session else { return }
        lastStatsSample = now
        let current = session.currentStats()
        let presented = renderer?.presentedFrames ?? 0
        defer { lastSample = (current, presented, now) }
        guard let last = lastSample, now > last.time else { return }
        let dt = now - last.time
        let dropping = Double(current.framesDropped &- last.stats.framesDropped) / dt > 2
        if dropping != isDroppingFrames { isDroppingFrames = dropping }
        guard app.prefs.showStats else { return }
        stats = Stats(
            inputFPS: Double(current.framesReceived &- last.stats.framesReceived) / dt,
            renderFPS: Double(presented &- last.presented) / dt,
            dropped: current.framesDropped,
            frameSize: renderer?.lastFrameSize,
            captureLatency: renderer?.latency().capture,
            displayLatency: renderer?.latency().display
        )
    }
}
