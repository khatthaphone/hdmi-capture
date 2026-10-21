#if os(macOS)
import AppKit
#else
import UIKit
#endif
import CaptureCore
import Observation
import SwiftUI

/// A short message at the bottom of a viewer. The kind sets its icon, tint and how long it stays.
struct Feedback: Equatable {
    enum Kind: Equatable {
        /// Something finished (saved, copied).
        case success
        /// Neutral information (profile applied).
        case info
        /// Work in progress; stays until replaced.
        case progress
        /// The action couldn't run; says what to do instead.
        case warning
        /// Something failed.
        case error
    }

    let text: String
    let kind: Kind

    var duration: Duration {
        switch kind {
        case .success, .info: .seconds(2.5)
        case .progress: .seconds(30)
        case .warning, .error: .seconds(5)
        }
    }
}

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
        /// iPadOS paused the camera; the last frame stays behind a clear "Paused" screen.
        case paused(PauseReason)
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
    #if os(macOS)
    @ObservationIgnored weak var window: NSWindow? {
        didSet { if window !== oldValue { observeWindow() } }
    }
    #endif

    private(set) var stats = Stats()
    /// Frames stopped arriving briefly while live; the last frame stays on screen.
    private(set) var signalInterrupted = false
    /// A frame has been shown since the current device was attached.
    private(set) var hasFrame = false
    /// Short confirmation shown at the bottom of the view.
    private(set) var toast: Feedback?
    /// More than a couple of dropped frames per second (often shared USB 2.0 bandwidth).
    private(set) var isDroppingFrames = false
    /// The window is in native full screen.
    private(set) var isFullScreen = false
    /// The hover control bar is showing (cursor near the bottom edge, over the bar, or in one of its menus).
    private(set) var controlsVisible = false
    /// QA: keep the control bar on screen.
    var controlsPinned = false {
        didSet { updateControls() }
    }
    /// The cursor is over the control bar itself.
    var controlsHovered = false {
        didSet { if controlsHovered != oldValue { updateControls() } }
    }

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
    @ObservationIgnored private var pointerNearBottom = false
    @ObservationIgnored private var menuOpen = false
    @ObservationIgnored private var hideControlsTask: Task<Void, Never>?
    /// iPad: the bar was summoned by a tap (hides after a few idle seconds).
    @ObservationIgnored private var tappedOpen = false
    #if os(macOS)
    @ObservationIgnored private var keyObserver: NSObjectProtocol?
    @ObservationIgnored private var fullScreenObservers: [NSObjectProtocol] = []
    @ObservationIgnored private var menuObservers: [NSObjectProtocol] = []
    #endif

    init(app: AppModel, source: Source) {
        self.app = app
        self.source = source
    }

    // MARK: Lifecycle (driven by the view's appear/disappear)

    func activate() {
        app.register(self)
        sync()
        #if os(macOS)
        if menuObservers.isEmpty {
            // A menu opened from the control bar runs its own event loop; keep the bar up until it closes.
            menuObservers = [NSMenu.didBeginTrackingNotification, NSMenu.didEndTrackingNotification].map { name in
                NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                    MainActor.assumeIsolated {
                        self?.menuOpen = name == NSMenu.didBeginTrackingNotification
                        self?.updateControls()
                    }
                }
            }
        }
        #else
        // One viewer per iPad window: it is always the active one.
        app.viewerBecameActive(self)
        #endif
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
        #if os(macOS)
        menuObservers.forEach(NotificationCenter.default.removeObserver)
        menuObservers = []
        #endif
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

    #if DEBUG
    /// QA: `-qaPhase scanning|permissionNeeded|permissionDenied|waiting|connecting|recovering|failed|paused`
    /// shows that state's screen regardless of the card (for screenshots).
    @ObservationIgnored lazy var debugPhase: Phase? = {
        switch UserDefaults.standard.string(forKey: "qaPhase") {
        case "scanning": .scanning
        case "permissionNeeded": .permissionNeeded
        case "permissionDenied": .permissionDenied
        case "waiting": .waiting("UGREEN-25854")
        case "connecting": .connecting("UGREEN-25854")
        case "recovering": .recovering("No signal")
        case "failed": .failed("The capture card stopped responding.")
        case "paused": .paused(.multitasking)
        case "pausedBackground": .paused(.background)
        default: nil
        }
    }()
    #endif

    var phase: Phase {
        #if DEBUG
        if let debugPhase { return debugPhase }
        #endif
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
        case let .paused(reason): return .paused(reason)
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

    #if os(macOS)
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
            if fit < 1 { showToast("Limited to the screen size", .info) }
        }
        let frame = window.frameRect(forContentRect: NSRect(origin: .zero, size: size))
        var origin = NSPoint(x: window.frame.midX - frame.width / 2, y: window.frame.midY - frame.height / 2)
        if let visible = (window.screen ?? NSScreen.main)?.visibleFrame {
            origin.x = min(max(origin.x, visible.minX), visible.maxX - frame.width)
            origin.y = min(max(origin.y, visible.minY), visible.maxY - frame.height)
        }
        window.setFrame(NSRect(origin: origin, size: frame.size), display: true, animate: true)
    }
    #else
    /// iPad windows are sized by the system (full screen, Split View, Stage Manager).
    func toggleFullScreen() {}
    #endif

    /// Saves the current source frame at full resolution to ~/Pictures/HDMI Captain.
    func saveScreenshot() {
        guard let frame = renderer?.currentFrame else {
            showToast("No picture to capture yet", .warning)
            return
        }
        Task.detached(priority: .userInitiated) {
            let folder = MediaLibrary.screenshotFolder
            let message: String, kind: Feedback.Kind
            do {
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                guard let data = FrameSnapshot.pngData(from: frame) else { throw CocoaError(.fileWriteUnknown) }
                let url = FrameSnapshot.fileURL(in: folder)
                try data.write(to: url)
                (message, kind) = ("Screenshot saved\(await MediaLibrary.destinationSuffix(for: url))", .success)
            } catch {
                (message, kind) = ("Couldn't save the screenshot: \(error.localizedDescription)", .error)
            }
            await MainActor.run { self.showToast(message, kind) }
        }
    }

    func copyFrame() {
        guard let frame = renderer?.currentFrame, let image = FrameSnapshot.cgImage(from: frame) else {
            showToast("No picture to copy yet", .warning)
            return
        }
        #if os(macOS)
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects([NSImage(cgImage: image, size: NSSize(width: image.width, height: image.height))])
        #else
        UIPasteboard.general.image = UIImage(cgImage: image)
        #endif
        showToast("Frame copied")
    }

    func toggleRecording() {
        guard testPattern == nil else {
            showToast("Switch to the live input to record", .warning)
            return
        }
        guard let session else {
            showToast("Connect a capture card to record", .warning)
            return
        }
        session.toggleRecording()
    }

    /// Pointer position from the video view: distance from the bottom edge, or nil when it left the view.
    func pointerMoved(fromBottom: CGFloat?, height: CGFloat) {
        let near = fromBottom.map { $0 <= max(110, height * 0.25) } ?? false
        guard near != pointerNearBottom else { return }
        pointerNearBottom = near
        updateControls()
    }

    /// iPad: a tap on the picture shows the bar (or hides it); it hides again after 3 s idle.
    func toggleControls() {
        hideControlsTask?.cancel()
        hideControlsTask = nil
        tappedOpen = !controlsVisible
        if !tappedOpen { controlsVisible = false }
        updateControls()
    }

    /// Shows the bar while something holds it; hides it 2 s after the last reason goes away.
    /// An open menu only keeps an already visible bar up (menu-bar menus must not summon it).
    private func updateControls() {
        let hold = pointerNearBottom || controlsHovered || controlsPinned || (controlsVisible && menuOpen)
        if tappedOpen, !hold {
            // Shown by a tap: visible now, hidden after 3 s unless something else holds it.
            hideControlsTask?.cancel()
            if !controlsVisible { controlsVisible = true }
            hideControlsTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(3))
                guard !Task.isCancelled, let self else { return }
                self.hideControlsTask = nil
                self.tappedOpen = false
                self.updateControls()
            }
            return
        }
        if hold {
            hideControlsTask?.cancel()
            hideControlsTask = nil
            if !controlsVisible { controlsVisible = true }
        } else if controlsVisible, hideControlsTask == nil {
            hideControlsTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(2))
                guard !Task.isCancelled, let self else { return }
                self.hideControlsTask = nil
                self.controlsVisible = false
            }
        }
    }

    func showToast(_ message: String, _ kind: Feedback.Kind = .success) {
        let feedback = Feedback(text: message, kind: kind)
        toast = feedback
        // VoiceOver users don't see toasts; read them out.
        AccessibilityNotification.Announcement(message).post()
        toastTask?.cancel()
        toastTask = Task { [weak self] in
            try? await Task.sleep(for: feedback.duration)
            guard !Task.isCancelled else { return }
            self?.toast = nil
        }
    }

    // MARK: Private

    #if os(macOS)
    private func observeWindow() {
        if let keyObserver { NotificationCenter.default.removeObserver(keyObserver) }
        keyObserver = nil
        fullScreenObservers.forEach(NotificationCenter.default.removeObserver)
        fullScreenObservers = []
        guard let window else { return }
        keyObserver = NotificationCenter.default.addObserver(forName: NSWindow.didBecomeKeyNotification, object: window, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.app.viewerBecameActive(self)
            }
        }
        if window.isKeyWindow { app.viewerBecameActive(self) }
        isFullScreen = window.styleMask.contains(.fullScreen)
        fullScreenObservers = [NSWindow.didEnterFullScreenNotification, NSWindow.didExitFullScreenNotification].map { name in
            NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.isFullScreen = name == NSWindow.didEnterFullScreenNotification }
            }
        }
    }
    #endif

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
