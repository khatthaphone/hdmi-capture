import AVFoundation
import os
import VideoToolbox

/// Instant replay (Pro): while enabled, encodes every frame and keeps the last `duration` seconds
/// in memory as whole GOPs; `save` writes them (plus audio) to a file without touching capture.
public final class ReplayBuffer: FrameSink, AudioSampleSink, @unchecked Sendable {
    static let log = Logger(subsystem: "hdmicaptain", category: "replay")

    /// Seconds kept (1…300; the UI offers 15…120).
    public var duration: Double {
        get { lock.withLock { _duration } }
        set { lock.withLock { _duration = min(max(newValue, 1), 300) } }
    }

    /// Upper bound for buffered encoded video, in bytes.
    public let memoryCap: Int

    public var isEnabled: Bool {
        get { lock.withLock { enabled } }
        set { setEnabled(newValue) }
    }

    private let lock = NSLock()
    private var _duration: Double = 30
    private var enabled = false
    private var session: VTCompressionSession?
    private var sessionSize: (width: Int, height: Int)?
    private var frameRate: Double = 60
    /// Encoded video, oldest first; each GOP starts with a keyframe.
    private var gops: [[CMSampleBuffer]] = []
    private var videoBytes = 0
    private var audio: [CMSampleBuffer] = []
    /// Timestamp of the last forced keyframe; encoders don't always honour interval properties.
    private var lastKeyframeRequest: CMTime = .invalid

    public init(memoryCap: Int = 400_000_000) {
        self.memoryCap = memoryCap
    }

    deinit {
        if let session { VTCompressionSessionInvalidate(session) }
    }

    /// Bytes of encoded video held right now.
    public var bufferedBytes: Int { lock.withLock { videoBytes } }

    /// Seconds of video currently available to save.
    public var bufferedSeconds: Double {
        lock.withLock {
            guard let first = gops.first?.first, let last = gops.last?.last else { return 0 }
            return (CMSampleBufferGetPresentationTimeStamp(last) - CMSampleBufferGetPresentationTimeStamp(first)).seconds
        }
    }

    public func setFrameRate(_ fps: Double) {
        lock.withLock { frameRate = max(1, fps) }
    }

    private func setEnabled(_ value: Bool) {
        let old = lock.withLock { () -> VTCompressionSession? in
            enabled = value
            guard !value else { return nil }
            defer { session = nil; sessionSize = nil; gops = []; audio = []; videoBytes = 0; lastKeyframeRequest = .invalid }
            return session
        }
        if let old { VTCompressionSessionInvalidate(old) }
    }

    // MARK: Sinks

    public func consume(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let width = CVPixelBufferGetWidth(pixelBuffer), height = CVPixelBufferGetHeight(pixelBuffer)
        let session: VTCompressionSession? = lock.withLock {
            guard enabled else { return nil }
            if let size = sessionSize, size == (width, height), let session = self.session { return session }
            // New or changed mode: start a fresh buffer.
            if let old = self.session { VTCompressionSessionInvalidate(old) }
            gops = []
            audio = []
            videoBytes = 0
            lastKeyframeRequest = .invalid
            self.session = makeSession(width: width, height: height, fps: frameRate)
            sessionSize = (width, height)
            return self.session
        }
        guard let session else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        // One keyframe per second keeps trimming (and the saved clip's start) precise.
        let forceKeyframe = lock.withLock {
            guard !lastKeyframeRequest.isValid || (pts - lastKeyframeRequest).seconds >= 1 else { return false }
            lastKeyframeRequest = pts
            return true
        }
        let properties = forceKeyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame: kCFBooleanTrue] as CFDictionary : nil
        VTCompressionSessionEncodeFrame(session, imageBuffer: pixelBuffer, presentationTimeStamp: pts,
                                        duration: CMSampleBufferGetDuration(sampleBuffer), frameProperties: properties,
                                        infoFlagsOut: nil) { [weak self] status, _, encoded in
            guard status == noErr, let encoded else { return }
            self?.store(encoded)
        }
    }

    public func consumeAudio(_ sampleBuffer: CMSampleBuffer) {
        lock.withLock {
            guard enabled, !gops.isEmpty else { return }
            audio.append(sampleBuffer)
            trimAudio()
        }
    }

    // MARK: Buffer

    private func store(_ encoded: CMSampleBuffer) {
        lock.withLock {
            guard enabled else { return }
            if Self.isKeyframe(encoded) || gops.isEmpty {
                guard Self.isKeyframe(encoded) else { return } // never start mid-GOP
                gops.append([encoded])
            } else {
                gops[gops.count - 1].append(encoded)
            }
            videoBytes += CMSampleBufferGetTotalSampleSize(encoded)
            // Keep whole GOPs: drop the oldest while the rest still covers `duration`, or over the cap.
            while gops.count > 1 {
                let secondStart = CMSampleBufferGetPresentationTimeStamp(gops[1][0])
                let newest = CMSampleBufferGetPresentationTimeStamp(encoded)
                guard (newest - secondStart).seconds >= _duration || videoBytes > memoryCap else { break }
                videoBytes -= gops.removeFirst().reduce(0) { $0 + CMSampleBufferGetTotalSampleSize($1) }
            }
            trimAudio()
        }
    }

    private func trimAudio() {
        guard let start = gops.first?.first.map(CMSampleBufferGetPresentationTimeStamp) else { return }
        audio.removeAll { (CMSampleBufferGetPresentationTimeStamp($0) + CMSampleBufferGetDuration($0)) < start }
    }

    static func isKeyframe(_ sample: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]],
              let first = attachments.first
        else { return true }
        return !(first[kCMSampleAttachmentKey_NotSync] as? Bool ?? false)
    }

    private func makeSession(width: Int, height: Int, fps: Double) -> VTCompressionSession? {
        var session: VTCompressionSession?
        let status = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
                                                codecType: kCMVideoCodecType_HEVC, encoderSpecification: nil,
                                                imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                outputCallback: nil, refcon: nil, compressionSessionOut: &session)
        guard status == noErr, let session else {
            Self.log.error("Replay encoder unavailable (\(status))")
            return nil
        }
        let bitrate = Int(Double(width * height) * fps * 0.12)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, value: 1 as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: Int(fps.rounded()) as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: fps as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: bitrate as CFNumber)
        VTCompressionSessionPrepareToEncodeFrames(session)
        Self.log.info("Replay buffer encoding \(width)x\(height) HEVC \(bitrate / 1_000_000) Mbps")
        return session
    }

    // MARK: Save

    public enum SaveError: LocalizedError {
        case empty
        case writer(String)
        public var errorDescription: String? {
            switch self {
            case .empty: "Nothing buffered yet"
            case let .writer(message): message
            }
        }
    }

    /// Writes the buffered moments to `url` (MOV, HEVC passthrough + AAC). Capture continues meanwhile.
    public func save(to url: URL) async throws {
        if let session = lock.withLock({ self.session }) {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        }
        let (video, audio) = lock.withLock { (gops.flatMap { $0 }, self.audio) }
        guard let first = video.first, let format = CMSampleBufferGetFormatDescription(first) else { throw SaveError.empty }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)

        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let videoInput = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: format)
        writer.add(videoInput)
        var audioInput: AVAssetWriterInput?
        if !audio.isEmpty {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 192_000,
            ])
            if writer.canAdd(input) { writer.add(input); audioInput = input }
        }
        guard writer.startWriting() else { throw SaveError.writer(writer.error?.localizedDescription ?? "Can't write replay") }
        writer.startSession(atSourceTime: CMSampleBufferGetPresentationTimeStamp(first))

        // Feed both tracks concurrently; the writer interleaves and would stall a sequential feed.
        async let videoDone: Void = Self.feed(video, to: videoInput, label: "video")
        if let audioInput { await Self.feed(audio, to: audioInput, label: "audio") }
        await videoDone
        await writer.finishWriting()
        guard writer.status == .completed else { throw SaveError.writer(writer.error?.localizedDescription ?? "Replay failed") }
    }

    private static func feed(_ samples: [CMSampleBuffer], to input: AVAssetWriterInput, label: String) async {
        let queue = DispatchQueue(label: "hdmicaptain.replay.\(label)")
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            var index = 0
            input.requestMediaDataWhenReady(on: queue) {
                while input.isReadyForMoreMediaData {
                    guard index < samples.count else {
                        input.markAsFinished()
                        continuation.resume()
                        return
                    }
                    input.append(samples[index])
                    index += 1
                }
            }
        }
    }

    /// "HDMI Captain Replay 2026-10-20 at 14.05.09.mov", unique within `folder`.
    public static func fileURL(in folder: URL, date: Date = Date()) -> URL {
        let base = Recorder.fileURL(in: folder, date: date).deletingPathExtension().lastPathComponent
            .replacingOccurrences(of: "HDMI Captain ", with: "HDMI Captain Replay ")
        var url = folder.appendingPathComponent(base).appendingPathExtension("mov")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent("\(base) \(n)").appendingPathExtension("mov")
            n += 1
        }
        return url
    }
}
