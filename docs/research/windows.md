# Windows research: capture, audio, stack, distribution, competitors

_2026-10-22. Three research passes (capture/audio; UI/render/language stack; distribution/encode/competitors) plus a survey of this PC and our code. Versions were checked on that date. Anything marked *estimate* is engineering judgment, not a measurement. Decisions are in `docs/spec.md` §13 and, after the spikes, `docs/adr/0003-windows-stack.md`._

**User decisions (2026-10-22):**
- **Stack:** C# on .NET 10 with WinUI (Windows App SDK 2.x). This replaces the earlier idea of a "Rust core + Win32 shell" (logged in tasks 029/030).
- **Distribution:** Microsoft Store MSIX first.
- **Hardware:** the cards will be plugged into the dev PC (Ryzen 7 9700X, RTX 5070 Ti and the Radeon iGPU, Windows 11 Pro 26200).

## 1. Summary

- **Capture:** Media Foundation `IMFSourceReader` in **async mode**, with **one `ReadSample` outstanding** so we always get the newest frame.
  - Set the device's **native media type** with `MF_READWRITE_DISABLE_CONVERTERS = TRUE`. Never enable `MF_SOURCE_READER_ENABLE_(ADVANCED_)VIDEO_PROCESSING`, so MF can't insert a converter. Set `MF_LOW_LATENCY`.
  - WinRT `MediaCapture` + `MediaFrameReader` (Realtime mode) is the fallback.
  - We don't use DirectShow. OBS uses it for history and legacy PCIe cards; that isn't relevant to UVC dongles.
- **MJPEG:** **libjpeg-turbo** (`tj3DecompressToYUV8`) to planar YCbCr, using 2 threads at 1080p60.
  - Optional fast paths, used only if a probe succeeds: the MF MJPEG decoder MFT, or DXVA MJPEG (`DXVA_PicParams_MJPEG`, Windows 11 24H2+, depends on the driver).
- **Render:** D3D11 + our own HLSL YCbCr→RGB shader (same maths as `MetalRenderer`) into a **flip-model swap chain**.
  - `FLIP_DISCARD` with a frame-latency waitable object, `SetMaximumFrameLatency(1)` and `ALLOW_TEARING`. Present when the frame arrives.
  - Start with a WinUI `SwapChainPanel`. If PresentMon shows it never reaches independent flip in full screen, viewer windows switch to an HWND swap chain.
- **Audio:** WASAPI shared mode, event-driven (`IAudioClient3` small periods where available).
  - Port `AudioRingBuffer` and `AudioPipeline` (delay, channel modes, meters).
  - Add a drift controller, since card and output clocks differ by tens of ppm.
  - Follow the default output with `IMMNotificationClient`.
  - Pair the card's capture endpoint with its video interface by **USB ContainerId**.
- **Shell:** C# on .NET 10 with **WinUI** (Windows App SDK 2.5.x).
  - Win32 calls go through **CsWin32**: windows, Media Foundation, WASAPI, power settings, the tray icon.
  - D3D11/DXGI through **Vortice.Windows**.
  - Pure logic is ported to a `HDMICaptain.Core` library tested against `docs/fixtures/` (same approach as Kotlin).
- **Ship:** MSIX through the Microsoft Store (free individual account; the Store signs the package, so there's no SmartScreen warning; the OS updates it).
  - Pro is a `StoreContext` **durable add-on**.
  - A direct download (Velopack + license key) is deferred.
- **Pro:** recording + Save Recent. As on Android, streaming and the virtual camera stay hidden.
  - A virtual camera (`MFCreateVirtualCamera`, Windows 11) is planned first as a **dev test source** and later as the Pro feature.

## 2. Capture

### 2a. API choice

| API | Verdict | Why |
|---|---|---|
| **MF `IMFSourceReader` (async)** | **Primary** | Exact native type control, error events (`OnEvent`), clean shutdown; `MF_LOW_LATENCY`, `MF_READWRITE_DISABLE_CONVERTERS` |
| WinRT `MediaCapture` + `MediaFrameReader` | Fallback | `Realtime` mode drops frames while the handler runs (good). Downsides: per-frame object churn (`MediaFrameReference`, `SoftwareBitmap`) and weaker subtype control. `SharedReadOnly` / `ExclusiveControl` sharing modes |
| DirectShow (OBS `win-dshow` / libdshowcapture) | Rejected | Legacy per Microsoft. OBS keeps it for crossbars, vendor property pages and old KS drivers. Its MJPEG decode goes through FFmpeg on the CPU |

- **Frame Server** (since Windows 10 1607) sits between apps and the camera. Nobody has published how much latency it adds (*estimate: well under a frame*), so we measure it in 092.
  - Never ship the `EnableFrameServerMode=0` registry tweak.
- **Windows 11 (KB5052093, 26100.3321):** a per-camera setting **"Allow multiple apps to use camera at the same time"**. When it's on, Windows picks the media type, so we may not get our chosen 1080p60 type.
  - Detect this and show the actual format (the requested vs actual display from 032).
- **Frames:** raw YUY2/NV12 arrive in system memory and we upload them to a D3D11 texture ourselves. The MF D3D manager (`MF_SOURCE_READER_D3D_MANAGER`) only helps when MF decodes.
  - Use `MF_SOURCE_READER_PASSTHROUGH_MODE` to stop MF making automatic copies.

### 2b. Enumeration, hot-plug, identity

- **Enumerate:** `MFEnumDeviceSources` (VIDCAP) → friendly name and symbolic link.
- **Hot-plug:** `CM_Register_Notification` on `KSCATEGORY_VIDEO_CAMERA` (no window needed; callbacks arrive on the thread pool, so never unregister from inside a callback), or `DeviceWatcher`. Compare symbolic links **case-insensitively**.
- **Stable key:** the symbolic link includes the USB instance path. Cards without a serial number (MS21xx, *likely*) get a different path on each port.
  - Persist VID:PID + name (+ serial when present) and fall back to a fuzzy match, as on Mac and Android.
- **Audio pairing:** every interface of a composite USB device shares one **ContainerId** (`DEVPKEY_Device_ContainerId`). Match the endpoint's `PKEY_Device_ContainerId` against the one from `IMMDeviceEnumerator`.
  - This works even with two identical cards plugged in.

### 2c. Formats

- `GetNativeMediaType(stream, i)` until `MF_E_NO_MORE_TYPES` gives `MF_MT_SUBTYPE` (`MJPG`, `YUY2`, `NV12`), `MF_MT_FRAME_SIZE` and `MF_MT_FRAME_RATE` (+ range).
- `FormatSelector` already understands the UVC names (`NV12`, `YUY2`, `MJPG`; see `docs/fixtures/README.md`), so the C# port uses the same fixtures.
- Typical cards:
  - MS2109 (USB 2.0): 1080p60 only as MJPEG; YUY2 only at low resolutions or frame rates.
  - MS2130/MS2131 (USB 3): YUY2/NV12 at 1080p60.
- The `format-bandwidth.json` link-speed rule (Android) also applies. Windows reports the link speed through `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX(_V2)` on the hub (*to verify in 089*). Without it, prefer uncompressed when that type exists at the target frame rate.

### 2d. Errors to map (→ `CaptureState`)

| Condition | Signal | UI |
|---|---|---|
| Unplug | Device-interface removal; `MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED` / `MF_E_SHUTDOWN` from `ReadSample` | Recovering → scanning |
| Camera in use | `MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED` (0xC00D3EA3), `MF_E_HW_MFT_FAILED_START_STREAMING` (0xC00D3704) | "In use by another app", retry with backoff |
| Camera privacy off | `E_ACCESSDENIED` from `ActivateObject` / `InitializeAsync` | Card + button → `ms-settings:privacy-webcam` |
| Microphone privacy off (camera allowed) | `E_ACCESSDENIED` from WASAPI on the card endpoint | Picture works; audio badge + `ms-settings:privacy-microphone` |
| Sleep / resume | `RegisterSuspendResumeNotification` / `PBT_APMRESUMEAUTOMATIC` | Rebuild the reader |

HRESULT values checked against `mferror.h` in SDK 10.0.26100 (089); they live in `MfErrors.cs`: `MF_E_SHUTDOWN` 0xC00D3E85, `MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED` 0xC00D3EA2, `MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED` 0xC00D3EA3, `MF_E_HW_MFT_FAILED_START_STREAMING` 0xC00D3704, `MF_E_VIDEO_DEVICE_LOCKED` 0xC00D4E24, `MF_E_NO_MORE_TYPES` 0xC00D36B9.

Observed on the dev PC (Windows 11 26200, UGREEN-25854, 2026-10-22, task 089):

| Condition | What actually happened |
|---|---|
| Windows Camera opens the card while we stream | Nothing: no `OnEvent`, no error, our 1080p60 keeps 60 fps. Windows 11 shares the card through the Frame Server, so "in use" is not an error for a second reader |
| We open while Windows Camera streams | Open succeeds at our type. A non-matching type (720p30 while Camera runs) "succeeds" but arrives at ~13 fps with gaps, so the frame rate watchdog has to catch it, not an HRESULT |
| Second reader of the same card in the same process | Both readers alternate: each open preempts the other (`MF_E_VIDEO_RECORDING_DEVICE_PREEMPTED`, every ~3 s with retries). Keep one session per card per process (task 098) |
| A DirectShow app (ffmpeg `-f dshow`) holds the card when we open | Our `SetCurrentMediaType` succeeds, then the first `ReadSample` fails with `MF_E_HW_MFT_FAILED_START_STREAMING` → "Paused: another app is using the capture card"; we retry every 3 s and were live 1.5 s after ffmpeg let go (102, 2026-10-23). The reverse is exclusive too: ffmpeg's dshow graph fails to run ("device already in use") while we stream, so DirectShow clients don't share through the Frame Server the way Windows Camera does |
| A second HDMI Captain process opens the card while the first streams | `MF_E_HW_MFT_FAILED_START_STREAMING` on the first `ReadSample` (102, 2026-10-23), so it shows the in-use card and retries. Windows Camera, by contrast, shares (row above): it presumably falls back to shared read-only, which our `MFCreateDeviceSource` path doesn't ask for |
| HDMI source unplugged (card stays on USB) | Nothing from MF: the card keeps delivering frames, irregularly (~30 fps with gaps). Signal loss must come from `SignalAnalyzer` on the picture plus a frame-gap watchdog |
| USB unplug mid-stream | *pending: needs the user to pull the cable* |
| Camera privacy off | *pending*. Writing `Deny` to HKCU `CapabilityAccessManager\ConsentStore\{webcam,microphone}\NonPackaged` does not affect a newly started process (tried for the microphone: it opened), so this has to be toggled in Settings |
| Sleep / resume | *pending* |

**Windows decodes MJPEG for us.** The UGREEN-25854 negotiates USB 2 high speed on this PC, yet lists NV12 1080p60 next to every MJPEG type. Each NV12 twin carries an undocumented attribute `{EA031A62-8BBB-43C5-B5C4-572D2D231C18}` = 1, reports full range + BT.601 (JPEG's), and streams 60 fps steadily even with `MF_READWRITE_DISABLE_CONVERTERS`. So the camera pipeline decodes MJPEG upstream of the reader. `MfDevices.Choose` skips the USB-link bandwidth rule when such types exist. Whether our own libjpeg-turbo path (090) beats it on latency is a 092 question. The 30 fps modes on this card are irregular (17–25 fps); the 60 fps modes are clean. One Store competitor's top complaint is that "denying the mic silently breaks capture". We treat the microphone as optional.

**The decoded twin blocks the raw MJPEG type for other readers** (090, 2026-10-22). While one process (our app) streams the Windows-decoded NV12 1080p60 twin, a second reader can open the same twin and share it (~60 fps after a short gap). But setting the raw `MJPG` type, at 1080p60 or 720p60, opens without error and then fails the first `ReadSample` with `MF_E_HW_MFT_FAILED_START_STREAMING` (0xC00D3704). With the card free, raw MJPG at 1080p60 streams a steady 60 fps after about 3 s of start-up, and the first 3 frames are not JPEG (`02 82 …`); the decoder drops them. So `PreferOwnMjpegDecode` can turn "another app uses the card" into "in use" where the twin would have worked. If 092 makes our decoder the default, fall back to the twin on that error.

## 3. MJPEG decode

| Option | Verdict |
|---|---|
| **libjpeg-turbo 3.x** (`tj3DecompressToYUV8`, BSD/IJG) | **Default.** SIMD, decodes straight to planes and the GPU does the colour conversion. *Estimate:* 3–8 ms per 1080p frame on one core; 4K30 needs about 4× that, so use 2+ threads or restart markers |
| MF MJPEG decoder MFT (`mfmjpegdec.dll`) | Probe only. CPU (*likely*); less control over threads and colour metadata |
| DXVA MJPEG (D3D11/12 video decode, Windows 11 24H2+) | Optional fast path when `GetVideoDecoderProfile` lists it. Intel likely; NVIDIA/AMD support unknown, and this PC has both GPUs to check |

Ship libjpeg-turbo as a native DLL (vcpkg `libjpeg-turbo:x64-windows` / `arm64-windows`) called through P/Invoke. No JNI-style copy is needed: decode into pinned or native plane buffers and map them into dynamic textures.

**Measured (090, 2026-10-22, Ryzen 7 9700X, libjpeg-turbo 3.1.3 x64, `HDMICaptain.Inventory --bench-mjpeg N`):** we captured raw MJPG from the UGREEN-25854 and decoded it offline with `tj3DecompressToYUVPlanes8` into reused native planes. Times are per frame and include the header parse.

| Input | Threads | Mean | p50 | p95 | Max | Throughput |
|---|---|---|---|---|---|---|
| UGREEN 1080p60, 600 real frames (4:2:0, ~73 KiB, desktop content) | 1 | 0.94 ms | 0.91 ms | 1.25 ms | 1.64 ms | 1055 fps |
| same | 2 | 0.94 ms | 0.91 ms | 1.26 ms | 1.49 ms | 2098 fps |
| UGREEN 720p60, 300 real frames | 1 | 0.63 ms | 0.59 ms | 0.84 ms | 1.33 ms | 1563 fps |
| UGREEN 1080p60, 117 real frames (second run) | 1 | 1.08 ms | 1.00 ms | 1.48 ms | 2.13 ms | 894 fps |
| 1080p 4:2:0 per-pixel noise, q85, 1.5 MiB (worst case, beyond USB 2's per-frame budget) | 1 | 9.14 ms | 8.99 ms | 9.99 ms | 11.0 ms | 108 fps |
| same | 2 | 9.22 ms | 8.99 ms | 10.38 ms | 11.4 ms | 213 fps |

- The card sends **4:2:0** MJPEG, full-range BT.601 (JFIF), so it decodes to `I420`.
- Real 1080p frames take about 1 ms, roughly 3–8× under the estimate above. One decoder on the MF thread is enough for 1080p60. Even the noise worst case (about 9 ms) fits a 16.7 ms frame.
- 4K30 MJPEG wasn't measured (this card has no 4K type). With about 4× the pixels, real content should take about 4–5 ms on one thread.
- **Decision:** decode inline on the MF callback thread (one `ReadSample` in flight, so the newest frame wins and nothing queues). Keep Windows' decoded NV12 twin as the default (`MjpegPolicy.Auto`). Our decoder is used for MJPG types without a twin, and for every MJPG type when `CaptureControllerOptions.PreferOwnMjpegDecode` is set. 092 decides from glass-to-glass latency whether our decoder should win when a twin exists. A worker pool (task 090 step 1) is unnecessary at these numbers.
- **DXVA MJPEG** (`HDMICaptain.Inventory --probe-dxva`, driver state 2026-10-22): the RTX 5070 Ti exposes 30 D3D11 decoder profiles and the Radeon iGPU (9700X) 20, but **neither lists any `MJPEG_VLD_*` profile**. On this PC the hardware fast path doesn't exist, so libjpeg-turbo stays the decoder.

## 4. Render

- **Device:** D3D11 on the **adapter that drives the window's monitor**, so frames aren't copied across adapters. This PC is hybrid (Radeon iGPU + RTX 5070 Ti).
  - When the window moves to another monitor (`WM_DISPLAYCHANGE`, `DXGI_ADAPTER_DESC` LUID), recreate the device on the right adapter.
- **Conversion:** our own pixel shader with the same matrices, ranges, scale modes, filters, adjustments, `.cube` LUT and guides as `Shaders.metal`. Two passes (convert at source size → scale), like Android; the pass-1 texture is the clean source for screenshots and recordings.
  - NV12: two SRVs (`R8`, `R8G8`). YUY2: `R8G8B8A8` at half width. Planar YUV from JPEG: three `R8` textures.
  - `ID3D11VideoProcessor` stays a fallback only (vendor-dependent colour handling).
- **Presentation:**
  - Flip model (`FLIP_DISCARD`, 2–3 buffers), waitable object, `SetMaximumFrameLatency(1)`, `ALLOW_TEARING` when `CheckFeatureSupport` allows it.
  - Present as each frame arrives; never use a timer.
  - Use borderless full screen, not exclusive full screen.
- **`SwapChainPanel` vs HWND swap chain:**
  - Only the HWND swap chain is documented to reach **independent flip** (when a window's frames bypass DWM composition).
  - The composed path adds up to about one refresh interval (*estimate:* 16.7 ms at 60 Hz). That matches the Mac in a window, but full screen should do better.
  - A `SwapChainPanel` covered by XAML (the control bar) is almost certainly composed. Measure with **PresentMon** ("Hardware: Independent Flip" vs "Composed: Flip").
  - The plan is to keep the video surface replaceable: if 088/092 show the panel never reaches independent flip in full screen, viewer windows use an HWND swap chain with the control bar in a separate layered popup window, and Settings and dialogs stay in WinUI.
- **`SwapChainPanel` limits:** `SetSwapChain` runs on the UI thread only, and Microsoft advises at most about 4 swap chains. Multi-View therefore composes every tile into **one** swap chain.
- **Later:** the Composition Swapchain API (`IPresentationManager`, Windows 11) for target-time presents and present statistics; HDR (scRGB / HDR10 via `SetColorSpace1`). Not in v1.
- D3D11, not D3D12: MF interop is native to it, and the workload is one quad per frame.

## 5. Audio

- **Capture:** the card endpoint (ContainerId match) in WASAPI shared, event-driven mode, with **`IAudioClient3.InitializeSharedAudioStream`** at the smallest period the engine allows.
  - The UAC1 class driver probably stays at 10 ms (*estimate*). `AUDCLNT_E_ENGINE_PERIODICITY_LOCKED` → fall back to `Initialize`.
- **Render:** the default output, event-driven. The output's mix format may be 44.1 kHz:
  - with `IAudioClient3`, resample ourselves;
  - on the legacy path, `AUTOCONVERTPCM | SRC_DEFAULT_QUALITY` is acceptable.
- **Drift:** keep the ring fill near a target with an adaptive resampler, or use `AUDCLNT_STREAMFLAGS_RATEADJUST` + `IAudioClockAdjustment::SetSampleRate` on the render client. OBS's "Monitor Only" is known to drift.
- **Features (same as spec §11 #1):** delay 0–500 ms, channel modes (auto / L↔R swap / mono fix), volume, mute, meters, output choice.
- **MS2109 quirk (verified on Linux):** it advertises **96 kHz mono 16-bit** but actually sends **48 kHz stereo**, possibly with L/R swapped. Reinterpret it when the endpoint reports 96 kHz/1 ch. Our MS2131 isn't affected, so test the rule with a synthetic stream (fixture).
- **Exclusive mode:** never by default (it locks the output for other apps). Possibly an "ultra-low latency" toggle later.
- **Threads:** MMCSS `"Pro Audio"` for audio, `"Capture"` for capture/decode.
- *Estimate:* about 25–40 ms of audio latency end to end (capture 10 + ring 10–20 + render 3–10).
- **"Listen to this device"** (mmsys.cpl) is the OS workaround users rely on. Mention it only in Connection Help, as a troubleshooting tip.

## 6. Always-on

Windows doesn't suspend desktop apps on focus loss, so the Mac bug can't happen here. Still:
- **While live:** `PowerCreateRequest` + `PowerSetRequest(PowerRequestDisplayRequired)` with a reason string (shown in `powercfg /requests`), or `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED)`.
- **Opt out of EcoQoS:** `SetProcessInformation(ProcessPowerThrottling)` with `EXECUTION_SPEED` and `IGNORE_TIMER_RESOLUTION` both set to high QoS. Task Manager's "Efficiency mode" can still be forced by the user.
- **Minimized or occluded:** keep capture and audio running and skip presents. The Mac rule (spec §5.3) applies unchanged.
- **Sleep:** the stream dies. Rebuild it on resume through the recovery machine (backoff 0.25 s → 4 s, `recovery-backoff.json`).
- **No timers in the frame path:** frames are pushed by MF callbacks, audio by WASAPI events, and presents are paced by the swap-chain waitable. The per-process `timeBeginPeriod` limits on Windows 11 therefore don't matter.

## 7. Stack decision

| Option | Verdict |
|---|---|
| **C# .NET 10 + WinUI (WinAppSDK 2.5.x)** | **Chosen.** Native controls, menus, dark mode, Mica, UI Automation (screen readers), `.resw` localisation, `KeyboardAccelerator`, `AppWindow` presenters (always on top, full screen) and Store APIs come almost free. The engine is plain C# over CsWin32 + Vortice |
| Rust core (windows-rs 0.62) + Win32 shell | Rejected. Best start-up time and size, but *estimate* 3–5× the UI work (dark menus need undocumented uxtheme; UIA by hand), and it shares nothing with Android's C++ engine. `windows-reactor` (WinUI from Rust) is too young (0.100, Apr 2026) |
| C++/WinRT + WinUI | Rejected. Stable (3.0 line, 2026) but not growing; IDL/MIDL and slow builds for XAML |
| WPF + `D3DImage`, Qt 6, Avalonia, Tauri, Flutter | Rejected. Extra copies or a D3D9 bridge (WPF); C++ and LGPL with a non-native look (Qt); airspace limits with `NativeControlHost` (Avalonia) and WebView2 (Tauri); a texture-registrar copy (Flutter) |

**Windows App SDK facts:**
- **Versions:** 2.0 went stable on 2026-04-29 (semantic versioning); 2.5.1 on 2026-09-16; 1.8 left support on 2026-09-24. The product is now just "WinUI". Development moved to GitHub in August 2026.
- **System compositor:** Microsoft announced a move to the system compositor. *Estimate:* this could change how the `SwapChainPanel` is composed, so re-run the latency check on every SDK bump.
- **Packaging:** packaged (MSIX) and unpackaged, framework-dependent and self-contained, are all supported.
- **Package identity:** Store purchases, the virtual camera and per-app privacy listing need it. Debug can run unpackaged with a fake Pro store.
- **Native AOT:** supported since 1.6. Startup is *about* 350 ms vs 465 ms with JIT (Kerr's numbers, disputed). It brings trim warnings (use `x:Bind` and source generators, no reflection binding) and slow builds. Ship ReadyToRun if AOT misbehaves.
- **CLI:** the `dotnet new winui` templates, `dotnet build`/`publish` and the `winapp` CLI (packaging, signing, identity) all work without opening Visual Studio.

**Code sharing:** port the pure logic per language with the JSON fixtures; the C# port is small. Revisit a Rust core with UniFFI (`uniffi-bindgen-cs` 0.11) only when the mini-OBS scene and compositor logic grows big enough that keeping three ports in step costs more than the build-system tax. The **virtual camera** has to be a native COM DLL (C++), whatever language the shell uses.

## 8. Recording, Save Recent, screenshots

- **Encoder:** MF Sink Writer using the hardware encoder MFTs (`MFTEnumEx(MFT_ENUM_FLAG_HARDWARE)`: NVENC, AMF, QSV) for H.264/HEVC, with the inbox AAC encoder.
  - Fall back to the software H.264 MFT. HEVC in software needs the paid HEVC extension, so on machines without a hardware encoder offer H.264 only.
- **Container:** fragmented MP4 (`MFCreateFMPEG4MediaSink`) so files survive crashes and unplugs.
- **Save Recent:** a deque of encoded samples trimmed at IDR frames, with a forced keyframe every 1–2 s (`CODECAPI_AVEncVideoForceKeyFrame`). On save, remux in passthrough into a second Sink Writer; nothing is re-encoded. This mirrors `RollingRecorder`.
- **Encoder latency:** the NVENC MFT's latency grows when input frames arrive at a limited rate. Set `CODECAPI_AVLowLatencyMode` and drive the encoder asynchronously. The direct NVENC/AMF/QSV SDKs are the fallback if MF proves flaky.
- **FFmpeg (LGPL DLLs):** the alternative if MF proves unreliable, and for streaming later. No `--enable-gpl`/`nonfree`; ship source and notices. *Estimate:* +10–25 MB.
- **What we built (100, 2026-10-22):** we drive the encoder MFTs ourselves and give the Sink Writer only encoded samples (passthrough), so Recorder and Save Recent share one path. Sink Writer encoding wouldn't let us keep the encoded samples.
  - **Hardware:** `MFTEnumEx(HARDWARE)` filtered by `MFT_ENUM_HARDWARE_VENDOR_ID_Attribute` = the render adapter's vendor, because a hybrid PC lists every GPU's encoder. On the RTX 5070 Ti that is **"NVIDIA H.264 Encoder MFT"**: async (`MF_TRANSFORM_ASYNC_UNLOCK`, a `GetEvent` loop), with `MFT_MESSAGE_SET_D3D_MANAGER` = an `MFDXGIDeviceManager` on the renderer's device. That device is now created with `D3D11_CREATE_DEVICE_VIDEO_SUPPORT`, made multithread-protected, and fed `MFCreateDXGISurfaceBuffer` samples over our pooled NV12 textures. **D3D11 input works; there is no CPU readback.** A pool slot is freed once the output with its time comes out (no B-frames).
  - **NV12 conversion:** two draws into R8 and R8G8 RTVs on the planes of an NV12 texture (BT.709 limited). This works on WARP and NVIDIA. The renderer leaves its source bound as a render target, so bind the output before the SRV, or D3D11 drops the SRV and you encode black.
  - **Software fallback:** the inbox "H264 Encoder MFT", sync, fed through a staging copy. It is used on WARP (all tests) or when no vendor encoder matches.
  - **Audio:** the inbox AAC MFT takes 16-bit PCM only; its output type carries the AudioSpecificConfig that the passthrough MP4 needs.
  - **Container:** `MF_TRANSCODE_CONTAINERTYPE = FMPEG4` on the Sink Writer gives 'moof' fragments in passthrough. Save Recent writes plain MP4.
  - **Interop:** use `Marshal.GetUniqueObjectForIUnknown`. Two recorders on one device share the cached RCW for the device pointer, and our `FinalReleaseComObject` killed it under the other thread.
  - **Numbers:** Save Recent of 15 s remuxes in about 30 ms. On the real card, the drain plus finalize at stop takes about 8 ms, and 5 frames drop while NVENC starts.
- **Screenshot (free):** WIC PNG from the pass-1 texture, never from the window (spec §6 rule), to `Pictures\HDMI Captain` + clipboard.
- **Streaming (hidden in v1):** libsrt (MPL-2.0) + our own small RTMP publisher (C#, a few hundred lines, like HaishinKit), or LGPL FFmpeg.

## 9. Virtual camera

- **API:** Windows 11 22000+ `MFCreateVirtualCamera(SoftwareCameraSource, Session|System, CurrentUser|AllUsers, name, CLSID, …)`.
  - It loads our in-proc COM **media source DLL** into Frame Server (LocalService), Frame Server Monitor (LocalSystem), our app and every consuming app.
  - `CurrentUser` access needs no admin rights.
  - Never call it on the UI thread, because the consent prompt deadlocks.
- **Samples:** Microsoft `Windows-Camera/Samples/VirtualCamera` (registers through an MSI), `smourier/VCamSample` (MIT, Direct2D source, NV12/RGB32).
- **Open risk:** can Frame Server see a COM server registered only inside an MSIX? Camo Studio ships a Store MSIX with a virtual camera, so it is possible.
- **Dev use:** a synthetic source (SMPTE bars, grid, frame counter) looks like a real camera to MF, so we can exercise enumeration, format choice, hot-plug (create/remove) and recovery without a card. It doesn't reproduce USB quirks (MJPEG-only modes, unplug mid-transfer), and CI still uses `-testPattern`.
- **Windows 10:** DirectShow filter (softcam, MIT), not visible to the Windows Camera app. Not planned.

## 10. Distribution, Pro, compliance

- **Store account:** free for individuals since Sept 2025, with ID + selfie verification. Policy 7.20: the account type can't change later, and a business-sounding publisher name needs a company account.
- **Commission:**
  - **Store commerce: 15%** for apps.
  - **0%** with our own processor (Policy 10.8.1; it must be declared and the provider named at checkout).
  - v1 uses Store commerce (simplest, refunds handled).
- **MSIX vs EXE in the Store:** MSIX gets free signing and hosting, OS updates, flighting and `StoreContext` add-ons. EXE/MSI needs our own certificate and our own commerce.
- **Manifest:** `runFullTrust`, `DeviceCapability webcam` and `microphone` (each declared capability must be justified, Policy 10.6).
- **Pro: `Windows.Services.Store` durable add-on.**
  - Purchase: `StoreContext.GetDefault()` + `InitializeWithWindow.Initialize(ctx, hwnd)` (else 0x80070578), `RequestPurchaseAsync` on the UI thread.
  - Ownership check: `GetAppLicenseAsync().AddOnLicenses`. Re-check on launch and when back online, and cache the result.
- **Testing Pro:** there is no sandbox or config file like StoreKit's.
  - Publish hidden/private, create the add-on, associate the app, install from the Store once, then debug with the same identity. Without that, calls return 0x803F6107.
  - Test purchases are real and refunded.
  - So: an `IProStore` interface with a Store implementation and a DEBUG fake (`-debugUnlockPro YES`, as on Apple and Android).
- **Privacy:**
  - Packaged apps appear individually under Settings › Privacy › Camera / Microphone.
  - Unpackaged apps fall under one "Let desktop apps access…" switch. Per-app switches for Win32 apps are in Insider builds (Aug 2026).
  - Windows shows camera and microphone in-use indicators.
- **Store listing:** a **privacy policy URL is mandatory** for Win32/Desktop Bridge apps (10.5.1). An IARC questionnaire is required (*estimate:* Everyone/3+).
- **Direct download (deferred):**
  - Signing options:
    - Azure Artifact Signing, about $9.99/mo; **individuals only in the US/CA**.
    - An OV certificate, $150–300/yr on a hardware token.
    - EV certificates no longer bypass SmartScreen (since 2024).
  - Updates: Velopack (MIT, delta updates). The `ms-appinstaller:` protocol has been off by default since Dec 2023.
  - Licences: Lemon Squeezy (5% + 50¢, merchant of record, built-in keys) or Keygen (signed offline licences).

## 11. Competitors on Windows

| App | Price | Strengths | Complaints |
|---|---|---|---|
| Windows Camera | Free, built in | Zero install | No audio playback; ~30 fps on cheap cards; little mode control |
| OBS Studio | Free (GPL), Store 4.7★ | Top Store result for every capture query | Overkill as a "monitor"; audio monitoring delay (~0.75 s reported) and drift |
| Elgato 4K Capture Utility | Free | Polished, Flashback | Elgato hardware only |
| AVerMedia RECentral | Free | Full with AVerMedia | Vendor-locked (*estimate*) |
| PotPlayer / VLC | Free | Plays audio; low latency once tuned | Complex settings; VLC caching lag |
| AmCap | ~$29 | Classic | Abandoned (2017) |
| **Capture View 4K** (Store) | Free, **3.1★ / 28 ratings** | Fast; Switch/Roku users like it | Black screen or "doesn't list my card"; crashes; washed-out colour (range); hidden key-only UI; **mic denied silently breaks capture** |
| CaptureViewer (GitHub, DShow + D3D12), itch.io viewer (Unity) | Free | Low latency | Hobby projects, no distribution |

There is **no dedicated, well-rated capture-card viewer in the Microsoft Store**. Store searches for "capture card", "hdmi capture" and "capture card viewer" return OBS, PotPlayer and screen recorders.

**What users ask for**, from Store reviews and OBS and DPReview forums:
- audio with no delay, instead of "Listen to this device" with its static and lag;
- real 60 fps (MS2109 USB 2.0 cards fake it);
- a mode and format picker;
- 4:3 and stretch fixes;
- a limited/full range toggle;
- full screen;
- one-click start.

All of these are in our free viewer already. Store-listing angle: "plug in → picture and sound, no delay, no setup".

## 12. Reuse from our codebase

Port the behaviour and keep the names, so the codebases read alike.

| Swift (Apple) | Windows (C#) | How |
|---|---|---|
| `FormatSelector` (+ Kotlin bandwidth rule) | `HDMICaptain.Core.FormatSelector` | Port; fixtures `format-selection.json`, `format-bandwidth.json` |
| `CaptureState`, backoff, Kotlin `RecoveryMachine` | `Core.RecoveryMachine` | Port the Kotlin state machine (it's already event-driven); `recovery-backoff.json`, `recovery-machine.json` |
| `SignalAnalyzer`, `SignalMonitor` | `Core` | `signal-classify.json`, `signal-monitor.json` |
| `RenderGeometry` | `Core` | `render-geometry.json` |
| `Presets`, `Profiles`, `ViewerPrefs` | `Core` + JSON prefs in LocalAppData | Same keys |
| `CubeLUT`, `TestPattern`, `Scopes` | `Core` parser / generator; scopes in a compute or pixel shader | |
| `DeviceCatalog`, `AVDeviceProvider`, `stableKey` | `Media.DeviceCatalog` (MF + CM notifications, ContainerId pairing) | |
| `CaptureEngine` | `Media.CaptureController` (one control thread) + `MfSource` / `TestPatternSource` | Same recovery rules |
| `MetalRenderer` + `Shaders.metal` | `Media.D3DRenderer` + HLSL (compiled from embedded source at runtime with `D3DCompile`, as Metal does) | Same maths |
| `AudioPipeline`, `AudioRingBuffer` | `Media.AudioPipeline` (WASAPI) | Same features, plus drift control |
| `Recorder`, `RollingRecorder`, `FrameSnapshot` | `Media.Recorder` (Sink Writer), `RollingRecorder`, WIC snapshot | |
| `LatencyProbe`, `LatencyTracker` | `Media.LatencyProbe` + flip window | For 092 |
| `EntitlementStore`, `ProFeature`, `UpgradeView` | `App.ProStore` (StoreContext) + upgrade dialog | Same feature list; streaming and virtual camera hidden |
| `StatusScreens`, `HelpSheet`, `ControlBar`, `docs/copy.md` | WinUI views + `.resw` | Same copy, Windows wording ("Settings", Ctrl shortcuts) |
| QA args (`-testPattern`, `-qaAction`, `-qaShowStats`, `-qaSoakMinutes`, `-debugUnlockPro`) | Same command-line args | DEBUG builds only |

**Dev PC** (`D:\Projects\hdmi-capture`):
- Windows 11 Pro 26200, Ryzen 7 9700X, RTX 5070 Ti plus the Radeon iGPU (hybrid, so test adapter selection).
- VS 2022 17.14 and VS 2026 18.2 (C++ and the WinAppSDK C# workload), .NET SDK 9.0.309 / 10.0.102, Windows SDK 10.0.26100.
- vcpkg (`C:\vcpkg`), CMake 4.2, Rust 1.92, `make` (Chocolatey), ffmpeg, OBS.
- USB: AMD and ASMedia xHCI plus USB4.
- No capture card was attached on 2026-10-22. The only display was the Parsec virtual display, which isn't valid for latency tests: the PC needs a real monitor for 092.

## 13. Risks

1. **`SwapChainPanel` never reaches direct scanout.** Up to one extra frame of latency. Mitigation: a replaceable video surface with an HWND fallback (088/092).
2. **Driver-specific MF behaviour** (format negotiation, MJPEG, unplug). Mitigation: test both real cards; error map in 089.
3. **Native AOT and trimming with WinUI.** Mitigation: JIT + ReadyToRun first, AOT only when clean (101).
4. **WinUI changes** (system compositor). Mitigation: pin the SDK; re-run 092's check on every bump.
5. **Store Pro testing needs a published (hidden) app.** Mitigation: the `IProStore` fake; real-purchase testing is part of release task 103.
6. **Hybrid-GPU copies** (iGPU ↔ dGPU). Mitigation: create the device on the display's adapter and recreate it when the window changes monitor (088/101).
7. **GC pauses in the frame path.** Mitigation: no allocation per frame (pooled buffers, `stackalloc`/`Span`); `GCSettings.LatencyMode = SustainedLowLatency` while live (101).

## Sources

**Capture/audio**
- MF Source Reader attributes https://learn.microsoft.com/en-us/windows/win32/medfound/source-reader-attributes
- `MF_LOW_LATENCY` https://learn.microsoft.com/en-us/windows/win32/medfound/mf-low-latency
- MediaFrameReader acquisition mode https://learn.microsoft.com/en-us/uwp/api/windows.media.capture.frames.mediaframereaderacquisitionmode
- Sharing modes https://learn.microsoft.com/en-us/uwp/api/windows.media.capture.mediacapturesharingmode
- DShow bridge / Frame Server https://learn.microsoft.com/en-us/windows-hardware/drivers/stream/dshow-bridge-implementation-guidance-for-usb-video-class-devices
- OBS win-dshow https://github.com/obsproject/obs-studio/blob/master/plugins/win-dshow/win-dshow.cpp
- Multi-app camera (KB5052093) https://pureinfotech.com/allow-multiple-apps-use-camera-windows-11/
- Video device loss https://learn.microsoft.com/en-us/windows/win32/medfound/handling-video-device-loss
- Container IDs https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-usb-devices-are-assigned-container-ids
- DXVA MJPEG https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/dxva/ns-dxva-dxva_picparams_mjpeg
- libjpeg-turbo performance https://libjpeg-turbo.org/About/Performance
- Camera privacy https://learn.microsoft.com/en-us/windows/apps/develop/camera/camera-privacy-setting
- MS2109 audio quirk https://lkml.iu.edu/hypermail/linux/kernel/2007.1/07921.html · https://github.com/yume-chan/ms2109-player
- Low-latency audio https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio
- `AUDCLNT_STREAMFLAGS` https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants
- OBS monitor drift https://obsproject.com/forum/threads/audio-drift-when-using-monitor-only.164590/
- `SetProcessInformation` https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
- `timeBeginPeriod` https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod

**Stack/render**
- WinAppSDK release channels https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels
- WinAppSDK 2.0 notes https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0
- WinAppSDK 1.6 / AOT https://blogs.windows.com/windowsdeveloper/2024/09/04/whats-new-in-windows-app-sdk-1-6/
- Build 2026 WinUI https://developer.mescius.com/blogs/microsoft-recommitted-winui-what-build-2026-means-for-windows-developers
- winapp CLI https://blogs.windows.com/windowsdeveloper/2026/01/22/announcing-winapp-the-windows-app-development-cli/
- `SwapChainPanel` https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel
- DXGI flip model https://devblogs.microsoft.com/directx/dxgi-flip-model/ · https://learn.microsoft.com/en-ie/Windows/Win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model
- Composition swapchain https://learn.microsoft.com/en-us/windows/win32/comp_swapchain/comp-swapchain
- NVIDIA swap chains https://developer.nvidia.com/blog/advanced-api-performance-swap-chains
- windows-rs https://github.com/microsoft/windows-rs
- windows-reactor https://crates.io/api/v1/crates/windows-reactor
- Kerr comparison https://gist.github.com/kennykerr/1b52f354496042b138abd835277268dd
- uniffi-bindgen-cs https://github.com/NordSecurity/uniffi-bindgen-cs/releases

**Distribution/encode/competitors**
- Free Store registration https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/
- Win32 in the Store https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store
- Store policies https://learn.microsoft.com/en-us/windows/apps/publish/store-policies
- Code signing https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options
- In-app purchases https://learn.microsoft.com/en-us/windows/uwp/monetize/in-app-purchases-and-trials
- Invalid window https://learn.microsoft.com/en-us/answers/questions/2201096/how-to-fix-hresult-0x80070578-invalid-window-excep
- `MFCreateVirtualCamera` https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera
- VirtualCamera sample https://github.com/microsoft/Windows-Camera/tree/master/Samples/VirtualCamera · https://github.com/smourier/VCamSample · https://github.com/tshino/softcam
- Velopack https://docs.velopack.io/packaging/deltas
- FFmpeg legal https://www.ffmpeg.org/legal.html
- NVENC MFT latency https://forums.developer.nvidia.com/t/nvenc-h-264-encoder-mft-latency-increases-when-framerate-is-limited/63930
- Capture View 4K https://apps.microsoft.com/detail/9n9tlrjh659t
- Elgato devices https://help.elgato.com/hc/en-us/articles/360060805031-Elgato-4K-Capture-Utility-Supported-Capture-Devices
- OBS audio delay https://obsproject.com/forum/threads/please-help-how-to-get-nintendo-switch-audio-to-go-through-my-headphones-with-no-delay-while-streaming.122339/
- DPReview MS2109/MS2130 https://www.dpreview.com/forums/threads/tip-record-hdmi-camera-output-for-15.4766056
- Per-app privacy https://www.windowscentral.com/microsoft/windows-11/windows-11-will-soon-let-you-block-camera-and-mic-access-for-any-individual-desktop-app
- Lemon Squeezy https://www.lemonsqueezy.com/pricing · Keygen https://keygen.sh/pricing/
