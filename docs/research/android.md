# Android research: libraries, Kotlin stack, performance, code reuse, UI

_2026-10-21. Two research passes (capture/decode/audio; UI/architecture/tooling) plus a survey of our own code. Versions were checked against release pages on that date. Anything marked *estimate* is engineering judgment, not a measurement. The decisions are in `docs/spec.md` §12 and, after the spikes, `docs/adr/0002-android-stack.md`._

## 1. Summary

- **Main capture path:** USB Host API → native C++ using **libusb 1.0.30 + libuvc 0.0.8**, both built from source.
  - Camera2 external cameras are rare. Google's camera lead (Mar 2025) said "very few mobile devices" support them, and the AOSP generic HAL caps 1080p at about 10 fps.
  - Camera2 is an optional secondary path, used only when `getCameraIdList()` actually returns a `LENS_FACING_EXTERNAL` camera (some Chromebooks and OEM builds).
- **Decode:**
  - MJPEG: **libjpeg-turbo 3.2** straight to planar YCbCr (`tj3DecompressToYUV8`), with 2 decode threads at 1080p60.
  - YUY2 and NV12 go straight to the GPU.
  - There is no standard MediaCodec MJPEG decoder; probe for one as an optional fast path, never require it.
- **Render:** **SurfaceView** (Compose `AndroidExternalSurface`, *not* the TextureView-backed embedded variant) → EGL / **GLES 3** YCbCr shader (same math as `MetalRenderer`).
  - Render each frame as it arrives, with at most one frame in flight.
  - Use `Surface.setFrameRate(srcFps, FIXED_SOURCE)`.
  - An AHardwareBuffer ring gives zero copy.
- **Audio:** the kernel `snd-usb-audio` driver owns the card's UAC interfaces. Use **Oboe 1.11** full duplex with `setDeviceId(TYPE_USB_DEVICE)`.
  - The UVC code must claim **only** the VideoControl and VideoStreaming interfaces. It never detaches the audio driver.
- **Shell:** Kotlin (K2) + Jetpack Compose + Material 3.
  - State: ViewModel + StateFlow. Prefs: DataStore.
  - Pro: Play Billing 9 one-time product (non-consumable, acknowledged, never consumed).
- **No Kotlin Multiplatform.** Port the pure logic (FormatSelector, recovery state machine, SignalAnalyzer, presets) to a JVM-only `:core:model` module. Keep Swift and Kotlin behaviour in step with **shared JSON test fixtures** that both test suites read.
- **Fallback for a faster MVP:** `com.herohan:UVCAndroid:1.0.13` (Apache-2.0, 16 KB-ready, active in 2026). Use its frame callback only, with our own GL and Oboe.

## 2. Capture paths

### 2a. Camera2 / CameraX external cameras

- AOSP has an external camera provider HAL (`LENS_FACING_EXTERNAL`, hardware level `EXTERNAL`, API 28+). It is optional for OEMs and "not designed to support … high-resolution and high-speed streaming" ([AOSP](https://source.android.com/docs/core/camera/external-usb-cameras)).
- The generic HAL decodes MJPEG on the CPU into `YUV_420_888`. Its sample config caps frame rate at 640×480@30, 720p@15 and 1080p@10.
- There is no choice of MJPEG vs YUY2 on the wire, and format enumeration is limited.
- Google's advice is to check `FEATURE_CAMERA_EXTERNAL`, or use `UsbManager` with your own UVC driver. CameraX does not support custom UVC drivers ([thread](https://groups.google.com/a/android.com/g/camerax-developers/c/0Q5DHEE-FZo)). CameraX `LENS_FACING_EXTERNAL` is `@ExperimentalLensFacing`.
- **Pixel 6+ "Connected Cameras"** is a user feature that shows up in selected apps. It is not a documented general Camera2 path.
- **Samsung:** no evidence of UVC cameras through Camera2.
- **Verdict:** secondary path only. Spike 073 measures it wherever it appears.

### 2b. USB Host + libusb + libuvc (primary)

**Flow**
1. `UsbManager.openDevice()` → `fileDescriptor`.
2. Native `libusb_set_option(NO_DEVICE_DISCOVERY)` → `libusb_wrap_sys_device()`.
3. libuvc probe/commit, payload assembly, controls.

**Libraries**

| Library | License | Activity | 16 KB pages | Notes |
|---|---|---|---|---|
| saki4510t/UVCCamera | Apache-2.0 (+ LGPL libusb) | last push 2021 | no | The original; unmaintained; ndk-build |
| jiangdongguo/AndroidUSBCamera (AUSBC) | Apache-2.0 | release 2023, push 2024 | no (#744–746 open) | Abandoned; prebuilt 4 KB .so files; its userspace "UAC" fights the audio driver |
| **shiyinghan/UVCAndroid** | Apache-2.0 | **1.0.13, 2026-06** | **yes (since 1.0.10)** | Most active saki fork; 2026 fixes for transfer buffering and MediaTek black screen; open recording bugs #81, #105 |
| alexey-pelykh/UVCCamera | Apache-2.0 | 0.0.13 (2025-03) | no | Hard fork that collects fixes; Flutter plugin |
| Peter-St/Android-UVC-Camera | **LGPL-2.1** | 2025-09 | ? | Reference for iso packet tuning on odd SoCs, not a library |
| **libuvc/libuvc** upstream | BSD | **v0.0.8, 2026-09** | build ourselves | Basic UVC 1.5, NV21/I420 GUIDs, `DISABLE_JPEG`, YUYV OOB fixes |
| **libusb/libusb** | LGPL-2.1 | **v1.0.30, 2026-05** | build ourselves | Has Android `wrap_sys_device`. Ship it as a separate replaceable `.so` with its notice |

**Plan:** a thin stack of our own on upstream libusb and libuvc.
- About 1.5–3k lines of C++ (*estimate*): fd wrapping, iso/bulk tuning (queue depth, packets per transfer), a frame ring delivered to native decode and render with **no JNI per frame**, unplug recovery, and a small JNI facade.
- Use UVCAndroid's 2026 fixes as a reference (Apache, attribution).
- A pure-Java `UsbRequest` stack can't do isochronous transfers without JNI, so it is rejected.

**Pitfalls**
- **16 KB pages** (Play, since 2025-11-01): NDK r28+ (local `28.2.13676358`), AGP 8.5.1+. Check with `zipalign -c -P 16 -v 4`. No prebuilt `.so` files.
- **USB permission PendingIntent** (API 34+): must be `FLAG_MUTABLE` with an explicit intent (`setPackage`), or `EXTRA_DEVICE`/`EXTRA_PERMISSION_GRANTED` never arrive. Register the receiver with `RECEIVER_NOT_EXPORTED`.
- **Permissions:** `CAMERA` is needed before the USB grant (Android 9+), and `RECORD_AUDIO` for the UAC side.
- **Privacy toggles:** the global Camera and Microphone toggles (Android 12+) give a **black picture** with no error (GrapheneOS #4630). Detect them with `SensorPrivacyManager` and explain.
- **Attach filter:** a `USB_DEVICE_ATTACHED` intent filter + `device_filter.xml` (class 14) lets users tick "always open with HDMI Captain".
- **Bandwidth:** many phones (most mid-range Samsung A) have **USB 2.0-only ports**. About 35–40 MB/s is usable.

  | Format | Data rate | Fits USB 2.0? |
  |---|---|---|
  | YUY2 1080p60 | ~249 MB/s | No |
  | YUY2 720p60 | ~110 MB/s | No |
  | MJPEG 1080p60 | fits | Yes |

  The format selector must use `libusb_get_device_speed`, not only the descriptors.
- **Isochronous quirks:** MediaTek and Exynos often need smaller packets or alt settings. The `usbfs_memory_mb` cap limits in-flight transfers. This is the biggest long-tail risk.
- **Power:** cards with power passthrough have shown black pictures on Pixels; the UGREEN card worked. A powered hub sometimes helps.

## 3. Decode (MJPEG at 1080p60)

- **libjpeg-turbo 3.2.0** (2026-06, BSD/IJG): decode to planar YUV. Capture-card MJPEG is usually 4:2:2, so upload three `R8` planes and convert in the shader.
  - *Estimate:* 6–12 ms per frame on one A7x core.
  - Two decode threads (frame N and N+1) hold 60 fps on mid-range SoCs, adding at most one frame of latency.
  - No published Android 1080p numbers exist, so **benchmark on a cheap MediaTek phone early** (task 074).
- **MediaCodec MJPEG** is vendor-only (`OMX.Nvidia.mjpeg.decoder` ran about 12 fps at 720p) and tends to buffer frames. Treat it as an optional probe.
- **YUY2:** an `RGBA8` texture at width/2, unpacked in the fragment shader.
- **NV12:** `R8` + `RG8` textures.

## 4. Render

- **SurfaceView, not TextureView.** SurfaceView buffers go straight to SurfaceFlinger/HWC, as an overlay when possible. TextureView is composited again in the app's RenderThread, which costs about one more vsync plus GPU time.
  - Compose `AndroidExternalSurface(isOpaque = true)` is SurfaceView-backed. Hand its `Surface` to native code as an `ANativeWindow`.
  - Compose overlays draw above it and so can never get into the video buffer, which keeps the overlay rule.
- **GLES 3.0** is enough: one textured quad, 601/709, limited/full range, the four scale modes, linear or nearest, the 3D LUT texture and adjustments. Vulkan only if compositing (mini-OBS) arrives.
- **Zero copy:** decode into a CPU-locked `AHardwareBuffer` ring (3 buffers) → `eglGetNativeClientBufferANDROID` → EGLImage → texture.
  - Experiment: `ASurfaceTransaction_setBuffer` (SurfaceControl, API 29+) with YUV buffers straight to the HWC, with no GPU.
- **Pacing:** draw on arrival; don't wait on Choreographer. `Surface.setFrameRate(fps, FIXED_SOURCE, ONLY_IF_SEAMLESS)` stops 60 fps from juddering on 90/120 Hz panels. Swappy is not used (it queues).

## 5. Audio (UAC)

- Route explicitly with Oboe `setDeviceId()` / `AudioRecord.setPreferredDevice()` to the `TYPE_USB_DEVICE` input that matches the card (by product name and address).
- Use Oboe 1.11 full duplex with `LowLatency` and a small jitter buffer. USB input rarely gets MMAP.
  - *Estimate:* 20–50 ms capture to speaker on Pixels, worse on budget phones, much worse on Bluetooth.
  - This is where the existing **audio delay** setting (spec §11 #1) pays off.
- **MS2109 quirk:** reports mono 96 kHz but sends 48 kHz stereo. Linux 5.9+ has a quirk for it, and USB Camera Pro ships a "48 kHz stereo mode", so keep a software de-interleave fallback.
- **Plug order:** on Linux the audio driver can grab the whole MacroSilicon device. Test hot-plug order on Android.
- **Drift:** card audio clock vs output clock means long sessions need drift correction. The ring buffer gets the same flat-latency check as the Mac and iPad soak.

## 6. Recording and screenshots (Pro / free)

- **Video:** MediaCodec AVC/HEVC from `createInputSurface()`. Render the **same decoded textures** into the encoder's EGL surface on a shared context, so recordings come from frames, never views. Set `KEY_LATENCY` and `KEY_PRIORITY=0`.
- **Audio:** AAC through MediaCodec.
- **Muxing:**
  - `MediaMuxer` MP4 for normal recordings.
  - For Save Recent, keep an encoded ring of 2 s GOPs (the same design as `RollingRecorder`) and mux on save.
  - Consider fragmented MP4 (Media3 muxer) so a crash doesn't lose the recording.
- **Saving:** MediaStore with `IS_PENDING` under `Movies/HDMI Captain` / `Pictures/HDMI Captain`. No storage permission is needed on API 29+.
- **Screenshots:** `glReadPixels` from the frame FBO, never `PixelCopy` of the window, so overlays stay out.
- Avoid UVCAndroid's recording bugs (#81 "no key frame" on Android 14; #105 freezes) by owning the encoder path.

## 7. Latency references

| Setup | Glass to glass | Source |
|---|---|---|
| USB 3 UVC card on Android (old) | 120–200 ms | YouTube |
| Phone's own camera | ~80 ± 25 ms | inovex |
| USB camera on Linux (Jetson) | ~58 ms | NVIDIA forum |

- Our *estimated* target at 1080p60 MJPEG is **50–80 ms**: card about 1 frame, USB plus assembly about 1 frame, decode under 1 frame, display 1–2 vsyncs.
- For comparison, the iPad measures about 30 ms on screen; that figure excludes the card.
- Measure with a 240 fps phone camera filming a flashing test pattern next to the preview (task 076).

## 8. Kotlin stack and tooling (Oct 2026)

| Item | Pin |
|---|---|
| AGP | 9.4 + Gradle 9.6.1 |
| Kotlin | 2.4.21 (AGP 9 has built-in Kotlin, so **don't** apply `org.jetbrains.kotlin.android`) |
| JDK | **21** (Android Studio's JBR: `/Applications/Android Studio.app/Contents/jbr/Contents/Home`). The system JDK 26 is too new for AGP/Robolectric |
| compileSdk / targetSdk | **36** (installed locally; Play floor since 2026-08-31). Move compileSdk to 37 when it's installed; targetSdk to 37 before Aug 2027 |
| minSdk | **29** (multi-resume, camera-priority callbacks, thermal listener, external camera level; newer APIs guarded) |
| NDK | **28.2.13676358** (installed, AGP default, 16 KB by default) |
| CMake | **3.22.1 or 4.1.2** (installed) |
| Compose BOM | 2026.09.00 (ui 1.12, material3 1.4) |
| AndroidX | activity 1.13, lifecycle 2.11, navigation3 1.2, material3-adaptive 1.3, datastore 1.2.1, benchmark/baselineprofile 1.5 |
| Play Billing | 9.1.0 (Play rejects PBL ≤ 7) |
| Robolectric | 4.17 |
| Oboe | 1.11.0 |
| libjpeg-turbo | 3.2.0 |

- **CLI only:** `gradle wrapper` once (or copy a wrapper), then `./gradlew assembleDebug installDebug test connectedDebugAndroidTest bundleRelease`.
- **Makefile mirror:** `make android-build | android-run | android-test`.
- **Device debugging:** `adb pair` / `adb connect` (wireless), because the card holds the USB-C port. This is the Android twin of `make run-ipad`.
- **Emulator:** has no USB host. A native **TestPatternSource** behind the same `CaptureSource` interface covers render, overlays and screenshots. A **ReplaySource** of recorded raw UVC payloads covers the parser and MJPEG paths.

## 9. UI

**Look and feel**
- Material 3 with a **fixed dark theme** around the video. Expressive motion only on the control bar.
- Copy comes from `docs/copy.md`, the same words as the Apple apps, adapted for Android ("phone/tablet", Settings paths).

**Video area**
- The video ignores insets.
- Edge-to-edge is enforced at targetSdk 35+.
- Immersive mode hides the system bars, with transient show on swipe.
- Keep the screen on while live.

**Activity and windowing**
- Handle `configChanges` (orientation, screen size, layout, density, keyboard) ourselves, so rotating, resizing, folding or a DeX/desktop window change **never** tears down capture. This is the Android form of "never stop the session".
- Large screens: from targetSdk 36, sw ≥ 600 dp ignores orientation and resizability locks. Design for any window, and let the renderer letterbox.
- **PiP:** `setAutoEnterEnabled` (API 31+). Capture continues in PiP, since the activity is STARTED.

**Controls**
- **Control bar:** a port of `ControlBar`. Touch toggles it with a 3 s auto-hide; on mouse/trackpad (DeX, ChromeOS, desktop windowing) it appears on hover near the bottom edge.
- **Keyboard:** the same shortcuts as the Mac, plus `onProvideKeyboardShortcuts` for the Meta+/ helper.
- **Pointer:** right-click opens the same menu.
- **Predictive back:** on by default at target 36. Back leaves full screen and closes sheets first.

**Screens**
- Status screens: a port of `StatusScreens` — `StateCard` layout, connect illustration, pause reasons.
- New Android-only states:
  - USB permission needed
  - privacy toggle on
  - USB 2.0 port limiting the format
  - card needs more power
- Settings is a list-detail pane on tablets and desktop, and a full-screen sheet on phones.
- **Android TV** (leanback, D-pad focus) and **Chromebook** are later targets. Make every control focusable from the start.

## 10. Performance

| Tactic | How |
|---|---|
| No GC on the frame path | Pixels stay native. No per-frame JNI, `Flow.emit`, boxing or logging. Stats are sampled at 2–4 Hz into a StateFlow |
| Thread priority | USB and render threads at `URGENT_DISPLAY` (−8). Oboe gives a real-time audio thread |
| **ADPF** | `APerformanceHint` session for the USB, decode and render TIDs, target = 1e9/fps, report the actual duration each frame (API 31+) |
| Thermal | `addThermalStatusListener` + `getThermalHeadroom`. Step down (scopes off, then a lower format) before the OS throttles. Log it in the soak CSV |
| Display | `setFrameRate` to match the source; `preferredDisplayModeId` on TV for 59.94 |
| Startup | Baseline profile (launch → device → first frame), R8 full mode, `@Keep` on JNI entry points |
| Measurement | Perfetto (`ATrace_beginSection` on capture, decode, render, present), Macrobenchmark `FrameTimingMetric` / `TraceSectionMetric`, JankStats in debug builds |

Game Mode and sustained performance mode don't apply (rare, superseded by ADPF).

## 11. Reuse from our codebase

Port the behaviour and keep the names, so the two codebases read alike.

| Swift (Apple) | Android | How |
|---|---|---|
| `FormatSelector` | `:core:model` `FormatSelector` | Port it and add **link-speed bandwidth** plus an MJPEG vs raw preference. Shared fixtures from `docs/research/devices/*.json` |
| `CaptureState`, `PauseReason`, backoff 0.25 s → 4 s | `:core:model` `CaptureState` (sealed) + `RecoveryMachine` | Pure state machine fed by events. Android reasons: background, camera priority lost, privacy toggle, too hot, USB permission revoked |
| `DeviceCatalog`, `AVDeviceProvider`, `stableKey` | `UsbDeviceCatalog` (UsbManager + attach/detach broadcasts) | Key: VID:PID + serial, or + name when there's no serial. Pair the UAC `AudioDeviceInfo` by product name and address |
| `CaptureEngine` | `CaptureController` (single-thread control dispatcher) + native `UvcSource` / `Camera2Source` / `TestPatternSource` | Same always-on recovery rules |
| `MetalRenderer` + shader | `:core:render` GLES 3 | Same matrices, ranges, scale modes, LUT, adjustments |
| `SignalAnalyzer` | `:core:model` | Pure; grid sampled natively at 160×90 |
| `AudioPipeline`, `AudioRingBuffer` | `:core:audio` (Oboe, C++) | Delay 0–500 ms, L/R swap, mono fix, meters |
| `Presets`, `Profiles`, `ViewerPrefs` | DataStore | The same keys as `docs/` settings keys |
| `TestPattern`, `CubeLUT`, `Scopes` | native generator; `.cube` parser in `:core:model`; scopes later | |
| `EntitlementStore`, `ProFeature`, `UpgradeView` | `:core:billing` + upgrade sheet | Same feature list; streaming and virtual camera stay hidden |
| `StatusScreens`, `HelpSheet`, `docs/copy.md` | `strings.xml` | Same copy, Android paths |
| QA args (`-testPattern`, `-qaAction`, `-qaShowStats`, `-qaSoakMinutes`) | intent extras (`adb shell am start … --es qaAction screenshot`) | DEBUG builds only; same names |

**Real cards to test with** (from `docs/research/devices/`):

| Card | USB ID | Formats |
|---|---|---|
| UGREEN-25854 | Actions Micro `1DE1:F105` | NV12-class, 1080p60 |
| UGREEN HDMI Capture (MS2131) | MacroSilicon `345F:2131` | YUY2 + NV12, 720p60 / 1080p30 |

macOS hides MJPEG, so the spike's descriptor dump (072) records the real wire formats.

## 12. Competitors on Play

- **USB Camera / Pro** (shenyaocn): the leader. libuvc/libusb, MS2109 audio mode, a "high performance render" option, recording and streaming.
- **Infinitegra USB Camera**: works once the privacy toggles are off; settings changes sometimes cause black screens.
- **CameraFi, nExt Camera**: general camera and streaming apps.
- **Peter-St UVC (F-Droid)**: tuning tool.
- **Yaky viewer**: minimal.

Common complaints: black screens on some SoCs or cards, no audio or drift, a permission prompt on every plug-in, 30 fps or low-resolution caps, and ads. Our angles are a **free, ad-free viewer**, 60 fps, audio that works, and auto-reconnect.

## 13. Risks

1. Isochronous transfer quirks on MediaTek and Exynos (packet size, short frames): the main long tail.
2. USB 2.0-only ports force MJPEG, so decode speed and thermals set the fps on budget phones.
3. Pixel black-screen cases: privacy toggles, power-passthrough cards. Needs clear diagnostics in the UI.
4. No standard hardware MJPEG decoder: CPU and heat at 1080p60. Soak-test it.
5. LGPL libusb: dynamic linking plus notices.
6. Play policy churn: 16 KB pages, a yearly targetSdk bump, FGS-type declarations (camera/microphone FGS needs a justification).
7. A/V drift on long sessions; the MS2109 channel quirk.
8. The library ecosystem is mostly unmaintained. Owning the stack is the hedge, and UVCAndroid is the fallback.
9. Hardware: **no Android device is attached to this Mac yet.** Spikes 072–076 need at least 2 devices (ideally a Pixel and a Samsung or MediaTek phone with USB-C) plus the two UGREEN cards.

## Sources

AOSP external USB cameras · camerax-developers thread (Talvala, 2025-03) · CameraX `CameraSelector` reference · Pixel help 15985851 · github.com/{saki4510t/UVCCamera, jiangdongguo/AndroidUSBCamera, shiyinghan/UVCAndroid, alexey-pelykh/UVCCamera, Peter-St/Android-UVC-Camera, libuvc/libuvc, libusb/libusb, libjpeg-turbo/libjpeg-turbo, google/oboe} · GrapheneOS os-issue-tracker #4630 · developer.android.com: AGP release notes, built-in Kotlin migration, target-SDK requirements, 16 KB page sizes, Compose BOM mapping, Media3 surface, PerformanceHintManager, Android 16/17 behaviour changes · Play Billing release notes · Robolectric releases · inovex glass-to-glass latency post · NVIDIA forums (MJPEG decoder, Jetson latency).
