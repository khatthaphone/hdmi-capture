import AVFoundation
import os

/// Records the live input (Pro). Video and audio are appended with their capture timestamps,
/// so sync doesn't depend on the monitoring delay. The writer is created on the first frame so
/// the file always matches what the card actually delivers.
public final class Recorder: FrameSink, AudioSampleSink, @unchecked Sendable {
    public enum Codec: String, Codable, CaseIterable, Sendable {
        case hevc, h264

        public var title: String { self == .hevc ? "HEVC (H.265)" : "H.264" }
        var avCodec: AVVideoCodecType { self == .hevc ? .hevc : .h264 }
        /// Bits per pixel per frame — visually clean for game/camera footage.
        var bitsPerPixel: Double { self == .hevc ? 0.15 : 0.25 }
    }

    public struct Settings: Equatable, Sendable {
        public var codec: Codec = .hevc
        /// Overrides the automatic bitrate when set (bits per second).
        public var bitrate: Int?

        public init(codec: Codec = .hevc, bitrate: Int? = nil) {
            self.codec = codec
            self.bitrate = bitrate
        }
    }

    public enum State: Equatable, Sendable {
        case idle
        case recording(url: URL, startedAt: Date)
        case finished(url: URL)
        case failed(String)
    }

    static let log = Logger(subsystem: "hdmicaptain", category: "recorder")

    /// Called on the main queue.
    public var onStateChange: ((State) -> Void)?

    private let queue = DispatchQueue(label: "hdmicaptain.recorder", qos: .userInitiated)
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInput: AVAssetWriterInput?
    private var armed: (url: URL, settings: Settings, frameRate: Double, includeAudio: Bool)?
    private var sessionStarted = false
    private var state: State = .idle

    public init() {}

    public var isRecording: Bool {
        queue.sync { armed != nil }
    }

    /// Arms recording; the file is created on the next video frame.
    public func start(url: URL, settings: Settings, frameRate: Double, includeAudio: Bool) {
        queue.async { [self] in
            guard armed == nil else { return }
            armed = (url, settings, frameRate, includeAudio)
            sessionStarted = false
            emit(.recording(url: url, startedAt: Date()))
        }
    }

    /// Finishes the file. Safe to call when not recording.
    public func stop(completion: (@Sendable (URL?) -> Void)? = nil) {
        queue.async { [self] in
            guard let armed else {
                completion?(nil)
                return
            }
            self.armed = nil
            guard let writer, writer.status == .writing else {
                reset()
                // Nothing was written (no frames arrived).
                emit(.idle)
                completion?(nil)
                return
            }
            videoInput?.markAsFinished()
            audioInput?.markAsFinished()
            writer.finishWriting { [self] in
                queue.async { [self] in
                    let ok = writer.status == .completed
                    if !ok { Self.log.error("Finish failed: \(writer.error?.localizedDescription ?? "unknown", privacy: .public)") }
                    reset()
                    emit(ok ? .finished(url: armed.url) : .failed(writer.error?.localizedDescription ?? "Recording failed"))
                    completion?(ok ? armed.url : nil)
                }
            }
        }
    }

    // MARK: Sinks (capture queues)

    public func consume(_ sampleBuffer: CMSampleBuffer) {
        queue.async { [self] in appendVideo(sampleBuffer) }
    }

    public func consumeAudio(_ sampleBuffer: CMSampleBuffer) {
        queue.async { [self] in appendAudio(sampleBuffer) }
    }

    // MARK: Writing (recorder queue)

    private func appendVideo(_ sampleBuffer: CMSampleBuffer) {
        guard let armed else { return }
        if writer == nil {
            guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
            do {
                try makeWriter(width: CVPixelBufferGetWidth(pixelBuffer), height: CVPixelBufferGetHeight(pixelBuffer), armed: armed)
            } catch {
                Self.log.error("Writer setup failed: \(error.localizedDescription, privacy: .public)")
                self.armed = nil
                reset()
                emit(.failed(error.localizedDescription))
                return
            }
        }
        guard let writer, writer.status == .writing, let videoInput else { return }
        if !sessionStarted {
            writer.startSession(atSourceTime: CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
            sessionStarted = true
        }
        if videoInput.isReadyForMoreMediaData, !videoInput.append(sampleBuffer) {
            Self.log.error("Video append failed: \(writer.error?.localizedDescription ?? "", privacy: .public)")
        }
    }

    private func appendAudio(_ sampleBuffer: CMSampleBuffer) {
        // Audio before the first video frame has nothing to sync to; drop it.
        guard armed != nil, sessionStarted, let writer, writer.status == .writing, let audioInput else { return }
        if audioInput.isReadyForMoreMediaData {
            audioInput.append(sampleBuffer)
        }
    }

    private func makeWriter(width: Int, height: Int, armed: (url: URL, settings: Settings, frameRate: Double, includeAudio: Bool)) throws {
        try FileManager.default.createDirectory(at: armed.url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let writer = try AVAssetWriter(outputURL: armed.url, fileType: .mov)
        // Fragments keep the file playable if the app or Mac dies mid-recording.
        writer.movieFragmentInterval = CMTime(seconds: 2, preferredTimescale: 600)

        let fps = max(1, armed.frameRate)
        let bitrate = armed.settings.bitrate ?? Int(Double(width * height) * fps * armed.settings.codec.bitsPerPixel)
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: armed.settings.codec.avCodec,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: bitrate,
                AVVideoExpectedSourceFrameRateKey: fps,
                AVVideoMaxKeyFrameIntervalKey: Int(fps * 2),
                AVVideoAllowFrameReorderingKey: false,
            ],
        ])
        video.expectsMediaDataInRealTime = true
        guard writer.canAdd(video) else { throw RecorderError("Can't record \(width)×\(height) with \(armed.settings.codec.title)") }
        writer.add(video)

        var audio: AVAssetWriterInput?
        if armed.includeAudio {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 48_000,
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 192_000,
            ])
            input.expectsMediaDataInRealTime = true
            if writer.canAdd(input) {
                writer.add(input)
                audio = input
            }
        }
        guard writer.startWriting() else { throw writer.error ?? RecorderError("Can't start writing") }
        self.writer = writer
        videoInput = video
        audioInput = audio
        Self.log.info("Recording \(width)x\(height) \(armed.settings.codec.rawValue, privacy: .public) \(bitrate / 1_000_000) Mbps → \(armed.url.lastPathComponent, privacy: .public)")
    }

    private func reset() {
        writer = nil
        videoInput = nil
        audioInput = nil
        sessionStarted = false
    }

    private func emit(_ newState: State) {
        state = newState
        DispatchQueue.main.async { [weak self] in self?.onStateChange?(newState) }
    }

    /// "HDMI Captain 2026-10-20 at 14.05.09.mov", unique within `folder`.
    public static func fileURL(in folder: URL, date: Date = Date()) -> URL {
        let png = FrameSnapshot.fileURL(in: folder, date: date)
        var url = png.deletingPathExtension().appendingPathExtension("mov")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent(png.deletingPathExtension().lastPathComponent + " \(n)").appendingPathExtension("mov")
            n += 1
        }
        return url
    }
}

private struct RecorderError: LocalizedError {
    var errorDescription: String?
    init(_ message: String) { errorDescription = message }
}
