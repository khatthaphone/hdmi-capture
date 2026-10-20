# HDMI Captain — Product & Technical Spec

_Status: draft v0.2 · 2026-10-20 (v0.1 MVP running; feature expansion + pricing added)_

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
- One Swift codebase serves **macOS + iPadOS (+ USB-C iPhone, visionOS)** via a shared Swift package (~80 % shared).
- **Android:** Kotlin + Jetpack Compose. **Windows:** C# WinUI 3 (or C++/WinRT) + Media Foundation.
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
Apps/iPadOS/                     (phase 3)
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
| 3 | iPadOS / USB-C iPhone target reusing CaptureCore; graceful background handling (OS-enforced there) |
| 4 | Mini-OBS: scene schema, Metal compositor (HDMI, webcam, ScreenCaptureKit, image, text, browser, chroma key), audio mixer, RTMP/SRT via HaishinKit, CoreMediaIO **virtual camera** |
| 5 | Android: Kotlin/Compose, UVC via USB Host + libuvc (Camera2 external where available), UAC audio, RootEncoder streaming |
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

Priority order. Details and acceptance criteria are in `docs/tasks/031`–`046`.

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
