import AVFoundation
import Testing
@testable import CaptureCore

struct RollingRecorderTests {
    private func videoSample(_ buffer: CVPixelBuffer, frame: Int) -> CMSampleBuffer {
        var format: CMVideoFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil, imageBuffer: buffer, formatDescriptionOut: &format)
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 30), presentationTimeStamp: CMTime(value: CMTimeValue(frame), timescale: 30), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        CMSampleBufferCreateReadyWithImageBuffer(allocator: nil, imageBuffer: buffer, formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample)
        return sample!
    }

    private func audioSample(startFrame: Int, frames: Int = 1600) -> CMSampleBuffer {
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

    /// `seconds` of 30 fps video + audio, paced so the encoders keep up.
    private func feed(_ recorder: RollingRecorder, seconds: Int) async throws {
        let frame = try #require(TestPattern.grayRamp.makePixelBuffer(width: 320, height: 180))
        for i in 0..<(seconds * 30) {
            recorder.consume(videoSample(frame, frame: i))
            recorder.consumeAudio(audioSample(startFrame: i * 1600))
            if i % 3 == 0 { try await Task.sleep(for: .milliseconds(2)) }
        }
        try await Task.sleep(for: .milliseconds(500))
    }

    private func makeRecorder() -> RollingRecorder {
        let recorder = RollingRecorder(folder: FileManager.default.temporaryDirectory.appendingPathComponent("recent-\(UUID())"), segmentSeconds: 1)
        recorder.configure(frameRate: 30, includeAudio: true)
        return recorder
    }

    @Test func savesRequestedSpanAndEverything() async throws {
        let recorder = makeRecorder()
        recorder.keepSeconds = 4
        recorder.setEnabled(true)
        try await feed(recorder, seconds: 8)

        let out = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).mov")
        try await recorder.save(last: 2, to: out)
        let last2 = try await AVURLAsset(url: out).load(.duration).seconds
        #expect(last2 >= 1.9 && last2 <= 3.1, "last 2 s → \(last2)")
        #expect(try await AVURLAsset(url: out).loadTracks(withMediaType: .audio).count == 1)

        let all = FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).mov")
        try await recorder.save(last: nil, to: all)
        let everything = try await AVURLAsset(url: all).load(.duration).seconds
        #expect(everything >= 3.5 && everything <= 6.1, "everything → \(everything)")
        #expect(recorder.isEnabled) // saving doesn't stop it
        try? FileManager.default.removeItem(at: out)
        try? FileManager.default.removeItem(at: all)
        recorder.setEnabled(false)
    }

    @Test func prunesOldSegmentsFromDisk() async throws {
        let recorder = makeRecorder()
        recorder.keepSeconds = 2
        recorder.setEnabled(true)
        try await feed(recorder, seconds: 8)
        #expect(recorder.segmentCount <= 3, "tracked \(recorder.segmentCount) segments")
        recorder.setEnabled(false)
        for _ in 0..<40 where FileManager.default.fileExists(atPath: recorder.folder.path) {
            try await Task.sleep(for: .milliseconds(100))
        }
        #expect(!FileManager.default.fileExists(atPath: recorder.folder.path))
    }

    @Test func saveWithNothingThrows() async {
        let recorder = makeRecorder()
        recorder.setEnabled(true)
        await #expect(throws: RollingRecorder.SaveError.self) {
            try await recorder.save(last: 30, to: FileManager.default.temporaryDirectory.appendingPathComponent("\(UUID()).mov"))
        }
        recorder.setEnabled(false)
    }
}
