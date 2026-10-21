import CaptureCore
import CoreMedia
import HaishinKit
import Observation
import os
import RTMPHaishinKit
// SRT (SRTHaishinKit) is left out: its libsrt has no x86_64 slice, which breaks the universal
// Release build (spec §5.7). Streaming is hidden for now (046); add the package back with an
// arm64-only or rebuilt libsrt (and register SRTSessionFactory) when SRT ships.
import VideoToolbox

/// Live streaming of one capture card (Pro). Frames and audio are handed to HaishinKit in order
/// through a single async stream; HaishinKit encodes H.264/AAC and publishes over RTMP or SRT.
@MainActor
@Observable
final class Streamer {
    enum State: Equatable {
        case idle
        case connecting
        case live(since: Date)
        case reconnecting(attempt: Int)
        case failed(String)

        var isActive: Bool {
            switch self {
            case .idle, .failed: false
            default: true
            }
        }
    }

    static let log = Logger(subsystem: "hdmicaptain", category: "stream")

    private(set) var state: State = .idle
    /// Device being streamed.
    private(set) var deviceID: String?

    @ObservationIgnored private var session: (any Session)?
    @ObservationIgnored private var mixer: MediaMixer?
    @ObservationIgnored private var feeder: Task<Void, Never>?
    @ObservationIgnored private let tap = SampleTap()
    @ObservationIgnored private weak var engine: CaptureEngine?
    @ObservationIgnored private var url: URL?
    @ObservationIgnored private var stopping = false
    @ObservationIgnored private static var factoriesRegistered = false

    /// Starts publishing `session`'s capture to `url`. `sourceSize` is the card's current mode.
    func start(device: DeviceSession, url: URL, settings: StreamSettings, sourceSize: CGSize, frameRate: Double) async {
        guard !state.isActive else { return }
        stopping = false
        deviceID = device.deviceID
        engine = device.engine
        self.url = url
        state = .connecting

        if !Self.factoriesRegistered {
            await SessionBuilderFactory.shared.register(RTMPSessionFactory())
            Self.factoriesRegistered = true
        }

        do {
            guard let session = try await SessionBuilderFactory.shared.make(url).setMode(.publish).build() else {
                throw StreamError("Unsupported stream address")
            }
            let mixer = MediaMixer(captureSessionMode: .manual)
            var mixerSettings = await mixer.videoMixerSettings
            mixerSettings.mode = .passthrough
            await mixer.setVideoMixerSettings(mixerSettings)

            let stream = await session.stream
            let size = settings.resolution.size(for: sourceSize)
            try await stream.setVideoSettings(VideoCodecSettings(
                videoSize: size,
                bitRate: settings.videoBitrate * 1000,
                profileLevel: kVTProfileLevel_H264_High_AutoLevel as String,
                maxKeyFrameIntervalDuration: 2,
                allowFrameReordering: false,
                expectedFrameRate: frameRate
            ))
            try await stream.setAudioSettings(AudioCodecSettings(bitRate: 160_000))
            await mixer.addOutput(stream)
            await mixer.startRunning()

            self.session = session
            self.mixer = mixer
            startFeeding(mixer)
            device.engine.addSink(tap)
            device.engine.addAudioSink(tap)
            Self.log.info("Connecting to \(url.scheme ?? "?", privacy: .public)://\(url.host ?? "?", privacy: .public) (key hidden) at \(Int(size.width))x\(Int(size.height)) \(settings.videoBitrate) kbps")
            try await connect(session)
        } catch {
            Self.log.error("Stream failed to start: \(error.localizedDescription, privacy: .public)")
            await teardown()
            state = .failed(error.localizedDescription)
        }
    }

    func stop() async {
        stopping = true
        await teardown()
        state = .idle
        Self.log.info("Stream stopped")
    }

    // MARK: Private

    private func connect(_ session: any Session, attempt: Int = 0) async throws {
        await session.setMaxRetryCount(3)
        try await session.connect { [weak self] in
            Task { @MainActor in await self?.handleDisconnect() }
        }
        state = .live(since: Date())
    }

    /// The network dropped: retry with backoff (1 s → 10 s), up to ~2 minutes.
    private func handleDisconnect() async {
        guard !stopping, let session else { return }
        for attempt in 1...12 {
            guard !stopping else { return }
            state = .reconnecting(attempt: attempt)
            Self.log.info("Stream disconnected; reconnect attempt \(attempt)")
            try? await Task.sleep(for: .seconds(min(10, attempt)))
            do {
                try await connect(session, attempt: attempt)
                return
            } catch {
                Self.log.error("Reconnect failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        await teardown()
        state = .failed("Connection lost")
    }

    private func startFeeding(_ mixer: MediaMixer) {
        let samples = tap.makeStream()
        feeder = Task.detached(priority: .userInitiated) {
            for await sample in samples {
                await mixer.append(sample.buffer, track: 0)
            }
        }
    }

    private func teardown() async {
        if let engine {
            engine.removeSink(tap)
            engine.removeAudioSink(tap)
        }
        tap.finish()
        feeder?.cancel()
        feeder = nil
        try? await session?.close()
        await mixer?.stopRunning()
        session = nil
        mixer = nil
        engine = nil
        deviceID = nil
    }
}

/// Collects capture-queue samples into an ordered async stream for the HaishinKit mixer.
private final class SampleTap: FrameSink, AudioSampleSink, @unchecked Sendable {
    struct Sample: @unchecked Sendable {
        let buffer: CMSampleBuffer
    }

    private let lock = NSLock()
    private var continuation: AsyncStream<Sample>.Continuation?

    func makeStream() -> AsyncStream<Sample> {
        // Drop the oldest under backpressure rather than growing latency.
        let (stream, continuation) = AsyncStream.makeStream(of: Sample.self, bufferingPolicy: .bufferingNewest(180))
        lock.withLock { self.continuation = continuation }
        return stream
    }

    func finish() {
        lock.withLock {
            continuation?.finish()
            continuation = nil
        }
    }

    func consume(_ sampleBuffer: CMSampleBuffer) {
        lock.withLock { _ = continuation?.yield(Sample(buffer: sampleBuffer)) }
    }

    func consumeAudio(_ sampleBuffer: CMSampleBuffer) {
        lock.withLock { _ = continuation?.yield(Sample(buffer: sampleBuffer)) }
    }
}

private struct StreamError: LocalizedError {
    var errorDescription: String?
    init(_ message: String) { errorDescription = message }
}
