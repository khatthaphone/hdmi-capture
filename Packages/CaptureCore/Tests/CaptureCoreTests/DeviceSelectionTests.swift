import Testing
@testable import CaptureCore

private let ugreen = CaptureDeviceInfo(
    id: "0x100000345f2131", name: "UGREEN HDMI Capture",
    modelID: "UVC Camera VendorID_13407 ProductID_8497", transport: "usb ", kind: .external
)
private let ugreenAudio = CaptureDeviceInfo(
    id: "AppleUSBAudioEngine:MACROSILICON:UGREEN HDMI Capture:20230424:3", name: "UGREEN HDMI Capture",
    modelID: "UGREEN HDMI Capture:345F:2131", transport: "usb ", kind: .external
)
private let builtInMic = CaptureDeviceInfo(id: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone", transport: "bltn", kind: .builtIn)
private let teams = CaptureDeviceInfo(id: "MSLoopback", name: "Microsoft Teams Audio", transport: "virt", kind: .other)
private let faceTime = CaptureDeviceInfo(id: "facetime", name: "FaceTime HD Camera", kind: .builtIn)

struct DeviceSelectionTests {
    @Test func parsesUSBIDsFromVideoAndAudioModelIDs() {
        #expect(DeviceSelection.usbIDs(from: ugreen.modelID) == [0x345F, 0x2131])
        #expect(DeviceSelection.usbIDs(from: ugreenAudio.modelID) == [0x345F, 0x2131])
        #expect(DeviceSelection.usbIDs(from: "FaceTime HD Camera") == nil)
    }

    @Test func pairsByUSBIDs() {
        let renamedAudio = CaptureDeviceInfo(id: "a", name: "USB Digital Audio", modelID: "USB Digital Audio:345F:2131", transport: "usb ", kind: .external)
        let other = CaptureDeviceInfo(id: "b", name: "Other Interface", modelID: "X:1234:5678", transport: "usb ", kind: .external)
        #expect(DeviceSelection.pairAudio(for: ugreen, among: [other, renamedAudio, builtInMic]) == "a")
    }

    @Test func pairsByName() {
        let noIDs = CaptureDeviceInfo(id: "v", name: "Elgato Cam Link 4K", transport: "usb ", kind: .external)
        let audio = CaptureDeviceInfo(id: "a", name: "Cam Link 4K", transport: "usb ", kind: .external)
        #expect(DeviceSelection.pairAudio(for: noIDs, among: [builtInMic, teams, audio]) == "a")
    }

    @Test func pairsSoleUSBAudioDevice() {
        let video = CaptureDeviceInfo(id: "v", name: "USB Video", transport: "usb ", kind: .external)
        let audio = CaptureDeviceInfo(id: "a", name: "USB Digital Audio", transport: "usb ", kind: .external)
        #expect(DeviceSelection.pairAudio(for: video, among: [builtInMic, audio]) == "a")
    }

    @Test func neverPairsBuiltInMicrophone() {
        #expect(DeviceSelection.pairAudio(for: ugreen, among: [builtInMic, teams]) == nil)
        #expect(DeviceSelection.pairAudio(for: faceTime, among: [builtInMic]) == nil)
    }

    @Test func picksExternalOverFallbackCamera() {
        let pick = DeviceSelection.pickVideo(available: [faceTime, ugreen], connectionOrder: ["facetime", ugreen.id], remembered: nil, current: "facetime")
        #expect(pick == ugreen.id)
    }

    @Test func keepsCurrentExternalDevice() {
        let second = CaptureDeviceInfo(id: "cam2", name: "Cam Link", kind: .external)
        let pick = DeviceSelection.pickVideo(available: [ugreen, second], connectionOrder: [ugreen.id, "cam2"], remembered: nil, current: ugreen.id)
        #expect(pick == ugreen.id)
    }

    @Test func picksRememberedThenNewest() {
        let second = CaptureDeviceInfo(id: "cam2", name: "Cam Link", kind: .external)
        #expect(DeviceSelection.pickVideo(available: [ugreen, second], connectionOrder: [ugreen.id, "cam2"], remembered: ugreen.id, current: nil) == ugreen.id)
        #expect(DeviceSelection.pickVideo(available: [ugreen, second], connectionOrder: [ugreen.id, "cam2"], remembered: nil, current: nil) == "cam2")
    }

    @Test func nothingAvailable() {
        #expect(DeviceSelection.pickVideo(available: [], connectionOrder: [], remembered: "x", current: "x") == nil)
    }
}

private final class FakeProvider: DeviceProviding {
    var video: [CaptureDeviceInfo] = []
    var audio: [CaptureDeviceInfo] = []
    var onChange: (@MainActor () -> Void)?

    func videoDevices(includeNonExternal: Bool) -> [CaptureDeviceInfo] {
        includeNonExternal ? video : video.filter { $0.kind == .external }
    }
    func audioDevices() -> [CaptureDeviceInfo] { audio }
    func startObserving(_ onChange: @escaping @MainActor () -> Void) { self.onChange = onChange }
}

@MainActor
struct DeviceCatalogTests {
    @Test func tracksHotPlugAndConnectionOrder() {
        let provider = FakeProvider()
        provider.video = [faceTime]
        let catalog = DeviceCatalog(provider: provider)
        #expect(catalog.videoDevices.isEmpty)

        provider.video = [faceTime, ugreen]
        provider.onChange?()
        #expect(catalog.videoDevices == [ugreen])
        #expect(catalog.connectionOrder == [ugreen.id])

        provider.video = [faceTime]
        provider.onChange?()
        #expect(catalog.videoDevices.isEmpty)
        #expect(catalog.connectionOrder.isEmpty)
    }

    @Test func includesFallbackCamerasWhenAsked() {
        let provider = FakeProvider()
        provider.video = [faceTime, ugreen]
        let catalog = DeviceCatalog(provider: provider, includeNonExternalCameras: true)
        #expect(catalog.videoDevices.count == 2)
        catalog.includeNonExternalCameras = false
        #expect(catalog.videoDevices == [ugreen])
    }

    @Test func notifiesOnChange() {
        let provider = FakeProvider()
        let catalog = DeviceCatalog(provider: provider)
        var calls = 0
        catalog.onChange = { calls += 1 }
        provider.onChange?()
        #expect(calls == 1)
    }
}
