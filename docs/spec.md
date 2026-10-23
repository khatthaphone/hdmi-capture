# HDMI Captain — Product & Technical Spec

_Status: draft v0.5 · 2026-10-23 (Mac + iPad in code; Android in progress, §12; Windows in progress, §13; Linux plan added, §14)_

## 1. Problem

HDMI-to-USB capture dongles (UVC video + UAC audio) let a Mac/iPad act as a monitor for a console, camera, or second computer. The app we use today, **Dongled** (`com.oddforms.dongled` v1.5), works well on iPad but on Mac **drops the capture whenever its window loses focus or the user switches Spaces / virtual desktops**. HDMI Captain is a clean-room, native replacement that never disconnects on Mac, then grows into a "mini-OBS" for live streaming and ships on iPadOS, Android and Windows.

## 2. Findings from Dongled (read-only bundle inspection)

| Observation | Evidence | Implication |
|---|---|---|
| It is the **iPad app running on Apple Silicon**, not a native Mac app | `/Applications/Dongled.app/Wrapper/Dongled.app`, `LSRequiresIPhoneOS=true`, `UIDeviceFamily=[2]`, `MacFamily20,1` in `UISupportedDevices` | Runs under iOS lifecycle rules on Mac |
| UIKit + `SceneDelegate`, min iPadOS 18, built with Xcode 26.6 | `Info.plist` | Modern, small Swift app |
| Uses the iPadOS 17+ **external (UVC) camera** API | `NSCameraUseExternalDeviceType=true`; links AVFoundation, AVFAudio, CoreMedia, Metal, QuartzCore | All capture is standard AVFoundation — easy to reproduce clean-room |
| Feature surface is small | Localizable strings: video input picker, audio input picker, "Scanning for Hardware", "Silent Mode", "Connecting to Device", camera/mic-permission messages | MVP scope is clear |
| Binary is **FairPlay-encrypted** | `LC_ENCRYPTION_INFO_64 cryptid 1` | **We do not decrypt or decompile it.** Clone behaviour only (clean-room). |

**Root-cause hypothesis for the Mac disconnect:** under the iOS app lifecycle, when the window loses focus or moves off-Space, the scene enters the background and the system interrupts `AVCaptureSession` (`AVCaptureSessionInterruptionReason.videoDeviceNotAvailableInBackground`). A **native macOS** `AVCaptureSession` has no such rule, so a native Mac build fixes the bug by design. (Verified in task 001.)

## 3. Goals / non-goals

**Goals (v1, macOS):**
- G1. Plug in a dongle → live picture + audio in ≤ 1 s, no configuration.
- G2. **Never disconnects** on focus loss, Space switch, minimize, other-app full-screen, or screen lock.
- G3. Auto-reconnect on unplug/replug and on capture runtime errors.
- G4. Glass-to-glass latency ≤ Dongled's on the same hardware.
- G5. Pleasant viewer UX: aspect-locked window, full screen, float on top, all Spaces, clean borderless mode.

**Non-goals (v1):** streaming, scenes/compositing, recording (phase 2), non-Apple platforms, decrypting/copying Dongled's code or assets.

## 4. Platform & technology decision

**Swift, native per platform. No Kotlin Multiplatform.**

- The core work — capture, GPU rendering, encoding, virtual camera — is platform-native everywhere (AVFoundation/Metal/VideoToolbox/CoreMediaIO; Camera2/USB-host/MediaCodec; Media Foundation/D3D11). KMP would only wrap these via `expect/actual`; Compose Multiplatform on Windows is JVM-based, a poor fit for low-latency video.
- One Swift codebase serves **macOS + iPadOS (+ visionOS later)** via a shared Swift package (~80 % shared). iPhone is out: Apple supports external (UVC) cameras on iPad only (`docs/research/ipados.md`).
- **Android:** Kotlin + Jetpack Compose shell over a C++ (NDK) media core: libusb + libuvc capture, libjpeg-turbo MJPEG decode, GLES 3 render, Oboe audio (§12, `docs/research/android.md`). Pure logic is ported to Kotlin, not shared; shared JSON test fixtures keep Swift and Kotlin behaviour in step. **Windows:** C# (.NET 10) + WinUI (Windows App SDK 2.x) shell; engine in C# over Media Foundation (Source Reader), D3D11 and WASAPI, libjpeg-turbo for MJPEG; pure logic ported to C# against the same fixtures (§13, `docs/research/windows.md`). A Rust core + Win32 shell was considered and rejected (2026-10-22): far more UI work, and it would share nothing with Android's C++ engine. **Linux (proposed, ADR 0006):** C# (.NET 10) + GTK4/libadwaita via Gir.Core, reusing `HDMICaptain.Core` as is and Android's C++ GLES 3 renderer; V4L2 capture, libjpeg-turbo, PulseAudio API audio (§14, `docs/research/linux.md`).
- Cross-platform sharing is **specs as data**: scene/layout JSON schema, settings keys, stream presets (in `docs/`). If shared logic later becomes substantial, extract a **Rust core with UniFFI** bindings (Swift/Kotlin/C#).
- Licensing: avoid GPL dependencies (e.g. libobs) unless we decide to ship GPL. Preferred: HaishinKit (BSD), RootEncoder (Apache-2.0), FFmpeg LGPL builds.

## 5. macOS v1 requirements

### 5.1 Device discovery
- `AVCaptureDevice.DiscoverySession` with `.external` (video) and `.microphone` (audio).
- Observe `AVCaptureDevice.wasConnectedNotification` / `wasDisconnectedNotification`.
- Auto-select: last-used `uniqueID` if present, else most recently connected external video device.
- Pair audio by matching `modelID` / name / transport; user can override.
- UI states: *Permission needed* · *Scanning for hardware* · *Connecting* · *Live* · *Signal lost*.

### 5.2 Capture
- `AVCaptureSession` on a dedicated serial queue; `AVCaptureVideoDataOutput` with `alwaysDiscardsLateVideoFrames = true`, pixel format `420v` (fallback to device native).
- `FormatSelector` (pure, unit-tested): default = native resolution at highest fps (prefer 1080p60, then 4K30, etc.); user can pin resolution/fps.

### 5.3 Always-on behaviour (G2/G3)
- `ProcessInfo.beginActivity([.userInitiated, .latencyCritical, .idleDisplaySleepDisabled])` while live.
- Handle `AVCaptureSession.runtimeErrorNotification`, `wasInterruptedNotification`, `interruptionEndedNotification`: restart with exponential backoff (0.25 s → 4 s cap).
- Re-attach automatically when a device with the remembered `uniqueID` reappears.
- When the window is occluded, pause rendering but **keep the session running**.

### 5.4 Rendering
- `CVMetalTextureCache` → `MTKView` (Metal from day one; required by phase-4 compositor).
- Scale modes: fit · fill · stretch · integer. Filter: linear · nearest. Color range: auto · limited · full (YCbCr→RGB shader, BT.709/BT.601).
- Optional stats overlay: input res/fps, render fps, dropped frames.

### 5.5 Audio
- v1: `AVCaptureAudioPreviewOutput` with volume + mute ("Silent Mode").
- Later: `AVAudioEngine` tap for meters/mixer.

### 5.6 Window & UX
- Aspect-locked resize (`contentAspectRatio`), native full screen, float on top, show on all Spaces (`.canJoinAllSpaces`), cursor auto-hide, borderless "clean" mode, double-click toggles full screen.
- Shortcuts: ⌘F full screen · ⌘M mute · ⌘1/2/3/4 scale modes · ⌘T float on top · ⌘, settings.
- Settings: video device, audio device, format, scale/filter/color, per-device presets (persisted by `uniqueID`).

### 5.7 Packaging
- macOS 14+, Apple Silicon + Intel (universal).
- App Sandbox, `com.apple.security.device.camera`, `com.apple.security.device.audio-input`; `NSCameraUsageDescription`, `NSMicrophoneUsageDescription`.
- Distribution: Developer ID + notarization (DMG) first; Mac App Store later. Sparkle for updates (phase 2).

## 6. Architecture

```
project.yml                      XcodeGen project definition
Packages/CaptureCore/            Swift package shared by macOS + iPadOS
  Sources/CaptureCore/
    DeviceCatalog.swift          discovery, hot-plug, audio pairing
    FormatSelector.swift         pure format choice
    CaptureEngine.swift          session lifecycle, recovery, frame stream
    MetalRenderer.swift          pixel buffer → texture → drawable
    Shaders.metal                YCbCr→RGB, scaling
    AudioMonitor.swift           passthrough, volume/mute
    Presets.swift                per-device settings persistence
  Tests/CaptureCoreTests/
Apps/macOS/                      SwiftUI app shell, windowing, menus, entitlements
Apps/Shared/                     app-layer code shared by macOS + iPadOS (phase 3)
Apps/iPadOS/                     iPad shell (phase 3)
Apps/Android/                    Gradle project (phase 5, §12.9)
Apps/Windows/                    .NET solution (phase 6, §13.9)
docs/spec.md                     this file
docs/tasks/                      task plan
docs/scene-schema.json           (phase 4) cross-platform scene contract
tools/                           device-inventory CLI, notarize script
```

Data flow: `DeviceCatalog` → selected device → `CaptureEngine` (session queue) → `CMSampleBuffer` → `MetalRenderer` (display link) → `MTKView`; audio → `AudioMonitor`. Engine publishes a `CaptureState` enum to the UI (Observation framework).

## 7. Roadmap

| Phase | Scope |
|---|---|
| 0 | Confirm root cause, device inventory, project scaffold |
| 1 | macOS MVP viewer (§5) |
| 2 | Snapshot, recording (AVAssetWriter, HEVC/H.264), latency HUD, multi-device windows, Sparkle |
| 3 | iPadOS target (USB-C iPads, iPadOS 17+) reusing CaptureCore + `Apps/Shared`. **Shipped in code (2026-10-21):** live 1080p60 (420f) at ~30 ms on screen, HDMI audio only via the card's USB route, Paused screen + instant resume for OS interruptions (multitasking camera access where supported; not on A12Z), Settings sheet, iPadOS menu bar, screenshots/recordings/Save Recent to Files + Photos, external-display clean feed (untested), 30-min soak passed (`docs/research/devices/ipad-pro-11-2g.md`). Tasks 020–021, 049–058 |
| 4 | Mini-OBS: scene schema, Metal compositor (HDMI, webcam, ScreenCaptureKit, image, text, browser, chroma key), audio mixer, RTMP/SRT via HaishinKit, CoreMediaIO **virtual camera** |
| 5 | Android (§12): Kotlin/Compose shell, native libusb + libuvc capture (Camera2 external where available), libjpeg-turbo, GLES 3, Oboe UAC audio, Play Billing Pro, MediaCodec recording. Spikes 069–076 → ADR 0002 (027); MVP 077–085 (028). Streaming stays hidden, as on Apple |
| 6 | Windows (§13): C# .NET 10 + WinUI, MF Source Reader capture, libjpeg-turbo, D3D11 flip-model render, WASAPI audio, Store MSIX + `StoreContext` Pro, MF Sink Writer recording. Spikes 086–092 → ADR 0003 (029); MVP 093–103 (030). Streaming and virtual camera hidden, as on Android |
| 7 | Free viewer features from competitor research (§11) |
| 10 | Linux (§14): C# .NET 10 + GTK4/libadwaita (Gir.Core) reusing `HDMICaptain.Core`, raw V4L2 capture, libjpeg-turbo, shared GLES 3 renderer in a `GtkGLArea`, PulseAudio-API audio, licence-key Pro, FFmpeg recording, Flatpak + .deb. Developed in WSL2/WSLg on the Windows dev PC (card via usbipd-win), signed off on a real Linux install. Spikes 105–111 → ADR 0006 (112); MVP 113–124 (125). Streaming and virtual camera hidden, as on Windows |
| 8 | Pro unlock (StoreKit 2), Pro features, Mac App Store (§10) |

## 8. Acceptance tests (macOS v1)

1. Live picture + audio within 1 s of plug-in.
2. Click another app / switch Spaces / minimize / another app full-screen / lock screen 5 min → capture still live on return; no session interruption in logs.
3. Unplug + replug → auto-reconnect without user action.
4. 1 h soak at 1080p60: dropped frames not growing, memory flat (Instruments).
5. Latency: slow-motion video of source timer vs. preview; ≤ Dongled on same hardware.

## 9. Open questions

- App name/bundle ID final (`com.<you>.hdmicaptain` placeholder).
- Apple Developer account availability for signing/notarization.
- ~~Monetization~~ → decided, see §10.
- Direct (non-App-Store) sales of Pro: possible later through a license-key provider implementing the same `EntitlementProviding` protocol.

## 10. Business model

**Free viewer + one-time "HDMI Captain Pro" unlock** (non-consumable in-app purchase, no subscription). Subscriptions are the most common complaint in competitor reviews (`docs/research/competitors.md`).

| Free (forever, no nags, no time limits) | Pro (one-time) |
|---|---|
| Everything a viewer needs: capture, audio, all scaling and color options, signal status, window sizes, menu bar, multi-view, latency test, image adjustments, profiles, test patterns, guides, scopes, LUTs, screenshots | Recording · Instant replay buffer · Streaming (RTMP/SRT) · Virtual camera |

- Gating lives in one place: `ProFeature` + `EntitlementStore` (task 042). Free features never check entitlements.
- StoreKit 2 requires Mac App Store distribution. Developer ID builds are possible later with a license-key provider.
- Local development uses a `.storekit` configuration file, so no App Store Connect account is needed until submission (task 045).

## 11. Feature expansion (from competitor research)

Priority order. Details and acceptance criteria are in `docs/tasks/031`–`048`.

1. Audio pipeline: delay (0–500 ms), L/R swap, mono fix, output routing, meters (031)
2. Signal status: no-signal/HDCP detection, hold last frame, requested vs actual mode (032)
3. Screenshot (free, 015) and recording (Pro, 016)
4. Window size presets and menu-bar extra (033)
5. Multi-view: window per card (018) and grid (034)
6. Built-in latency test using the Mac's own HDMI output (035)
7. Image adjustments and presets (036)
8. Console profiles (037)
9. Virtual camera (Pro, 026), replay buffer (Pro, 038), direct streaming (Pro, 046)
10. Monitoring: test patterns and guides (039), scopes (040), LUTs (041)
11. Hover control bar near the bottom edge: input/profiles, volume, screenshot, Save Recent, Go Live, Record, view options (047; custom glyphs 048). It is a window overlay only and never part of recordings or streams.

## 12. Android v1 (phase 5)

Research: `docs/research/android.md`. The stack is confirmed or changed by the spike results in ADR 0002 (task 027). Product scope = the iPad app: free viewer + Pro recording/Save Recent; streaming and virtual camera hidden.

### 12.1 Devices & OS
- Phones, tablets, foldables with a USB-C port in host mode; Chromebooks and Samsung DeX / Android desktop windowing (pointer + keyboard). Android TV later (keep every control D-pad focusable).
- minSdk 29, targetSdk/compileSdk 36 (Play floor 2026), arm64-v8a + x86_64 (emulator/ChromeOS). Native libraries 16 KB page aligned.

### 12.2 Discovery & permissions
- `UsbManager` device list + `ACTION_USB_DEVICE_ATTACHED/DETACHED`; manifest attach filter for UVC (class 14) so the user can choose "always open with HDMI Captain".
- Order: explain (first-run card, as on Apple) → `CAMERA` + `RECORD_AUDIO` runtime permissions → USB permission (`FLAG_MUTABLE` explicit PendingIntent, `RECEIVER_NOT_EXPORTED`).
- Detect the global Camera/Microphone privacy toggles (`SensorPrivacyManager`) and say so instead of showing black.
- Auto-select: remembered device (stable key VID:PID + serial, else + product name) else most recently attached. Pair the UAC `AudioDeviceInfo` (`TYPE_USB_DEVICE`) by product name/address.

### 12.3 Capture
- **Primary:** native C++ `UvcSource` — libusb (wrapping the `UsbDeviceConnection` fd) + libuvc, built from source. Claims only the UVC interfaces; never detaches the audio driver.
- **Secondary:** `Camera2Source` when Camera2 lists a `LENS_FACING_EXTERNAL` camera (lower fps; labelled in Settings).
- **Dev/CI:** `TestPatternSource` (SMPTE bars, full bars, grid, gray ramp) behind the same `CaptureSource` interface.
- `FormatSelector` (Kotlin port) also weighs **USB link speed**: on USB 2.0 prefer MJPEG for ≥ 720p60; prefer uncompressed (NV12/YUY2) when bandwidth allows. User can pin a format.
- Frames live in a native buffer ring; no JNI and no Kotlin allocation per frame.

### 12.4 Decode & render
- MJPEG: libjpeg-turbo to planar YCbCr (2 threads for ≥ 1080p60); optional vendor MediaCodec MJPEG decoder if it proves faster.
- Render: SurfaceView (Compose `AndroidExternalSurface`, opaque) → EGL/GLES 3 shader with the same colour maths and options as `MetalRenderer` (fit/fill/stretch/integer, linear/nearest, auto/limited/full, BT.601/709, adjustments, `.cube` LUT, guides). Draw on arrival, ≤ 1 frame in flight; `Surface.setFrameRate` to the source rate.

### 12.5 Always-on & lifecycle
- Never tear down capture for rotation, resize, fold, multi-window, DeX window changes or loss of focus: the activity handles `configChanges` itself; capture is tied to STARTED/STOPPED (visible), not RESUMED.
- Keeps running in PiP and split screen. Stops when not visible (Android forbids background camera without a foreground service); resumes instantly on return, like the iPad Paused screen.
- Same recovery as Apple: unplug/replug, stream errors and camera-priority loss → `recovering` with backoff 0.25 s → 4 s, auto re-attach of the remembered device.
- Keep screen on while live. Thermal status listener steps down (scopes off, then a lower format) before the OS throttles.

### 12.6 Audio
- Oboe full duplex, low-latency mode, input pinned to the card's USB device; output follows the system route. Volume, mute, delay 0–500 ms, L/R swap, mono fix, meters (same features as §11 #1). MS2109 48 kHz stereo quirk handled.

### 12.7 UI
- Kotlin + Jetpack Compose + Material 3, dark theme around the video, edge-to-edge + immersive. Same structure and copy as the Apple apps (`docs/copy.md`): status cards, connect illustration, no-signal/HDCP help, Connection Help, control bar (tap toggles with 3 s auto-hide; hover near the bottom edge with a pointer), keyboard shortcuts as on Mac, right-click menu, predictive back, PiP, adaptive Settings (list-detail on large screens).
- Android-only states: USB permission needed, privacy toggle on, card on a USB 2.0 port (format limited), card needs more power.
- Overlays are Compose views above the SurfaceView and never reach screenshots or recordings (§6 rule).

### 12.8 Outputs & Pro
- Screenshot (free): read the rendered frame from the GL framebuffer → PNG in MediaStore `Pictures/HDMI Captain`.
- External display (free, like the iPad): a `Presentation` on any presentation display (USB-C DisplayPort, HDMI adapter, Miracast) shows the clean picture full screen; the phone keeps the controls. The renderer draws it as a second window without vsync so the phone never waits on it.
- Recording and Save Recent (Pro): MediaCodec HEVC/H.264 from an encoder input surface fed the same frames + AAC → MP4 in `Movies/HDMI Captain`; Save Recent keeps an encoded ring like `RollingRecorder`.
- Pro = one Play Billing one-time product (non-consumable: acknowledged, never consumed); purchases re-queried on launch; one gate equivalent to `EntitlementStore.gate`; free features never check it.

### 12.9 Code layout
```
Apps/Android/            Gradle (AGP 9, Kotlin 2.4, version catalog)
  app/                   Activity, manifest (USB filter, PiP), wiring
  feature/viewer/        Compose viewer, ViewerViewModel, control bar, status screens
  feature/settings/      Settings, device/format pickers, upgrade sheet
  core/model/            pure Kotlin (JVM): FormatSelector, CaptureState + RecoveryMachine, SignalAnalyzer, presets, LUT parser
  core/capture/          CaptureSource API, UsbDeviceCatalog, Camera2Source; NDK: UvcSource (libusb+libuvc), TestPatternSource
  core/render/           NDK: EGL/GLES 3 renderer, MJPEG decode (libjpeg-turbo)
  core/audio/            NDK: Oboe pipeline
  core/billing/          Play Billing → StateFlow<ProStatus>
  benchmark/, baselineprofile/
docs/fixtures/           shared JSON test vectors read by Swift and Kotlin tests
```
- `make android-build | android-test | android-run` (wireless adb; the card holds the USB-C port). DEBUG intent extras mirror the Apple QA arguments (`testPattern`, `qaAction`, `qaShowStats`, `qaSoakMinutes`, `debugUnlockPro`).

### 12.10 Performance
- Targets: 1080p60 shown at 60 fps without drops on a mid-range 2023+ phone; glass-to-glass ≤ 80 ms (MJPEG) and ≤ 60 ms (uncompressed); 30-min soak with flat memory and audio buffer.
- Means: native frame path, `URGENT_DISPLAY` threads, ADPF performance-hint session, baseline profile, R8, Perfetto trace sections (capture/decode/render/present).

### 12.11 Acceptance tests (Android v1, on ≥ 2 devices)
1. Plug in a card → picture + sound within 2 s on second and later plug-ins (first plug-in includes permission prompts).
2. Rotate, resize, fold, split screen, PiP, DeX window moves → no capture restart in logs.
3. Unplug/replug → auto-reconnect; app in background → resumes on return without user action.
4. 30-min soak at the best format: no growing drops, memory flat, audio buffer flat, thermal logged.
5. Latency measured with a 240 fps camera and recorded in `docs/research/devices/`.
6. Recording + Save Recent play back with A/V in sync; screenshots and recordings contain no UI.

## 13. Windows v1 (phase 6)

Research: `docs/research/windows.md`. ADR 0003 (task 029) confirms the stack or changes it, based on the spike results. Product scope is the same as Android: a free viewer plus Pro recording and Save Recent. Streaming and the virtual camera stay hidden.

### 13.1 Devices & OS
- Windows 11 22H2+ on x64 and ARM64 is supported and tested. Windows 10 22H2 should run (Windows App SDK supports 1809+) but isn't tested. Windows 11-only APIs (virtual camera, DXVA MJPEG on 24H2) are feature-detected.
- Desktops and laptops, including hybrid-GPU machines. Mouse, keyboard, pen and touch. Several monitors with mixed DPI and refresh rates.

### 13.2 Discovery & permissions
- Video: `MFEnumDeviceSources` (VIDCAP). Hot-plug: `CM_Register_Notification` on `KSCATEGORY_VIDEO_CAMERA`, matching symbolic links case-insensitively.
- Audio: pair each card's video interface with its WASAPI capture endpoint by **USB ContainerId**; the user can override the pairing. Listen for endpoint changes with `IMMNotificationClient`.
- Auto-select the remembered device if present, otherwise the most recently attached one.
  - Stable key: VID:PID + serial, or VID:PID + name when there is no serial. The symbolic link changes from port to port.
- Privacy:
  - `E_ACCESSDENIED` on the camera → status card with a button to `ms-settings:privacy-webcam`.
  - Microphone denied → the picture still works and an audio badge links to `ms-settings:privacy-microphone`. Never fail capture because audio is missing.
- Camera in use by another app (0xC00D3EA3 / 0xC00D3704) → an "In use" state that keeps retrying with backoff.
- Windows 11 "Allow multiple apps to use camera" → show the format we actually got (requested vs actual).

### 13.3 Capture
- `IMFSourceReader` in async mode with one `ReadSample` outstanding, `MF_LOW_LATENCY`, and `MF_READWRITE_DISABLE_CONVERTERS`. Video processing stays off, so MF never inserts a converter.
- `FormatSelector` (C# port, same fixtures) picks from the native types. It prefers uncompressed (NV12/YUY2) when the type exists at the target frame rate, and applies the USB link-speed rule when the hub reports the speed. The user can pin a format.
- The capture/decode thread runs under MMCSS "Capture". No allocation per frame: pooled native buffers.
- Dev/CI: `TestPatternSource` (SMPTE bars, full bars, grid, gray ramp) behind the same `ICaptureSource` interface. Optional: our own virtual camera as a test device (task 104).

### 13.4 Decode & render
- MJPEG: libjpeg-turbo (native DLL, P/Invoke) to planar YCbCr, with 2 threads at ≥ 1080p60; the newest frame wins. Optional fast paths, used only when a probe succeeds: DXVA MJPEG (Windows 11 24H2+) or the MF MJPEG MFT.
- Render: D3D11 on the adapter that drives the window's monitor; the device is recreated when the window moves to another adapter. Our own HLSL shader uses the same maths and options as `MetalRenderer`: fit/fill/stretch/integer, linear/nearest, auto/limited/full, BT.601/709, adjustments, `.cube` LUT, guides.
  - Two passes, like Android: convert at source size, then scale. The pass-1 texture is the clean source for screenshots and recordings.
- Present: flip-model swap chain (`FLIP_DISCARD`, waitable object, `SetMaximumFrameLatency(1)`, `ALLOW_TEARING`), presenting as each frame arrives. Full screen is borderless.
- Start in a WinUI `SwapChainPanel`. If PresentMon shows it never reaches independent flip in full screen (092), viewer windows switch to an HWND swap chain with the control bar in a separate popup (ADR 0003).
- Multi-View composes every tile into **one** swap chain.

### 13.5 Always-on & lifecycle
- Never stop capture for focus loss, minimize, occlusion, virtual-desktop switches, DPI or monitor changes, or the lock screen. Pause presenting when hidden; capture and audio keep running (§5.3 rule).
- While live:
  - a `PowerRequestDisplayRequired` power request with a reason string;
  - EcoQoS opt-out (`SetProcessInformation` ProcessPowerThrottling, execution speed + timer resolution);
  - audio threads under MMCSS "Pro Audio".
- Recovery is the same as on Apple. These events → `recovering` with backoff 0.25 s → 4 s and automatic re-attach of the remembered device:
  - unplug/replug;
  - reader errors;
  - "in use" by another app;
  - sleep/resume (`RegisterSuspendResumeNotification`).
- Frames are pushed by MF callbacks, audio by WASAPI events, and presents are paced by the swap chain. There are no timers in the frame path.

### 13.6 Audio
- WASAPI shared mode, event-driven, on both sides; `IAudioClient3` small periods where the engine allows them. The output follows the default device unless the user picks one.
- A ring buffer plus drift controller (adaptive resample) keeps latency flat over hours.
- The same features as §11 #1: volume, mute, delay 0–500 ms, L/R swap, mono fix, meters.
- MS2109 quirk: when the card reports 96 kHz mono 16-bit, reinterpret the stream as 48 kHz stereo.

### 13.7 UI
- C# + WinUI with Mica and a dark theme around the video. Same structure and copy as the Apple apps (`docs/copy.md`): status cards, connect illustration, no-signal/HDCP help, Connection Help, hover control bar, right-click menu.
- Window behaviour:
  - aspect-locked resize (`WM_SIZING`);
  - borderless clean mode;
  - always on top;
  - double-click toggles full screen;
  - the cursor hides after 3 s;
  - window size presets.
- Shortcuts: F11 / Alt+Enter full screen · Ctrl+M mute · Ctrl+1/2/3/4 scale modes · Ctrl+T always on top · Ctrl+, settings · Ctrl+Shift+S screenshot.
- A notification-area (tray) icon stands in for the Mac menu-bar extra.
- Settings window: video device, audio device, format, scale/filter/colour, image adjustments, profiles, per-device presets. Prefs are JSON in LocalAppData and use the same keys as `ViewerPrefs`.
- Accessibility (UI Automation names for every control, keyboard navigation, high contrast) and per-monitor DPI v2.
- Overlays are XAML above the video and never reach screenshots, recordings or Save Recent (§6 rule).

### 13.8 Outputs & Pro
- Screenshot (free): WIC PNG from the pass-1 texture → `Pictures\HDMI Captain`, plus a copy to the clipboard.
- Recording and Save Recent (Pro):
  - MF Sink Writer with a hardware encoder MFT (NVENC/AMF/QSV) for H.264/HEVC, falling back to software H.264, plus AAC → fragmented MP4 in `Videos\HDMI Captain`.
  - Save Recent keeps an encoded ring trimmed at keyframes and remuxes it on save, like `RollingRecorder`.
- Pro is one Microsoft Store **durable add-on** (`StoreContext`, `InitializeWithWindow`).
  - The licence is re-checked on launch and cached for offline use.
  - One gate, the equivalent of `EntitlementStore.gate`; free features never check it.
  - DEBUG builds use a fake store (`-debugUnlockPro YES`).

### 13.9 Code layout
```
Apps/Windows/                    .NET 10 solution (HDMICaptain.Windows.slnx), Directory.Packages.props
  src/HDMICaptain.Core/          net10.0, pure: FormatSelector, RecoveryMachine, SignalAnalyzer/Monitor, RenderGeometry, presets/profiles, CubeLUT, ProFeature
  src/HDMICaptain.Media/         net10.0-windows: DeviceCatalog, CaptureController, MfSource, TestPatternSource, MjpegDecoder (libjpeg-turbo), D3DRenderer + HLSL, AudioPipeline (WASAPI), Recorder/RollingRecorder, LatencyProbe
  src/HDMICaptain.App/           WinUI app (MSIX): AppModel/ViewerModel, windows, control bar, settings, tray, ProStore, .resw strings
  native/                        vcpkg manifest (libjpeg-turbo); later VirtualCamera (C++ COM media source)
  tests/HDMICaptain.Core.Tests/  xUnit, reads docs/fixtures/
  tests/HDMICaptain.Media.Tests/ renderer on WARP, decoder and audio DSP tests
```
- Interop: CsWin32 (Win32, MF, WASAPI, power) and Vortice.Windows (D3D11/DXGI/D3DCompiler).
- `make win-build | win-test | win-run WIN_ARGS="-testPattern smpteBars" | win-package`. DEBUG builds accept the Apple QA arguments (`-testPattern`, `-qaAction`, `-qaShowStats`, `-qaPinControls`, `-qaSoakMinutes`, `-debugUnlockPro`).

### 13.10 Performance
- Targets:
  - 1080p60 shown at 60 fps with no drops (MJPEG and uncompressed);
  - 4K30 on the RTX/Ryzen dev PC;
  - glass-to-glass no worse than the Mac app on the same card, and ≤ 1 frame above OBS preview;
  - a 30-min soak with flat memory and a flat audio buffer.
- Means:
  - a native-memory frame path;
  - no GC allocation per frame (`SustainedLowLatency` while live);
  - MMCSS;
  - the display's adapter;
  - independent flip in full screen;
  - ReadyToRun (Native AOT if clean);
  - PresentMon and ETW traces for the capture, decode, render and present stages.

### 13.11 Acceptance tests (Windows v1, on the dev PC + one laptop if available)
1. Plug in a card → picture and sound within 1 s on later plug-ins (the first plug-in may include privacy prompts).
2. Minimize, occlude, switch virtual desktops, move between monitors (DPI and adapter change), lock 5 min → no capture restart in the logs.
3. Unplug/replug, sleep/resume, another app taking the camera and then releasing it → recovers without user action.
4. 30-min soak at 1080p60: no growing drops, memory and audio buffer flat.
5. Latency measured with the latency probe and a 240 fps camera, windowed and full screen, with the PresentMon present mode recorded in `docs/research/devices/`.
6. Recording and Save Recent play back with A/V in sync. Screenshots and recordings contain no UI.
7. Camera privacy off → the right status card. Microphone off → picture with an audio badge.

## 14. Linux v1 (phase 10)

Research: `docs/research/linux.md`. ADR 0006 (task 112) confirms the stack, or changes it, based on spikes 105–111. Product scope is the same as Windows: a free viewer plus Pro recording and Save Recent. Streaming and the virtual camera stay hidden.

### 14.1 Devices & OS
- Ubuntu 24.04 LTS and Fedora (current), GNOME on Wayland, are supported and tested. KDE Plasma and X11 sessions should work but are not part of sign-off. x64 first; arm64 builds once the pipeline is up.
- Development and CI run in **WSL2 + WSLg** (Ubuntu 24.04) on the Windows dev PC. Latency, the soak and desktop integration are signed off on a real install (§14.11).

### 14.2 Discovery & permissions
- Video: enumerate `/dev/video*` nodes with `V4L2_CAP_VIDEO_CAPTURE`, skipping metadata nodes. The sysfs parent gives the USB VID:PID, serial and link speed.
- Stable device key: VID:PID + serial, or VID:PID + name when there is no serial, with the USB path as the tiebreaker. Never the `/dev/videoN` number.
- Hot-plug: a libudev monitor (`video4linux`, `sound`) on the host. Inside Flatpak, inotify on `/dev` plus a sysfs rescan, because the netlink monitor is unreliable in the sandbox.
- Audio pairing: the card's ALSA card shares a USB parent with the V4L2 node. Map that card to its PulseAudio/PipeWire source, falling back to ALSA `hw:` directly when no sound server owns it (WSL). The user can override the pairing.
- Access: `/dev/video*` needs the `video` group (the norm on desktop distros) or a uaccess seat. Flatpak needs `--device=all`. Permission denied → a status card that explains the fix. The PipeWire camera portal is a later second backend.
- `EBUSY` (another app streams the card) → "In use", retrying with backoff.

### 14.3 Capture
- Raw V4L2: `S_FMT` + `S_PARM` on the native type, ≥ 4 mmap buffers, and `poll` on a dedicated thread. Dequeue everything that's ready and keep the newest. Drop buffers with `bytesused == 0` or `V4L2_BUF_FLAG_ERROR`.
- `FormatSelector` (the shared C# Core, same fixtures) picks from the enumerated types. The USB link-speed rule uses the sysfs `speed`. USB 2 cards do 1080p30/60 as MJPEG.
- No allocation per frame: the mmap buffers feed decode and upload directly.
- Dev/CI: `TestPatternSource` behind the same capture interface (`-testPattern`).

### 14.4 Decode & render
- MJPEG: system libjpeg-turbo (`tj3DecompressToYUVPlanes8`) on the capture thread into reused planes; the newest frame wins. VA-API JPEG decode is optional, behind a probe.
- Render: the Android GLES 3 renderer, moved to a shared C++ library (`native/render-gles/`), drawing into a `GtkGLArea` with GLES allowed. Same two passes and options as Android/Windows: fit/fill/stretch/integer, linear/nearest, auto/limited/full, BT.601/709, adjustments, `.cube` LUT, guides. The pass-1 texture is the clean source for screenshots and recordings.
- Present when a frame arrives (`gtk_gl_area_queue_render` from the frame callback, marshalled to the GTK main context). A later step is `GtkGraphicsOffload` with a dmabuf texture, for direct scanout in full screen (§14.10).
- WSLg uses Mesa d3d12, with `GSK_RENDERER` pinned to a GL renderer if `ngl` misrenders (task 105).

### 14.5 Always-on & lifecycle
- Never stop capture for focus loss, minimize, workspace switches, occlusion, scale or monitor changes, or the lock screen. Stop presenting while hidden; capture and audio keep running (§5.3 rule).
- While live: inhibit idle (`gtk_application_inhibit` / Inhibit portal). While recording, also inhibit suspend. Capture and audio threads ask rtkit (or the Realtime portal) for real-time priority and fall back quietly.
- Recovery is the same as everywhere else (`RecoveryMachine`, backoff 0.25 s → 4 s, re-attach by device key) for:
  - unplug/replug;
  - `ENODEV`/`POLLERR`;
  - `EBUSY`;
  - sleep/resume (logind `PrepareForSleep` with a delay inhibitor);
  - usbipd detach/attach in WSL.
- USB autosuspend: the .deb/.rpm installs a udev rule (`power/control=on` for known capture cards). Flatpak builds detect `auto` and show advice.

### 14.6 Audio
- PulseAudio API (`libpulse`, through pipewire-pulse on modern desktops) for playback and for capture where a sound server owns the card. Use ALSA `hw:` capture directly otherwise. Spike 111 measures both. Native `pw_stream` is the upgrade if Pulse can't hold ~20 ms.
- The ring buffer, drift controller, resampler, channel modes, delay 0–500 ms, meters and the MS2109 fix are reused from `HDMICaptain.Core/Audio`. The kernel's MS2109 quirk usually fixes the format already, so `AudioFormatFix` must leave a correct 48 kHz stereo stream alone.
- Never fail capture because audio is missing.

### 14.7 UI
- GTK4 + libadwaita (dark style around the video), following GNOME HIG. Same structure and copy as the other apps (`docs/copy.md`): status cards, connect illustration, no-signal/HDCP help, Connection Help, hover control bar (`GtkOverlay`), right-click menu.
- Window behaviour:
  - full screen (double-click, F11);
  - the cursor hides after 3 s;
  - window size presets;
  - borderless clean mode.
  - Wayland can't lock the aspect ratio while resizing, so the picture is letterboxed; after a resize, the window snaps to the source aspect when the compositor allows it.
  - Always on top is X11 only (`_NET_WM_STATE_ABOVE`). On Wayland the menu item explains the window-menu alternative.
- Shortcuts are the same as Windows, with Ctrl as the modifier. Global shortcuts come through the GlobalShortcuts portal where it exists.
- No tray icon in v1: GNOME has none by default, and StatusNotifierItem is optional later.
- Settings window: video device, audio device, format, scale/filter/colour, image adjustments, profiles, per-device presets. Prefs are JSON in `$XDG_CONFIG_HOME/hdmicaptain/`, with the same keys as `ViewerPrefs`.
- Accessibility: AT-SPI names for every control (GTK accessible properties), keyboard navigation, high-contrast and large-text styles. The UI follows the desktop's fractional scaling.
- Overlays are GTK widgets above the video and never reach screenshots, recordings or Save Recent (§6 rule).

### 14.8 Outputs & Pro
- Screenshot (free): PNG from the pass-1 texture to `$(xdg-user-dir PICTURES)/HDMI Captain`, plus a copy to the clipboard.
- Recording and Save Recent (Pro): FFmpeg libraries (LGPL build), with hardware H.264/HEVC through VA-API or NVENC and OpenH264 as the fallback, plus AAC → fragmented MP4 in `$(xdg-user-dir VIDEOS)/HDMI Captain`. Save Recent keeps an encoded ring trimmed at keyframes and remuxes it on save.
- Pro is a **licence key** bought on our site (vendor to be chosen: Lemon Squeezy, or Paddle + Keygen; ADR 0004 gets a Linux section).
  - It's activated once online and re-validated periodically; the cache allows offline use.
  - There is one gate, the equivalent of `EntitlementStore.gate`; free features never check it.
  - DEBUG builds use a fake store (`-debugUnlockPro YES`). Flathub reviewers get a key.

### 14.9 Code layout
```
Apps/Linux/                      .NET 10 solution (HDMICaptain.Linux.slnx); shares Apps/Windows/Directory.Packages.props versions where they overlap
  src/HDMICaptain.Linux.Media/   net10.0 (linux): V4l2Device/V4l2Source, DeviceCatalog (udev/inotify/sysfs), CaptureController, TestPatternSource, MjpegDecoder (libturbojpeg), AudioPipeline (libpulse/ALSA), Recorder/RollingRecorder (FFmpeg)
  src/HDMICaptain.Linux.App/     GTK4/libadwaita (Gir.Core): AppModel/ViewerModel, viewer window, control bar, settings, ProStore, strings
  tests/HDMICaptain.Linux.Media.Tests/  V4L2 parsing, decoder, renderer (EGL surfaceless), audio DSP glue
  packaging/                     Flatpak manifest (com.example.HDMICaptain), .desktop, metainfo, icons, udev rule, deb/rpm specs
native/render-gles/              shared C++ GLES 3 renderer (Android JNI + Linux C ABI shims); CMake
Apps/Windows/src/HDMICaptain.Core/  referenced as is (net10.0, pure); moves to a neutral path only if ADR 0006 says so
```
- `make linux-setup | linux-build | linux-test | linux-run LINUX_ARGS="-testPattern smpteBars" | linux-logs | linux-package`. From Git Bash on the Windows PC these run inside WSL (`wsl -d Ubuntu-24.04`) on an ext4 copy of the tree. On Linux they run directly.
- DEBUG builds accept the shared QA arguments (`-testPattern`, `-qaAction`, `-qaPhase`, `-qaShowStats`, `-qaPinControls`, `-qaSoakMinutes`, `-debugUnlockPro`) through the shared `LaunchOptions`.

### 14.10 Performance
- Targets:
  - 1080p60 shown at 60 fps with no drops (MJPEG and uncompressed);
  - glass-to-glass no worse than the Windows app on the same card and PC, and ≤ 1 frame above OBS preview;
  - a 30-min soak with flat memory and a flat audio buffer.
- WSLg numbers don't count: no true vsync, and RDP composition.
- Means:
  - mmap buffers decoded or uploaded in place;
  - no GC allocation per frame (`SustainedLowLatency` while live);
  - rtkit priority;
  - present on arrival;
  - `GtkGraphicsOffload`/direct scanout in full screen if measurement shows it helps;
  - ReadyToRun;
  - an EventSource/`dotnet-trace` stage trace like Windows' `HDMICaptain-Media`.

### 14.11 Acceptance tests (Linux v1, on a real GNOME/Wayland install; WSL runs 1–4 as a pre-check)
1. Plug in a card → picture and sound within 1 s.
2. Minimize, cover the window, switch workspaces, change scale or monitor, lock 5 min → no capture restart in the logs.
3. Unplug/replug, suspend/resume, another app streaming the card and then releasing it → recovers without user action.
4. 30-min soak at 1080p60: no growing drops, memory and audio buffer flat.
5. Latency measured with the latency probe and a 240 fps camera, windowed and full screen, recorded in `docs/research/devices/`.
6. Recording and Save Recent play back with A/V in sync. Screenshots and recordings contain no UI.
7. No `/dev/video` access → the right status card. No audio source → picture with an audio badge.
8. The Flatpak and the .deb both install, launch and capture on a clean Ubuntu 24.04. The .deb's udev rule keeps `power/control=on`.
