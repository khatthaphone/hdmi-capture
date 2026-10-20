import AVFoundation
import Testing
@testable import CaptureCore

/// Synthetic capture: video frames from a test pattern plus a 1 kHz tone, both on one clock.
private func videoSample(_ buffer: CVPixelBuffer, at time: CMTime) -> CMSampleBuffer {
    var format: CMVideoFormatDescription?
    CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil, imageBuffer: buffer, formatDescriptionOut: &format)
    var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 60), presentationTimeStamp: time, decodeTimeStamp: .invalid)
    var sample: CMSampleBuffer?
    CMSampleBufferCreateReadyWithImageBuffer(allocator: nil, imageBuffer: buffer, formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample)
    return sample!
}

private func audioSample(frames: Int, startFrame: Int) -> CMSampleBuffer {
    var asbd = AudioStreamBasicDescription(
        mSampleRate: 48_000, mFormatID: kAudioFormatLinearPCM,
        mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
        mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8, mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0
    )
    var format: CMAudioFormatDescription?
    CMAudioFormatDescriptionCreate(allocator: nil, asbd: &asbd, layoutSize: 0, layout: nil, magicCookieSize: 0, magicCookie: nil, extensions: nil, formatDescriptionOut: &format)
    var samples = [Float](repeating: 0, count: frames * 2)
    for i in 0..<frames {
        let v = Float(sin(2 * Double.pi * 1000 * Double(startFrame + i) / 48_000)) * 0.3
        samples[2 * i] = v
        samples[2 * i + 1] = v
    }
    var block: CMBlockBuffer?
    let bytes = frames * 8
    CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: bytes, blockAllocator: nil, customBlockSource: nil, offsetToData: 0, dataLength: bytes, flags: 0, blockBufferOut: &block)
    samples.withUnsafeBytes { _ = CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block!, offsetIntoDestination: 0, dataLength: bytes) }
    var sample: CMSampleBuffer?
    CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: nil, dataBuffer: block!, formatDescription: format!, sampleCount: frames,
                                                         presentationTimeStamp: CMTime(value: CMTimeValue(startFrame), timescale: 48_000),
                                                         packetDescriptions: nil, sampleBufferOut: &sample)
    return sample!
}

struct RecorderTests {
    @Test(arguments: Recorder.Codec.allCases)
    func writesPlayableFileWithVideoAndAudio(codec: Recorder.Codec) async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let url = Recorder.fileURL(in: folder)
        let recorder = Recorder()
        recorder.start(url: url, settings: .init(codec: codec), frameRate: 60, includeAudio: true)

        let frame = try #require(TestPattern.smpteBars.makePixelBuffer(width: 1280, height: 720))
        var audioFrame = 0
        for i in 0..<120 { // 2 s at 60 fps
            recorder.consume(videoSample(frame, at: CMTime(value: CMTimeValue(i * 800), timescale: 48_000)))
            while audioFrame < (i + 1) * 800 {
                recorder.consumeAudio(audioSample(frames: 1024, startFrame: audioFrame))
                audioFrame += 1024
            }
            try await Task.sleep(for: .milliseconds(4))
        }
        let finished: URL? = await withCheckedContinuation { continuation in
            recorder.stop { continuation.resume(returning: $0) }
        }
        #expect(finished == url)

        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration).seconds
        #expect(abs(duration - 2) < 0.1, "duration \(duration)")
        let video = try #require(try await asset.loadTracks(withMediaType: .video).first)
        let size = try await video.load(.naturalSize)
        #expect(size == CGSize(width: 1280, height: 720))
        let audio = try await asset.loadTracks(withMediaType: .audio)
        #expect(audio.count == 1)
        let videoStart = try await video.load(.timeRange).start.seconds
        let audioStart = try await audio[0].load(.timeRange).start.seconds
        #expect(abs(videoStart - audioStart) < 1.0 / 60 + 0.025, "A/V start offset \(videoStart - audioStart)")
        try? FileManager.default.removeItem(at: folder)
    }

    @Test func stopWithoutFramesWritesNothing() async {
        let recorder = Recorder()
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).mov")
        recorder.start(url: url, settings: .init(), frameRate: 60, includeAudio: false)
        let finished: URL? = await withCheckedContinuation { continuation in
            recorder.stop { continuation.resume(returning: $0) }
        }
        #expect(finished == nil)
        #expect(!FileManager.default.fileExists(atPath: url.path))
    }
}
