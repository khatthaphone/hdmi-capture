import Testing
@testable import CaptureCore

private func f(_ w: Int, _ h: Int, _ cc: String, _ rates: Double...) -> FormatDescriptor {
    FormatDescriptor(width: w, height: h, fourCC: cc, frameRateRanges: rates.map { FrameRateRange(min: $0, max: $0) })
}

/// Subset of the real UGREEN HDMI Capture (MS2131) list, see docs/research/devices/.
private let ms2131: [FormatDescriptor] = [
    f(720, 480, "yuvs", 60, 30), f(720, 480, "420v", 60, 50, 30),
    f(1024, 768, "420v", 60, 30), f(1280, 720, "420v", 60, 50, 30), f(1280, 720, "yuvs", 20, 10),
    f(1280, 1024, "420v", 60, 30), f(1360, 768, "420v", 60, 30),
    f(1920, 1080, "420v", 30, 20), f(1920, 1080, "yuvs", 10, 5),
]

struct FormatSelectorTests {
    @Test func emptyListReturnsNil() {
        #expect(FormatSelector.select(from: []) == nil)
    }

    @Test func autoPrefersSmoothWidescreenOnMS2131() throws {
        let choice = try #require(FormatSelector.select(from: ms2131))
        #expect(choice.format == f(1280, 720, "420v", 60, 50, 30))
        #expect(choice.frameRate == 60)
    }

    @Test func autoPrefers1080p60Over4K30() throws {
        let formats = [f(3840, 2160, "420v", 30), f(1920, 1080, "420v", 60)]
        #expect(try #require(FormatSelector.select(from: formats)).format.width == 1920)
    }

    @Test func autoPicks4K60WhenAvailable() throws {
        let formats = [f(3840, 2160, "420v", 60), f(1920, 1080, "420v", 60)]
        #expect(try #require(FormatSelector.select(from: formats)).format.width == 3840)
    }

    @Test func autoFallsBackToHighestResolutionWhenNothingIsSmooth() throws {
        let formats = [f(1280, 720, "420v", 30), f(1920, 1080, "420v", 30)]
        #expect(try #require(FormatSelector.select(from: formats)).format.width == 1920)
    }

    @Test func uncompressedBeatsMJPEGAtSameModeAndRate() throws {
        let formats = [f(1920, 1080, "dmb1", 60), f(1920, 1080, "yuvs", 60)]
        #expect(try #require(FormatSelector.select(from: formats)).format.fourCC == "yuvs")
    }

    @Test func mjpegOnlyDongleStillGetsSmoothMode() throws {
        let formats = [f(1920, 1080, "dmb1", 60), f(1920, 1080, "yuvs", 5)]
        let choice = try #require(FormatSelector.select(from: formats))
        #expect(choice.format.fourCC == "dmb1")
        #expect(choice.frameRate == 60)
    }

    @Test func pinnedResolutionAndRate() throws {
        let choice = try #require(FormatSelector.select(from: ms2131, preference: .pinned(width: 1920, height: 1080, frameRate: 30)))
        #expect(choice.format == f(1920, 1080, "420v", 30, 20))
        #expect(choice.frameRate == 30)
    }

    @Test func pinnedUnavailableRateUsesNearest() throws {
        let choice = try #require(FormatSelector.select(from: ms2131, preference: .pinned(width: 1920, height: 1080, frameRate: 60)))
        #expect(choice.frameRate == 30)
        #expect(choice.format.fourCC == "420v")
    }

    @Test func pinnedRangeClampsToRequestedRate() throws {
        let formats = [FormatDescriptor(width: 1920, height: 1080, fourCC: "420v", frameRateRanges: [FrameRateRange(min: 1, max: 60)])]
        let choice = try #require(FormatSelector.select(from: formats, preference: .pinned(width: 1920, height: 1080, frameRate: 24)))
        #expect(choice.frameRate == 24)
    }

    @Test func pinnedUnknownResolutionFallsBackToAuto() throws {
        let choice = try #require(FormatSelector.select(from: ms2131, preference: .pinned(width: 2560, height: 1440, frameRate: nil)))
        #expect(choice.format.width == 1280)
    }

    @Test func optionsAreUniqueAndBestFirst() {
        let options = FormatSelector.options(from: ms2131)
        #expect(options.first == .pinned(width: 1920, height: 1080, frameRate: 30))
        #expect(Set(options).count == options.count)
    }

    @Test func fourCCRoundTrip() {
        #expect(FourCC.string(0x3432_3076) == "420v")
        #expect(FourCC.string(0x7573_6220) == "usb ")
    }
}
