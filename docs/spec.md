# HDMI Captain — Product & Technical Spec

_Status: draft v0.3 · 2026-10-21 (Mac + iPad in code; Android plan added, §12)_

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
- **Android:** Kotlin + Jetpack Compose shell over a C++ (NDK) media core: libusb + libuvc capture, libjpeg-turbo MJPEG decode, GLES 3 render, Oboe audio (§12, `docs/research/android.md`). Pure logic is ported to Kotlin, not shared; shared JSON test fixtures keep Swift and Kotlin behaviour in step. **Windows:** C# WinUI 3 (or C++/WinRT) + Media Foundation.
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
| 6 | Windows: WinUI 3 + Media Foundation `MediaCapture`, D3D11 render, MF/FFmpeg-LGPL encode |
| 7 | Free viewer features from competitor research (§11) |
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
