# ADR 0006 — Linux stack

- **Status:** accepted for development (2026-10-23); the live-capture parts are re-checked when spikes 108/111 run on the card (WSL via usbipd, then a real install)
- **Task:** 112 · spec §14 · research `docs/research/linux.md`

## Context

HDMI Captain needs a Linux viewer with the same product scope as Windows: a free viewer plus Pro recording and Save Recent.
Development happens on the Windows dev PC; the user wanted the UI live from WSL. We already had a pure C# core
(`Apps/Windows/src/HDMICaptain.Core`, 160 tests), a GLES 3 renderer in C++ (Android), and the Windows engine
design (controller, recovery machine, audio drift control).

## Decision

| Area | Choice | Evidence so far |
|---|---|---|
| Language, UI | C# .NET 10 + GTK 4 / libadwaita through Gir.Core 0.8.1 | Builds and runs on Ubuntu 24.04's GTK 4.14 / Adw 1.5 in WSLg; status pages, control bar, settings, scopes, grid all work. Gaps worked around with small P/Invokes: accessible labels (`gtk_accessible_update_property_value`), X11 keep-on-top (EWMH), the GL clear in the grid |
| Pure logic | `HDMICaptain.Core` referenced from `Apps/Windows`, not moved | All 160 Core tests pass on Linux unchanged (one fixture-path fix); one shared controller tuning knob added (`DriftController` gain scale, default 1) |
| Render | The Android GLES 3 pipeline extracted to `native/render-gles` (`GlPipeline` + C ABI), drawn in a `GtkGLArea` | GLES 3.1 on Mesa d3d12 (RTX 5070 Ti) in WSLg; pixel tests on EGL surfaceless; 60 fps patterns, arrival→draw ≤ 1.2 ms. The look is baked into the source when set (`lookInSource`), matching Windows/Mac outputs. Android still has its own copy until it is rewired on the Mac |
| Capture | Raw V4L2 through a C shim (`native/linux-media`, kernel headers) + C# sysfs identity | Struct sizes checked by test; enumeration, newest-frame drain, errno mapping and the controller (13 virtual-time tests) done. **Live streaming not yet measured** (no usbipd yet) |
| MJPEG | System libturbojpeg, TurboJPEG 2 API (Ubuntu ships 2.1.5) | JFIF full-range BT.601 decode into renderer planes verified by pixel test |
| Audio | ALSA capture (`plughw`) or Pulse capture, Pulse playback, shared ring/drift/resampler | WSLg sink runs ~600 ppm fast; the loop holds 40 ms with 0 underflows (WSL gains ×4). **Card capture not yet measured** |
| Recording | ffmpeg child process, encoder probe NVENC → VA-API → x264 → OpenH264 | NVENC picked in WSL; 3 s H.264 + AAC test passes. Recordings don't carry the look yet |
| Pro | Licence key outside the store, 30-day offline grace | Store, policy, gate and UI done with a fake service; **vendor is the user's decision** (task 119 blocked) |
| Packaging | Flatpak (main channel) + .deb/.rpm with the udev rule + .tar.gz | .deb installs and runs in WSL; Flatpak manifest written, not built yet |
| Dev loop | WSL2 + WSLg, `make linux-*` from Git Bash, outputs on ext4 | Build 1.3 s warm; windows are msrdc RAIL windows, PrintWindow works |

## Alternatives considered

- **Avalonia 12:** cross-platform C#, but Wayland is experimental/opt-in and it doesn't look native on GNOME.
- **Qt 6 / QML:** solid video stack, but a C++ app sharing nothing with our C# code.
- **Rust + gtk4-rs:** the best GTK bindings, but a fourth port of the pure logic.
- **PipeWire camera portal / GStreamer `v4l2src` for capture:** more sandbox-friendly or less code, but less control over buffers, formats and errors; the portal stays a later second backend.
- **libav via P/Invoke for recording:** lower overhead, but tied to distro FFmpeg ABI versions; the process boundary is simpler and keeps LGPL obligations clear.

## Consequences

- Two native libraries to build per architecture (CMake from the Media csproj); arm64 needs its own runs.
- Gir.Core is pre-1.0: pin versions, keep P/Invoke shims small and documented.
- WSLg is not a Linux desktop: latency, soak, portals, suspend and Wayland window behaviour are signed off on a real install (task 124).
- If usbip can't stream the cards reliably (task 108), WSL stays the loop for everything except live capture.
