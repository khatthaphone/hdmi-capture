# iPadOS port — research notes (2026-10-21)

Input for Phase 3 (tasks 020, 021, 049–058). Uncertain points are marked **unverified**: check them on hardware before relying on them.

## Platform support
- **Version and devices:** iPadOS 17 added `AVCaptureDevice.DeviceType.external`. It works on **USB-C iPads** with UVC devices, which includes HDMI capture cards. Apple names no M-series requirement. https://developer.apple.com/videos/play/wwdc2023/10106
- **iPhone is not supported**, USB-C iPhones included. The symbol exists on iOS 17+, but Apple DTS (March 2025): "only iPad supports external cameras." Phase 3 is iPad only; re-check each WWDC. https://developer.apple.com/forums/thread/775883
- **Dongled** itself is iPad-only (`UIDeviceFamily=[2]`, min iPadOS 18, `NSCameraUseExternalDeviceType=true`). See spec §2.
- **Info.plist:** `NSCameraUseExternalDeviceType = true`, `NSCameraUsageDescription` and `NSMicrophoneUsageDescription`. External devices go through the normal camera consent.

## Discovery and formats
- **Discovery:** `AVCaptureDevice.DiscoverySession(deviceTypes: [.external], mediaType: .video, position: .unspecified)`. `externalUnknown` is deprecated.
- **Reconnects:** a reconnected device is a **new `AVCaptureDevice` instance**. Hot-plug comes from `wasConnected`/`wasDisconnected` notifications (posted on background queues) or KVO on the discovery session's `devices`.
- **Not available on iOS:** `transportType`. This is the **only compile error** when building CaptureCore for iOS today (`AVDeviceProvider.swift:17`; checked 2026-10-21 with Xcode 26.6 / iOS SDK 26.5).
- **Pixel formats:** iPadOS converts `yuvs`/`2vuy` → `420v`, and MJPEG/H.264 → `420f`. The bi-planar 4:2:0 shader in `MetalRenderer` still applies, but it must honour **full vs video range**.
- **Limits:** some devices expose only a few formats. No maximum resolution or fps is documented (**unverified**).
- **Preview:** external cameras are mirrored by default in `AVCaptureVideoPreviewLayer`. We render ourselves, so check there's no mirroring in our path. Use `AVCaptureDeviceRotationCoordinator`, not the deprecated `videoOrientation`.
- **Debugging:** the USB-C port is taken by the card, so **use Xcode wireless debugging**.

## Audio
- **One mic at a time:** iOS has a single `.microphone` capture device. The system picks the physical input, normally the most recently connected one. https://developer.apple.com/documentation/avfoundation/avcapturedevice/devicetype-swift.struct/microphone
- **The card's input:** it should appear as an `AVAudioSession` input with port type `.usbAudio`. Select it with `setPreferredInput`. Reports say USB inputs sometimes drop out of `availableInputs` (**unverified**). https://developer.apple.com/forums/thread/786137
- **Session setup:** `.playAndRecord` + `.defaultToSpeaker` (otherwise output may go to the receiver), and a small `preferredIOBufferDuration`. Set `AVCaptureSession.automaticallyConfiguresApplicationAudioSession = false`, so capture doesn't reset our routing or fall back to the built-in mic.
- **`AVCaptureAudioDataOutput.audioSettings` is macOS-only**, already `#if`'d in `CaptureEngine`. On iOS the samples arrive in the device's native format, so `AudioPipeline.enqueue` must convert.
- **iOS 26:** `AVInputPickerInteraction`, a system input picker, is available as an optional extra.

## Lifecycle, the iPad version of the Dongled bug
- **Background:** the camera is never allowed in the background (`videoDeviceNotAvailableInBackground`). If we **don't** call `stopRunning`, the session restarts on its own when the app returns. https://developer.apple.com/documentation/avfoundation/avcapturesession/interruptionreason/videodevicenotavailableinbackground
- **Split View / Slide Over / Stage Manager:** disabled by default (`videoDeviceNotAvailableWithMultipleForegroundApps`).
  - Fix: `isMultitaskingCameraAccessEnabled = true` before `startRunning`, and **untick "Requires full screen"**.
  - `isMultitaskingCameraAccessSupported` is true on Stage Manager iPads, or with iOS 18+ linkage + `voip` background mode, or with the entitlement. The entitlement is only needed for deployment targets below 16.
  - **Don't add `voip`** to a non-VoIP app (App Review risk).
  - https://developer.apple.com/documentation/avkit/accessing-the-camera-while-multitasking-on-ipad
- **Picture-in-picture:** possible only through `AVPictureInPictureVideoCallViewController` (an `AVSampleBufferDisplayLayer`, or an `MTKView` on iOS 18+). It is designed for video calls, so it is an App Review risk. It is a stretch goal, not planned.
- **External display:** M1+ iPads give interactive `windowApplication` scenes on external displays via Stage Manager. Non-interactive external-display scenes need a registered scene accessory from iOS 27. https://developer.apple.com/documentation/uikit/presenting-content-on-a-connected-display

## Outputs
- **Encoding:** AVAssetWriter HEVC/H.264 hardware encoding is the same as macOS, so `Recorder` / `RollingRecorder` should port as they are.
- **Photos:** add-only access (`PHAccessLevel.addOnly`) needs `NSPhotoLibraryAddUsageDescription`.
- **Files:** `UIFileSharingEnabled` + `LSSupportsOpeningDocumentsInPlace` show Documents in the Files app.

## UI (iPadOS 26)
- **Menu bar:** every iPad gets one (swipe down from the top). SwiftUI `.commands`/`CommandGroup` fill it, so most of `HDMICaptainApp.swift`'s commands carry over. Apple asks that every command appear in it, with unavailable ones shown disabled. https://developer.apple.com/videos/play/wwdc2025/243/
- **Windows:** freely resizable, so layouts must adapt. The control bar's `ViewThatFits` layouts already do.

## Development without the paid account
- **Free personal team:** 7-day profiles, 10 App IDs per 7 days, 3 apps per device, no TestFlight.
- **StoreKit:** local `.storekit` testing should work (**unverified** on a personal team). The sandbox needs the paid agreement.
- **Distribution** is blocked on the Developer account, like 013/045.

## Competitors
- **Orion (Lux):** free, USB-C iPad, iPadOS 17+, ~$5 Pro (AI upscaling, picture adjustments, CRT mode). Keeps a list of compatible cards. https://www.lux.camera/use-your-ipad-as-an-hdmi-monitor-with-orion
- **Others:** Genki Studio, UVC Camera, UVC Capture.
- **Opportunities for us:** none of them offer scopes, a replay buffer or console profiles.
