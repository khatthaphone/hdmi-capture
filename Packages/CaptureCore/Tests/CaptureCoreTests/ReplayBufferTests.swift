import AVFoundation
import Testing
@testable import CaptureCore

struct ReplayBufferTests {
    private func videoSample(_ buffer: CVPixelBuffer, frame: Int) -> CMSampleBuffer {
        var format: CMVideoFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil, imageBuffer: buffer, formatDescriptionOut: &format)
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 60), presentationTimeStamp: CMTime(value: CMTimeValue(frame), timescale: 60), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        CMSampleBufferCreateReadyWithImageBuffer(allocator: nil, imageBuffer: buffer, formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample)
        return sample!
    }

    private func audioSample(startFrame: Int, frames: Int = 800) -> CMSampleBuffer {
        var asbd = AudioStreamBasicDescription(mSampleRate: 48_000, mFormatID: kAudioFormatLinearPCM,
                                               mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
                                               mBytesPerPacket: 8, mFramesPerPacket: 1, mBytesPerFrame: 8, mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0)
        var format: CMAudioFormatDescription?
        CMAudioFormatDescriptionCreate(allocator: nil, asbd: &asbd, layoutSize: 0, layout: nil, magicCookieSize: 0, magicCookie: nil, extensions: nil, formatDescriptionOut: &format)
        var block: CMBlockBuffer?
        CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: frames * 8, blockAllocator: nil, customBlockSource: nil, offsetToData: 0, dataLength: frames * 8, flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &block)
        CMBlockBufferFillDataBytes(with: 0, blockBuffer: block!, offsetIntoDestination: 0, dataLength: frames * 8)
        var sample: CMSampleBuffer?
        CMAudioSampleBufferCreateReadyWithPacketDescriptions(allocator: nil, dataBuffer: block!, formatDescription: format!, sampleCount: frames,
                                                             presentationTimeStamp: CMTime(value: CMTimeValue(startFrame), timescale: 48_000), packetDescriptions: nil, sampleBufferOut: &sample)
        return sample!
    }

    /// Feeds `seconds` of 60 fps video + audio in real-ish time.
    private func feed(_ replay: ReplayBuffer, seconds: Int) async throws {
        let frame = try #require(TestPattern.grayRamp.makePixelBuffer(width: 640, height: 360))
        for i in 0..<(seconds * 60) {
            replay.consume(videoSample(frame, frame: i))
            replay.consumeAudio(audioSample(startFrame: i * 800))
            if i % 6 == 0 { try await Task.sleep(for: .milliseconds(1)) }
        }
        try await Task.sleep(for: .milliseconds(300)) // let the encoder drain
    }

    @Test func keepsAboutTheConfiguredDurationAndSaves() async throws {
        let replay = ReplayBuffer()
        replay.duration = 2
        replay.setFrameRate(60)
        replay.isEnabled = true
        try await feed(replay, seconds: 5)
        let buffered = replay.bufferedSeconds
        #expect(buffered >= 1.9 && buffered <= 3.1, "buffered \(buffered)")

        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).mov")
        try await replay.save(to: url)
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration).seconds
        #expect(duration >= 1.9 && duration <= 3.2, "saved \(duration)")
        #expect(try await asset.loadTracks(withMediaType: .video).count == 1)
        #expect(try await asset.loadTracks(withMediaType: .audio).count == 1)
        try? FileManager.default.removeItem(at: url)
        #expect(replay.isEnabled) // saving doesn't stop buffering
    }

    @Test func memoryCapDropsOldGOPs() async throws {
        let replay = ReplayBuffer(memoryCap: 1)
        replay.duration = 60
        replay.isEnabled = true
        try await feed(replay, seconds: 3)
        #expect(replay.bufferedSeconds <= 1.1, "only the newest GOP survives a tiny cap")
    }

    @Test func disablingClearsAndSaveFailsWhenEmpty() async throws {
        let replay = ReplayBuffer()
        replay.isEnabled = true
        try await feed(replay, seconds: 1)
        replay.isEnabled = false
        #expect(replay.bufferedBytes == 0)
        await #expect(throws: ReplayBuffer.SaveError.self) {
            try await replay.save(to: FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).mov"))
        }
    }
}
