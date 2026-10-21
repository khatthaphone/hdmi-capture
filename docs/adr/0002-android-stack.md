# ADR 0002 — Android stack

- **Status:** proposed (2026-10-21). Code for every spike is built (tasks 071–076). It becomes **accepted** once the measurements below are filled in from ≥ 2 devices; a measurement that contradicts a decision reopens it.
- **Context:** spec §12, research in `docs/research/android.md`. The Apple apps are Swift/Metal; Android has to reach the same "plug in → picture in a second, never drops" bar on phones whose USB ports, SoCs and camera stacks vary widely.

## Decisions

| # | Area | Decision | Rejected | Why |
|---|---|---|---|---|
| 1 | Language / UI | Kotlin 2.4 + Jetpack Compose (Material 3), AGP 9, minSdk 29, targetSdk 36 | Flutter, KMP / Compose Multiplatform | Native media APIs everywhere; KMP would only wrap them (spec §4) |
| 2 | Shared logic | Port pure logic to `:core:model` (JVM); keep Swift and Kotlin in step with `docs/fixtures/` JSON vectors | KMP framework in the Xcode build | A few hundred lines; fixtures give the same guarantee without a cross-language toolchain |
| 3 | Primary capture | Own thin native stack on **libusb 1.0.30 + libuvc 0.0.8** (vendored, built from source; libusb as its own LGPL `.so`) | UVCCamera/AUSBC (unmaintained, 4 KB pages), UVCAndroid as the main path | Control over transfers, recovery and the frame path; no JNI per frame |
| 4 | Secondary capture | **Camera2** `LENS_FACING_EXTERNAL` only when no USB card is visible (e.g. ChromeOS) | Camera2 as primary | Rare on phones, 10 fps at 1080p in the generic HAL |
| 5 | Format choice | `FormatSelector` port + **USB link budget** (USB 2.0 → MJPEG for ≥ 720p60) | Descriptor-only choice | Many phones have USB 2.0-only ports |
| 6 | MJPEG | **libjpeg-turbo 3.2** to planar YCbCr, 2 threads, newest frame wins | MediaCodec MJPEG (vendor-only), AImageDecoder (RGBA, slow) | Universal, NEON SIMD; the GPU does the colour conversion |
| 7 | Render | **SurfaceView** (`AndroidExternalSurface`) + EGL/**GLES 3**, 2 passes (convert at source size → scale), draw on arrival, `setFrameRate` | TextureView, Vulkan | One vsync less than TextureView; the pass-1 texture is also the clean screenshot source |
| 8 | Audio | **Oboe 1.11** input pinned to the card's USB device + output, C++ port of the ring/drift controller | AUSBC userspace UAC, AudioRecord + AudioTrack | Low latency; never steals the audio interface from the kernel driver |
| 9 | Lifecycle | Capture tied to STARTED/STOPPED; activity handles config changes itself; PiP keeps capture | Tie to RESUMED, recreate on rotation | The "never drops" requirement (spec §12.5) |
| 10 | Pro | Play Billing 9 one-time product, acknowledged, never consumed | Subscriptions | ADR 0004 |

## Measurements (fill in from tasks 072–076)

| Device | Android | USB port | Card | Path | Format | fps shown | Decode ms (p50) | Glass-to-glass ms | 15-min soak |
|---|---|---|---|---|---|---|---|---|---|
| _pending_ | | | UGREEN-25854 | UVC | | | | | |
| _pending_ | | | MS2131 | UVC | | | | | |

Camera2 external exposed on: _pending (073)_.

## Consequences

- Three native libraries we own (`libhdmirender`, `libhdmicapture`, `libhdmiaudio`) plus `libusb1.0` and `liboboe`; all 16 KB aligned (`make android-check16k`).
- The LGPL notice for libusb and the BSD/IJG notices go into the app's About/licenses screen (task 085).
- Device-specific USB quirks (MediaTek/Exynos isochronous packet sizes) are ours to fix; UVCAndroid's fixes are the reference, and it stays the fallback if our stack can't be made reliable.
