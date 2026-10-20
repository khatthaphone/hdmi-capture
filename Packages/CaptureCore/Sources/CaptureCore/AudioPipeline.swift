import Atomics
import AVFoundation
import os

public struct AudioLevels: Equatable, Sendable {
    /// Linear 0…1 per channel (left, right).
    public var peak: SIMD2<Float> = .zero
    public var rms: SIMD2<Float> = .zero
    /// Audio currently buffered ahead of the output, in milliseconds.
    public var bufferedMs: Double = 0

    public init() {}
}

/// Plays captured HDMI audio through AVAudioEngine with adjustable delay, channel mapping,
/// output-device choice and level metering (spec §11.1).
///
/// Capture queue → `enqueue` (convert, map channels, meter) → ring buffer →
/// real-time render block (drift control, resample by ±1 frame) → mixer → output device.
public final class AudioPipeline: @unchecked Sendable {
    public struct Settings: Equatable, Sendable {
        public var delayMs: Int = 0
        public var channelMode: ChannelMode = .auto
        public var volume: Float = 1
        public var muted = false
        /// Core Audio device UID; nil follows the system default output.
        public var outputDeviceUID: String?

        public init(delayMs: Int = 0, channelMode: ChannelMode = .auto, volume: Float = 1, muted: Bool = false, outputDeviceUID: String? = nil) {
            self.delayMs = delayMs
            self.channelMode = channelMode
            self.volume = volume
            self.muted = muted
            self.outputDeviceUID = outputDeviceUID
        }
    }

    public static let sampleRate: Double = 48_000
    static let log = Logger(subsystem: "hdmicaptain", category: "audio")
    /// Requested from AVCaptureAudioDataOutput so the converter does the heavy lifting.
    public static let captureSettings: [String: Any] = [
        AVFormatIDKey: kAudioFormatLinearPCM,
        AVSampleRateKey: sampleRate,
        AVNumberOfChannelsKey: 2,
        AVLinearPCMBitDepthKey: 32,
        AVLinearPCMIsFloatKey: true,
        AVLinearPCMIsNonInterleaved: false,
        AVLinearPCMIsBigEndianKey: false,
    ]

    private let ring = AudioRingBuffer(capacityFrames: 1 << 16, channels: 2)
    private let controlQueue = DispatchQueue(label: "hdmicaptain.audio.control")
    private var engine: AVAudioEngine?
    private var configObserver: NSObjectProtocol?
    private var settings = Settings()
    private var sourceChannels = 2

    // Producer state (capture audio queue); guarded by `producerLock` against settings changes.
    private let producerLock = NSLock()
    private var channelMode: ChannelMode = .normal
    private var scratch: [Float] = []
    private var levels = AudioLevels()
    private var lastFillLog: CFTimeInterval = 0
    /// Converts whatever the capture device delivers (iOS: its native format) to 48 kHz stereo float.
    private var converter: AVAudioConverter?
    private var converterSource: AVAudioFormat?
    private let pipelineFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 2, interleaved: true)!
    #if os(iOS)
    private var interruptionObserver: NSObjectProtocol?
    #endif

    // Shared with the render thread.
    private let delayFrames = ManagedAtomic<Int>(0)
    private let chunkFrames = ManagedAtomic<Int>(1024)
    private let running = ManagedAtomic<Bool>(false)

    // Render-thread state (touched only inside the render block).
    private var drift = DriftController(targetFrames: 2048)
    private let renderScratch = UnsafeMutablePointer<Float>.allocate(capacity: 8192 * 2)
    private let renderScratchFrames = 8192

    public init() {
        #if os(iOS)
        // A phone call, FaceTime or Siri stops the engine; bring it back when they're done.
        interruptionObserver = NotificationCenter.default.addObserver(
            forName: AVAudioSession.interruptionNotification, object: nil, queue: nil
        ) { [weak self] note in
            let raw = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt
            guard raw.flatMap(AVAudioSession.InterruptionType.init(rawValue:)) == .ended else { return }
            self?.controlQueue.async {
                guard let self, self.engine != nil else { return }
                Self.log.info("Audio interruption ended; restarting output")
                try? AVAudioSession.sharedInstance().setActive(true)
                self.stopEngine()
                self.startEngine()
            }
        }
        #endif
    }

    deinit {
        renderScratch.deallocate()
    }

    // MARK: Control

    /// Starts playback for a source with the given channel count (1 = mono stick).
    public func start(sourceChannels: Int) {
        controlQueue.async { [self] in
            self.sourceChannels = sourceChannels
            applyChannelMode()
            startEngine()
        }
    }

    public func stop() {
        controlQueue.async { [self] in stopEngine() }
    }

    public func update(_ newSettings: Settings) {
        controlQueue.async { [self] in
            let old = settings
            settings = newSettings
            delayFrames.store(Int(Double(newSettings.delayMs) / 1000 * Self.sampleRate), ordering: .relaxed)
            applyChannelMode()
            engine?.mainMixerNode.outputVolume = newSettings.muted ? 0 : newSettings.volume
            if old.outputDeviceUID != newSettings.outputDeviceUID, engine != nil {
                stopEngine()
                startEngine()
            }
        }
    }

    public func currentLevels() -> AudioLevels {
        producerLock.withLock { levels }
    }

    private func applyChannelMode() {
        let mode = settings.channelMode.resolved(sourceChannels: sourceChannels)
        producerLock.withLock { channelMode = mode }
    }

    private func startEngine() {
        guard engine == nil else { return }
        let engine = AVAudioEngine()
        let format = AVAudioFormat(standardFormatWithSampleRate: Self.sampleRate, channels: 2)!
        let source = AVAudioSourceNode(format: format) { [unowned self] isSilence, _, frameCount, buffers in
            render(isSilence: isSilence, frameCount: Int(frameCount), buffers: UnsafeMutableAudioBufferListPointer(buffers))
        }
        engine.attach(source)
        engine.connect(source, to: engine.mainMixerNode, format: format)
        engine.mainMixerNode.outputVolume = settings.muted ? 0 : settings.volume
        #if os(macOS)
        setOutputDevice(of: engine, uid: settings.outputDeviceUID)
        #endif
        ring.discard(ring.availableFrames)
        drift = DriftController(targetFrames: 2048)
        do {
            engine.prepare()
            try engine.start()
        } catch {
            Self.log.error("Audio engine failed to start: \(error.localizedDescription, privacy: .public)")
            return
        }
        self.engine = engine
        running.store(true, ordering: .releasing)
        // Output device changes (default switched, AirPods connected) reconfigure the engine; rebuild it.
        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil
        ) { [weak self] _ in
            self?.controlQueue.async {
                guard let self, self.engine != nil else { return }
                Self.log.info("Audio configuration changed; restarting output")
                self.stopEngine()
                self.startEngine()
            }
        }
        Self.log.info("Audio output started (source channels: \(self.sourceChannels))")
    }

    private func stopEngine() {
        running.store(false, ordering: .releasing)
        if let configObserver { NotificationCenter.default.removeObserver(configObserver) }
        configObserver = nil
        engine?.stop()
        engine = nil
    }

    #if os(macOS)
    private func setOutputDevice(of engine: AVAudioEngine, uid: String?) {
        guard let uid, var deviceID = AudioDevices.deviceID(forUID: uid),
              let unit = engine.outputNode.audioUnit
        else { return }
        let status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                          &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size))
        if status != noErr { Self.log.error("Could not select output device (\(status))") }
    }
    #endif

    // MARK: Producer (capture audio queue)

    public func enqueue(_ sampleBuffer: CMSampleBuffer) {
        guard running.load(ordering: .acquiring) else { return }
        ingest(sampleBuffer)
    }

    /// Converts if needed, then maps channels, meters and buffers one capture chunk.
    func ingest(_ sampleBuffer: CMSampleBuffer) {
        guard let description = CMSampleBufferGetFormatDescription(sampleBuffer),
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(description)?.pointee
        else { return }
        let matches = asbd.mFormatID == kAudioFormatLinearPCM && asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0
            && asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0
            && asbd.mChannelsPerFrame == 2 && asbd.mBitsPerChannel == 32 && asbd.mSampleRate == Self.sampleRate
        guard !matches else {
            ingestNative(sampleBuffer)
            return
        }
        // iOS can't ask AVCaptureAudioDataOutput for a format; it delivers the device's own (often 16-bit).
        guard let converted = convert(sampleBuffer, description: description),
              let samples = converted.floatChannelData?[0]
        else { return }
        write(samples, frames: Int(converted.frameLength))
    }

    private func convert(_ sampleBuffer: CMSampleBuffer, description: CMAudioFormatDescription) -> AVAudioPCMBuffer? {
        let source = AVAudioFormat(cmAudioFormatDescription: description)
        if converterSource != source {
            converter = AVAudioConverter(from: source, to: pipelineFormat)
            converterSource = source
            Self.log.info("Converting capture audio from \(source.description, privacy: .public)")
        }
        let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sampleBuffer))
        guard let converter, frames > 0,
              let input = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: frames)
        else { return nil }
        input.frameLength = frames
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sampleBuffer, at: 0, frameCount: Int32(frames),
                                                           into: input.mutableAudioBufferList) == noErr
        else { return nil }
        let capacity = AVAudioFrameCount((Double(frames) * Self.sampleRate / source.sampleRate).rounded(.up)) + 64
        guard let output = AVAudioPCMBuffer(pcmFormat: pipelineFormat, frameCapacity: capacity) else { return nil }
        var supplied = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, inputStatus in
            if supplied {
                inputStatus.pointee = .noDataNow
                return nil
            }
            supplied = true
            inputStatus.pointee = .haveData
            return input
        }
        guard status != .error, output.frameLength > 0 else { return nil }
        return output
    }

    private func ingestNative(_ sampleBuffer: CMSampleBuffer) {
        var blockBuffer: CMBlockBuffer?
        var list = AudioBufferList()
        let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: &list,
            bufferListSize: MemoryLayout<AudioBufferList>.size, blockBufferAllocator: nil,
            blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: &blockBuffer
        )
        guard status == noErr, let data = list.mBuffers.mData else { return }
        let frames = Int(list.mBuffers.mDataByteSize) / (MemoryLayout<Float>.size * 2)
        guard frames > 0 else { return }
        write(data.assumingMemoryBound(to: Float.self), frames: frames)
    }

    /// Interleaved 48 kHz stereo float in → channel map, meter, ring buffer.
    private func write(_ data: UnsafePointer<Float>, frames: Int) {
        chunkFrames.store(frames, ordering: .relaxed)

        producerLock.lock()
        defer { producerLock.unlock() }
        if scratch.count < frames * 2 { scratch = [Float](repeating: 0, count: frames * 2) }
        scratch.withUnsafeMutableBufferPointer { buffer in
            let samples = buffer.baseAddress!
            samples.update(from: data, count: frames * 2)
            channelMode.apply(to: samples, frames: frames)
            meter(samples, frames: frames)
            ring.write(samples, frames: frames)
        }
        levels.bufferedMs = Double(ring.availableFrames) / Self.sampleRate * 1000

        let now = CACurrentMediaTime()
        if now - lastFillLog > 10 {
            lastFillLog = now
            let delayMs = Double(delayFrames.load(ordering: .relaxed)) / Self.sampleRate * 1000
            Self.log.debug("Audio buffered \(self.levels.bufferedMs, format: .fixed(precision: 1)) ms, delay \(delayMs, format: .fixed(precision: 0)) ms, chunk \(frames) frames")
        }
    }

    private func meter(_ samples: UnsafePointer<Float>, frames: Int) {
        var peak = SIMD2<Float>.zero
        var sum = SIMD2<Float>.zero
        for i in 0..<frames {
            let s = SIMD2(samples[2 * i], samples[2 * i + 1])
            peak = pointwiseMax(peak, abs(s))
            sum += s * s
        }
        let rms = (sum / Float(frames)).squareRoot()
        // Fast attack, slow release, so meters are readable at UI refresh rates.
        levels.peak = pointwiseMax(peak, levels.peak * 0.85)
        levels.rms = pointwiseMax(rms, levels.rms * 0.85)
    }

    // MARK: Render (real-time thread: no locks, no allocation)

    private func render(isSilence: UnsafeMutablePointer<ObjCBool>, frameCount: Int, buffers: UnsafeMutableAudioBufferListPointer) -> OSStatus {
        guard buffers.count >= 2,
              let left = buffers[0].mData?.assumingMemoryBound(to: Float.self),
              let right = buffers[1].mData?.assumingMemoryBound(to: Float.self)
        else { return noErr }

        drift.targetFrames = DriftController.target(
            delayFrames: delayFrames.load(ordering: .relaxed),
            chunkFrames: chunkFrames.load(ordering: .relaxed),
            quantumFrames: frameCount
        )

        switch drift.plan(available: ring.availableFrames, request: frameCount) {
        case .silence:
            left.update(repeating: 0, count: frameCount)
            right.update(repeating: 0, count: frameCount)
            isSilence.pointee = true
        case let .play(discard, consume):
            if discard > 0 { ring.discard(discard) }
            let count = min(consume, renderScratchFrames)
            let got = ring.read(into: renderScratch, frames: count)
            Self.stretch(renderScratch, frames: got, toLeft: left, right: right, frames: frameCount)
        }
        return noErr
    }

    /// Linear-interpolates `frames` interleaved stereo frames onto `outFrames` planar frames.
    static func stretch(_ source: UnsafePointer<Float>, frames: Int, toLeft left: UnsafeMutablePointer<Float>,
                        right: UnsafeMutablePointer<Float>, frames outFrames: Int) {
        guard frames > 0 else {
            left.update(repeating: 0, count: outFrames)
            right.update(repeating: 0, count: outFrames)
            return
        }
        if frames == outFrames {
            for i in 0..<outFrames {
                left[i] = source[2 * i]
                right[i] = source[2 * i + 1]
            }
            return
        }
        let step = outFrames > 1 ? Double(frames - 1) / Double(outFrames - 1) : 0
        for i in 0..<outFrames {
            let position = Double(i) * step
            let i0 = Int(position)
            let i1 = min(i0 + 1, frames - 1)
            let t = Float(position - Double(i0))
            left[i] = source[2 * i0] * (1 - t) + source[2 * i1] * t
            right[i] = source[2 * i0 + 1] * (1 - t) + source[2 * i1 + 1] * t
        }
    }
}
