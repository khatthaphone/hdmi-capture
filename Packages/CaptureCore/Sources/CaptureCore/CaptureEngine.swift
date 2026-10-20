import AVFoundation
import os

/// Receives every captured video frame on the capture queue. Must not block.
public protocol FrameSink: AnyObject {
    func consume(_ sampleBuffer: CMSampleBuffer)
}

/// Receives captured audio (float32 interleaved stereo, 48 kHz, capture timestamps) on the audio queue.
public protocol AudioSampleSink: AnyObject {
    func consumeAudio(_ sampleBuffer: CMSampleBuffer)
}

public struct LiveInfo: Equatable, Sendable {
    public var deviceName: String
    public var format: FormatDescriptor
    public var frameRate: Double
    public var audioDeviceName: String?
}

public enum CaptureState: Equatable, Sendable {
    case idle
    case connecting(deviceName: String)
    case live(LiveInfo)
    /// Lost the stream; the engine is retrying on its own.
    case recovering(reason: String)
    case failed(message: String)
}

public struct CaptureStats: Sendable {
    public var framesReceived: UInt64 = 0
    public var framesDropped: UInt64 = 0
    /// `CACurrentMediaTime()` of the newest frame.
    public var lastFrameTime: CFTimeInterval = 0
}

/// Owns the AVCaptureSession. Configuration runs on a private serial queue; frames arrive on a
/// high-priority queue and fan out to sinks without copying. Keeps itself alive across focus
/// changes, Spaces, sleep and transient device errors (spec §5.3).
public final class CaptureEngine: NSObject, @unchecked Sendable {
    public struct Config: Equatable, Sendable {
        public var videoDeviceID: String
        public var audioDeviceID: String?
        public var format: FormatPreference

        public init(videoDeviceID: String, audioDeviceID: String?, format: FormatPreference) {
            self.videoDeviceID = videoDeviceID
            self.audioDeviceID = audioDeviceID
            self.format = format
        }
    }

    public static let log = Logger(subsystem: "hdmicaptain", category: "capture")

    /// Called on the main queue.
    public var stateHandler: ((CaptureState) -> Void)?

    private let session = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "hdmicaptain.session")
    private let videoQueue = DispatchQueue(label: "hdmicaptain.video", qos: .userInteractive)
    private let videoOutput = AVCaptureVideoDataOutput()
    private let audioQueue = DispatchQueue(label: "hdmicaptain.audio", qos: .userInteractive)
    private let audioOutput = AVCaptureAudioDataOutput()

    /// HDMI audio playback (delay, channel mapping, output device, meters).
    public let audio = AudioPipeline()

    // Session-queue state.
    private var desired: Config?
    private var state: CaptureState = .idle
    private var generation = 0
    private var restartAttempt = 0
    private var activity: NSObjectProtocol?
    private var watchdog: DispatchSourceTimer?

    // Shared with the video queue.
    private let lock = NSLock()
    private var sinks: [WeakSink] = []
    private var audioSinks: [WeakAudioSink] = []
    private var stats = CaptureStats()
    /// Set after (re)start; the first frame flips the state to `.live`.
    private var pendingLive: LiveInfo?

    /// Seconds without frames before we consider the stream stalled.
    private let stallTimeout: CFTimeInterval = 3

    public override init() {
        super.init()
        videoOutput.alwaysDiscardsLateVideoFrames = true
        videoOutput.videoSettings = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            kCVPixelBufferMetalCompatibilityKey as String: true,
        ]
        videoOutput.setSampleBufferDelegate(self, queue: videoQueue)
        #if os(macOS)
        audioOutput.audioSettings = AudioPipeline.captureSettings
        #endif
        audioOutput.setSampleBufferDelegate(self, queue: audioQueue)

        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(runtimeError(_:)), name: AVCaptureSession.runtimeErrorNotification, object: session)
        center.addObserver(self, selector: #selector(didStopRunning(_:)), name: AVCaptureSession.didStopRunningNotification, object: session)
        center.addObserver(self, selector: #selector(deviceDisconnected(_:)), name: AVCaptureDevice.wasDisconnectedNotification, object: nil)
    }

    deinit {
        NotificationCenter.default.removeObserver(self)
    }

    // MARK: Public API

    public func addSink(_ sink: FrameSink) {
        lock.withLock { sinks.append(WeakSink(sink)) }
    }

    public func addAudioSink(_ sink: AudioSampleSink) {
        lock.withLock { audioSinks.append(WeakAudioSink(sink)) }
    }

    /// Starts (or reconfigures) capture. No-op if already live with the same config.
    public func start(_ config: Config) {
        sessionQueue.async { [self] in
            if desired == config, session.isRunning, case .live = state { return }
            desired = config
            restartAttempt = 0
            generation += 1
            configureAndRun()
        }
    }

    public func stop() {
        sessionQueue.async { [self] in
            desired = nil
            generation += 1
            teardown()
            emit(.idle)
        }
    }

    /// Restart if the session silently died (e.g. after system sleep).
    public func revalidate() {
        sessionQueue.async { [self] in
            guard desired != nil, !session.isRunning else { return }
            Self.log.info("Revalidate: session not running, restarting")
            scheduleRestart(reason: "Resuming capture")
        }
    }

    public func currentStats() -> CaptureStats {
        lock.withLock { stats }
    }

    public static func formats(forDeviceID id: String) -> [FormatDescriptor] {
        AVCaptureDevice(uniqueID: id)?.formats.map(FormatDescriptor.init) ?? []
    }

    // MARK: Session lifecycle (session queue)

    private func configureAndRun() {
        guard let config = desired else { return }
        guard let device = AVCaptureDevice(uniqueID: config.videoDeviceID) else {
            teardown()
            emit(.recovering(reason: "Waiting for device"))
            return
        }
        emit(.connecting(deviceName: device.localizedName))
        if session.isRunning { session.stopRunning() }

        session.beginConfiguration()
        session.inputs.forEach(session.removeInput)

        do {
            let input = try AVCaptureDeviceInput(device: device)
            guard session.canAddInput(input) else { throw EngineError("Cannot use \(device.localizedName)") }
            session.addInput(input)
        } catch {
            session.commitConfiguration()
            fail(error.localizedDescription)
            return
        }
        if !session.outputs.contains(videoOutput), session.canAddOutput(videoOutput) {
            session.addOutput(videoOutput)
        }

        var audioName: String?
        if let audioID = config.audioDeviceID, let audioDevice = AVCaptureDevice(uniqueID: audioID),
           let input = try? AVCaptureDeviceInput(device: audioDevice), session.canAddInput(input) {
            session.addInput(input)
            audioName = audioDevice.localizedName
            if !session.outputs.contains(audioOutput), session.canAddOutput(audioOutput) {
                session.addOutput(audioOutput)
            }
            let description = audioDevice.activeFormat.formatDescription
            let channels = CMAudioFormatDescriptionGetStreamBasicDescription(description).map { Int($0.pointee.mChannelsPerFrame) } ?? 2
            audio.start(sourceChannels: channels)
        } else {
            if config.audioDeviceID != nil {
                Self.log.error("Audio device unavailable; continuing with video only")
            }
            audio.stop()
        }

        // Holding the device lock through startRunning stops the session from resetting
        // activeFormat back to its preset (macOS behaviour).
        var locked = false
        var applied: (format: AVCaptureDevice.Format, duration: CMTime)?
        do {
            try device.lockForConfiguration()
            locked = true
            applied = apply(config.format, to: device)
        } catch {
            Self.log.error("lockForConfiguration failed: \(error.localizedDescription, privacy: .public)")
        }
        session.commitConfiguration()
        session.startRunning()
        // Some UVC drivers reset format/frame rate when the session starts (seen by Consolation);
        // verify and re-apply while we still hold the lock.
        if locked, let applied, device.activeFormat != applied.format || device.activeVideoMinFrameDuration != applied.duration {
            Self.log.info("Format changed by driver on start; re-applying")
            apply(config.format, to: device)
        }
        if locked { device.unlockForConfiguration() }

        guard session.isRunning else {
            scheduleRestart(reason: "Device did not start")
            return
        }
        beginActivity()
        startWatchdog()
        let format = FormatDescriptor(device.activeFormat)
        let fps = device.activeVideoMinFrameDuration.seconds > 0 ? 1 / device.activeVideoMinFrameDuration.seconds : format.maxFrameRate
        Self.log.info("Running: \(device.localizedName, privacy: .public) \(format.width)x\(format.height) \(format.fourCC, privacy: .public) @ \(fps)")
        let info = LiveInfo(deviceName: device.localizedName, format: format, frameRate: fps, audioDeviceName: audioName)
        lock.withLock {
            stats.lastFrameTime = CACurrentMediaTime()
            pendingLive = info
        }
    }

    /// Returns the chosen format (to verify after start), or nil if nothing was applied.
    @discardableResult
    private func apply(_ preference: FormatPreference, to device: AVCaptureDevice) -> (format: AVCaptureDevice.Format, duration: CMTime)? {
        let descriptors = device.formats.map(FormatDescriptor.init)
        guard let choice = FormatSelector.select(from: descriptors, preference: preference) else { return nil }
        let format = device.formats[choice.index]
        device.activeFormat = format
        let ranges = format.videoSupportedFrameRateRanges
        let duration = ranges.first(where: { abs($0.maxFrameRate - choice.frameRate) < 0.01 })?.minFrameDuration
            ?? CMTime(value: 1000, timescale: CMTimeScale((choice.frameRate * 1000).rounded()))
        // Out-of-range durations raise an Objective-C exception, so check first.
        if ranges.contains(where: { duration >= $0.minFrameDuration && duration <= $0.maxFrameDuration }) {
            device.activeVideoMinFrameDuration = duration
        }
        return (format, duration)
    }

    private func teardown() {
        lock.withLock { pendingLive = nil }
        audio.stop()
        watchdog?.cancel()
        watchdog = nil
        if session.isRunning { session.stopRunning() }
        session.beginConfiguration()
        session.inputs.forEach(session.removeInput)
        session.commitConfiguration()
        endActivity()
    }

    private func scheduleRestart(reason: String) {
        guard desired != nil else { return }
        restartAttempt += 1
        let delay = min(0.25 * pow(2, Double(restartAttempt - 1)), 4)
        let scheduled = generation
        Self.log.info("Restart #\(self.restartAttempt) in \(delay)s: \(reason, privacy: .public)")
        emit(.recovering(reason: reason))
        sessionQueue.asyncAfter(deadline: .now() + delay) { [self] in
            guard generation == scheduled, desired != nil else { return }
            configureAndRun()
        }
    }

    private func fail(_ message: String) {
        Self.log.error("Failed: \(message, privacy: .public)")
        teardown()
        emit(.failed(message: message))
    }

    private func startWatchdog() {
        watchdog?.cancel()
        let timer = DispatchSource.makeTimerSource(queue: sessionQueue)
        timer.schedule(deadline: .now() + 1, repeating: 1)
        timer.setEventHandler { [weak self] in self?.checkStall() }
        timer.resume()
        watchdog = timer
    }

    private func checkStall() {
        guard desired != nil else { return }
        let last = lock.withLock { stats.lastFrameTime }
        let silent = CACurrentMediaTime() - last
        if !session.isRunning {
            scheduleRestart(reason: "Capture stopped")
        } else if silent > stallTimeout, state.isLiveOrConnecting {
            // Many dongles stop sending frames while the HDMI source changes mode; a restart recovers them.
            watchdog?.cancel()
            watchdog = nil
            scheduleRestart(reason: "No signal")
        } else if silent < 1, restartAttempt > 0, case .live = state {
            restartAttempt = 0
        }
    }

    private func beginActivity() {
        guard activity == nil else { return }
        // Keep App Nap and idle display sleep from throttling a live view.
        activity = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiated, .latencyCritical, .idleDisplaySleepDisabled],
            reason: "Live HDMI capture"
        )
    }

    private func endActivity() {
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
        activity = nil
    }

    private func emit(_ newState: CaptureState) {
        guard newState != state else { return }
        state = newState
        DispatchQueue.main.async { [weak self] in self?.stateHandler?(newState) }
    }

    // MARK: Notifications

    @objc private func runtimeError(_ note: Notification) {
        let error = note.userInfo?[AVCaptureSessionErrorKey] as? Error
        sessionQueue.async { [self] in
            scheduleRestart(reason: error?.localizedDescription ?? "Capture error")
        }
    }

    @objc private func didStopRunning(_ note: Notification) {
        sessionQueue.async { [self] in
            guard desired != nil, !session.isRunning else { return }
            scheduleRestart(reason: "Capture stopped")
        }
    }

    @objc private func deviceDisconnected(_ note: Notification) {
        guard let device = note.object as? AVCaptureDevice else { return }
        let id = device.uniqueID
        sessionQueue.async { [self] in
            guard desired?.videoDeviceID == id else { return }
            Self.log.info("Device disconnected: \(device.localizedName, privacy: .public)")
            generation += 1
            teardown()
            emit(.recovering(reason: "Device disconnected"))
        }
    }
}

extension CaptureEngine: AVCaptureVideoDataOutputSampleBufferDelegate, AVCaptureAudioDataOutputSampleBufferDelegate {
    public func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        if output === audioOutput {
            audio.enqueue(sampleBuffer)
            for sink in lock.withLock({ audioSinks.compactMap(\.sink) }) {
                sink.consumeAudio(sampleBuffer)
            }
            return
        }
        let (targets, becameLive): ([FrameSink], LiveInfo?) = lock.withLock {
            stats.framesReceived += 1
            stats.lastFrameTime = CACurrentMediaTime()
            defer { pendingLive = nil }
            return (sinks.compactMap(\.sink), pendingLive)
        }
        if let becameLive {
            sessionQueue.async { [self] in
                Self.log.info("First frame received")
                emit(.live(becameLive))
            }
        }
        for sink in targets {
            sink.consume(sampleBuffer)
        }
    }

    public func captureOutput(_ output: AVCaptureOutput, didDrop sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        lock.withLock { stats.framesDropped += 1 }
    }
}

private extension CaptureState {
    var isLiveOrConnecting: Bool {
        switch self {
        case .live, .connecting: true
        default: false
        }
    }
}

private struct WeakAudioSink {
    weak var sink: AudioSampleSink?
    init(_ sink: AudioSampleSink) { self.sink = sink }
}

private struct WeakSink {
    weak var sink: FrameSink?
    init(_ sink: FrameSink) { self.sink = sink }
}

private struct EngineError: LocalizedError {
    var errorDescription: String?
    init(_ message: String) { errorDescription = message }
}
