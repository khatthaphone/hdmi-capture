import AVFoundation
import os
import VideoToolbox

/// "Save Recent" (Pro), PlayStation-style: while enabled, keeps the last `keepSeconds` of the
/// input on disk as short segment files, and `save(last:to:)` stitches the segments covering the
/// requested span into one file without re-encoding.
///
/// One HEVC encoder runs for the whole buffer (keyframe every second); its output is written into
/// segment files by passthrough writers that rotate on keyframes. Rotation never creates encoders,
/// which matters because hardware encoder sessions are a limited system resource.
public final class RollingRecorder: FrameSink, AudioSampleSink, @unchecked Sendable {
    struct Segment {
        let url: URL
        let start: CMTime
        let end: CMTime
    }

    static let log = Logger(subsystem: "hdmicaptain", category: "recent")

    /// Where segments live (deleted when disabled).
    public let folder: URL
    /// Target length of each segment file.
    public let segmentSeconds: Double

    // Guarded by `lock`.
    private let lock = NSLock()
    private var enabled = false
    private var keep: Double = 15 * 60
    private var frameRate: Double = 60
    private var includeAudio = true
    private var encoder: VTCompressionSession?
    private var encoderSize: (width: Int, height: Int)?
    private var lastKeyframeRequest: CMTime = .invalid
    private var segments: [Segment] = []
    private var latest: CMTime = .invalid

    // Touched only on `writerQueue`.
    private let writerQueue = DispatchQueue(label: "hdmicaptain.recent.writer", qos: .userInitiated)
    private var current: SegmentWriter?
    private var counter = 0

    /// Segment files still being finalized; `save` waits for them.
    private let closing = DispatchGroup()

    public init(folder: URL, segmentSeconds: Double = 10) {
        self.folder = folder
        self.segmentSeconds = segmentSeconds
    }

    deinit {
        if let encoder { VTCompressionSessionInvalidate(encoder) }
    }

    // MARK: Configuration

    public var isEnabled: Bool { lock.withLock { enabled } }

    /// Seconds of footage kept (older segments are deleted).
    public var keepSeconds: Double {
        get { lock.withLock { keep } }
        set { lock.withLock { keep = max(newValue, segmentSeconds) } }
    }

    public func configure(frameRate: Double, includeAudio: Bool) {
        lock.withLock {
            self.frameRate = max(1, frameRate)
            self.includeAudio = includeAudio
        }
    }

    public func setEnabled(_ value: Bool) {
        let oldEncoder: VTCompressionSession? = lock.withLock {
            guard enabled != value else { return nil }
            enabled = value
            if value {
                try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                return nil
            }
            defer { encoder = nil; encoderSize = nil; segments = []; latest = .invalid; lastKeyframeRequest = .invalid }
            return encoder
        }
        guard !value else { return }
        if let oldEncoder { VTCompressionSessionInvalidate(oldEncoder) }
        writerQueue.async { [self] in
            let writer = current
            current = nil
            let removeFolder = { [folder] in try? FileManager.default.removeItem(at: folder) }
            if let writer { writer.finish { _ in removeFolder() } } else { removeFolder() }
        }
    }

    /// Completed segments on disk (for tests and diagnostics).
    var segmentCount: Int { lock.withLock { segments.count } }

    /// Seconds currently available to save.
    public var bufferedSeconds: Double {
        lock.withLock {
            guard let first = segments.first?.start, latest.isValid else { return 0 }
            return max(0, (latest - first).seconds)
        }
    }

    // MARK: Sinks

    public func consume(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        let width = CVPixelBufferGetWidth(pixelBuffer), height = CVPixelBufferGetHeight(pixelBuffer)
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let (encoder, forceKeyframe): (VTCompressionSession?, Bool) = lock.withLock {
            guard enabled else { return (nil, false) }
            if encoderSize.map({ $0 != (width, height) }) ?? true {
                // New or changed mode: start a fresh encoder; the next segment begins at its first keyframe.
                if let old = self.encoder { VTCompressionSessionInvalidate(old) }
                self.encoder = makeEncoder(width: width, height: height, fps: frameRate)
                encoderSize = (width, height)
                lastKeyframeRequest = .invalid
            }
            let force = !lastKeyframeRequest.isValid || (pts - lastKeyframeRequest).seconds >= 1
            if force { lastKeyframeRequest = pts }
            return (self.encoder, force)
        }
        guard let encoder else { return }
        let properties = forceKeyframe ? [kVTEncodeFrameOptionKey_ForceKeyFrame: kCFBooleanTrue] as CFDictionary : nil
        VTCompressionSessionEncodeFrame(encoder, imageBuffer: pixelBuffer, presentationTimeStamp: pts,
                                        duration: CMSampleBufferGetDuration(sampleBuffer), frameProperties: properties,
                                        infoFlagsOut: nil) { [weak self] status, _, encoded in
            guard status == noErr, let encoded, let self else { return }
            self.writerQueue.async { self.write(encoded) }
        }
    }

    public func consumeAudio(_ sampleBuffer: CMSampleBuffer) {
        guard isEnabled else { return }
        writerQueue.async { [self] in current?.appendAudio(sampleBuffer) }
    }

    // MARK: Writing (writer queue)

    private func write(_ encoded: CMSampleBuffer) {
        guard isEnabled else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(encoded)
        let keyframe = Self.isKeyframe(encoded)
        if keyframe, current.map({ (pts - $0.start).seconds >= segmentSeconds }) ?? true {
            rotate(at: pts, format: CMSampleBufferGetFormatDescription(encoded))
        }
        current?.appendVideo(encoded)
        lock.withLock { latest = pts }
    }

    /// Finishes the running segment and (with a format) starts the next one at `pts`.
    private func rotate(at pts: CMTime, format: CMFormatDescription?) {
        if let finished = current {
            current = nil
            closing.enter()
            finished.finish { [weak self, closing] url in
                defer { closing.leave() }
                guard let self, let url else { return }
                self.lock.withLock {
                    guard self.enabled else { return }
                    self.segments.append(Segment(url: url, start: finished.start, end: pts))
                    self.segments.sort { $0.start < $1.start }
                    self.prune()
                }
            }
        }
        guard let format else { return }
        counter += 1
        let url = folder.appendingPathComponent(String(format: "segment-%06d.mov", counter))
        do {
            current = try SegmentWriter(url: url, start: pts, format: format, includeAudio: lock.withLock { includeAudio })
        } catch {
            Self.log.error("Segment writer failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Deletes segments that are entirely older than `keep`. Caller holds `lock`.
    private func prune() {
        guard latest.isValid else { return }
        while let first = segments.first, (latest - first.end).seconds > keep {
            try? FileManager.default.removeItem(at: first.url)
            segments.removeFirst()
        }
    }

    static func isKeyframe(_ sample: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]],
              let first = attachments.first
        else { return true }
        return !(first[kCMSampleAttachmentKey_NotSync] as? Bool ?? false)
    }

    private func makeEncoder(width: Int, height: Int, fps: Double) -> VTCompressionSession? {
        var session: VTCompressionSession?
        let status = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
                                                codecType: kCMVideoCodecType_HEVC, encoderSpecification: nil,
                                                imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                outputCallback: nil, refcon: nil, compressionSessionOut: &session)
        guard status == noErr, let session else {
            Self.log.error("Save Recent encoder unavailable (\(status))")
            return nil
        }
        let bitrate = Int(Double(width * height) * fps * 0.10)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: fps as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: bitrate as CFNumber)
        VTCompressionSessionPrepareToEncodeFrames(session)
        Self.log.info("Save Recent encoding \(width)x\(height) HEVC \(bitrate / 1_000_000) Mbps")
        return session
    }

    // MARK: Save

    public enum SaveError: LocalizedError {
        case empty
        case export(String)

        public var errorDescription: String? {
            switch self {
            case .empty: "Nothing recorded yet"
            case let .export(message): message
            }
        }
    }

    /// Saves the last `seconds` (nil = everything kept) to `url`. Capture keeps running.
    public func save(last seconds: Double?, to url: URL) async throws {
        // Close the running segment so the newest moments are included; the next keyframe starts a new one.
        await withCheckedContinuation { continuation in
            writerQueue.async { [self] in
                rotate(at: lock.withLock { latest }, format: nil)
                continuation.resume()
            }
        }
        await withCheckedContinuation { continuation in
            closing.notify(queue: .global()) { continuation.resume() }
        }
        let available = lock.withLock { segments }
        guard let newest = available.last else { throw SaveError.empty }
        let cutoff = seconds.map { newest.end - CMTime(seconds: $0, preferredTimescale: 600) }
        let chosen = available.filter { segment in cutoff.map { segment.end > $0 } ?? true }

        let composition = AVMutableComposition()
        let videoTrack = composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
        let audioTrack = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
        var cursor = CMTime.zero
        for segment in chosen {
            let asset = AVURLAsset(url: segment.url)
            guard let video = try await asset.loadTracks(withMediaType: .video).first else { continue }
            let range = try await video.load(.timeRange)
            try videoTrack?.insertTimeRange(range, of: video, at: cursor)
            if let audio = try await asset.loadTracks(withMediaType: .audio).first {
                let audioRange = try await audio.load(.timeRange)
                try? audioTrack?.insertTimeRange(CMTimeRangeGetIntersection(audioRange, otherRange: range), of: audio, at: cursor)
            }
            cursor = cursor + range.duration
        }
        // Trim the front to the requested span (segments start on keyframes, so the cut stays clean).
        if let seconds, cursor.seconds > seconds {
            composition.removeTimeRange(CMTimeRange(start: .zero, duration: CMTime(seconds: cursor.seconds - seconds, preferredTimescale: 600)))
        }
        if let audioTrack, audioTrack.segments.isEmpty { composition.removeTrack(audioTrack) }

        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard let export = AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough) else {
            throw SaveError.export("Can't export")
        }
        export.outputURL = url
        export.outputFileType = .mov
        await export.export()
        guard export.status == .completed else {
            throw SaveError.export(export.error?.localizedDescription ?? "Export failed")
        }
        Self.log.info("Saved recent \(composition.duration.seconds, format: .fixed(precision: 1)) s from \(chosen.count) segments")
    }

    /// "HDMI Captain Recent 2026-10-20 at 14.05.09.mov", unique within `folder`.
    public static func fileURL(in folder: URL, date: Date = Date()) -> URL {
        let base = Recorder.fileURL(in: folder, date: date).deletingPathExtension().lastPathComponent
            .replacingOccurrences(of: "HDMI Captain ", with: "HDMI Captain Recent ")
        var url = folder.appendingPathComponent(base).appendingPathExtension("mov")
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = folder.appendingPathComponent("\(base) \(n)").appendingPathExtension("mov")
            n += 1
        }
        return url
    }
}

/// One segment file: HEVC passthrough plus AAC encoded from the captured PCM.
private final class SegmentWriter {
    let url: URL
    let start: CMTime
    private let writer: AVAssetWriter
    private let video: AVAssetWriterInput
    private let audio: AVAssetWriterInput?
    private var started = false

    init(url: URL, start: CMTime, format: CMFormatDescription, includeAudio: Bool) throws {
        self.url = url
        self.start = start
        writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        video = AVAssetWriterInput(mediaType: .video, outputSettings: nil, sourceFormatHint: format)
        video.expectsMediaDataInRealTime = true
        writer.add(video)
        if includeAudio {
            let input = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 160_000,
            ])
            input.expectsMediaDataInRealTime = true
            if writer.canAdd(input) { writer.add(input); audio = input } else { audio = nil }
        } else {
            audio = nil
        }
        guard writer.startWriting() else { throw writer.error ?? CocoaError(.fileWriteUnknown) }
    }

    func appendVideo(_ sample: CMSampleBuffer) {
        if !started {
            writer.startSession(atSourceTime: CMSampleBufferGetPresentationTimeStamp(sample))
            started = true
        }
        if video.isReadyForMoreMediaData { video.append(sample) }
    }

    func appendAudio(_ sample: CMSampleBuffer) {
        guard started, let audio, audio.isReadyForMoreMediaData,
              CMSampleBufferGetPresentationTimeStamp(sample) >= start
        else { return }
        audio.append(sample)
    }

    func finish(completion: @escaping (URL?) -> Void) {
        guard started else {
            writer.cancelWriting()
            completion(nil)
            return
        }
        video.markAsFinished()
        audio?.markAsFinished()
        writer.finishWriting { [writer, url] in
            completion(writer.status == .completed ? url : nil)
        }
    }
}
