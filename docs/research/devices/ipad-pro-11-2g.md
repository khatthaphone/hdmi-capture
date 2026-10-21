# iPad Pro 11" (2nd gen, iPad8,9, A12Z) — iPadOS 18.7.3

Tested 2026-10-21 with the **UGREEN-25854** card and a PS5 (HDCP off), using the iPad's single USB-C port (later through a charge-through adapter).

## Formats (`AVCaptureDevice.formats` on iPadOS)

| Size | iPadOS | macOS (`ugreen-25854.json`) |
|---|---|---|
| 640×480 … 1920×1080 (10 sizes, identical list) | `420f` @ 60 | `420v` @ 60/30/15 |

**Differences from the Mac:**
- iPadOS delivers **full-range `420f`**; macOS delivers video-range `420v`. This matches Apple's note that iPadOS converts the card's MJPEG output to 420f. `MetalRenderer` handles both (`ColorRange.auto`).
- iPadOS lists each size with **60 fps only**. macOS also lists 30 and 15 fps ranges.
- The auto format picks 1920×1080@60 on both platforms.

## Capture and display

- Live 1920×1080 420f at the source rate: 59 fps with the PS5 at 60 Hz, 49.8 fps when the PS5 output 50 Hz.
- Latency: capture 14–19 ms, on screen ≈ 28–36 ms (p95 41–60 ms). The Mac with the same card measured similar numbers.
- Audio: the route input is the card's USB audio (`AVAudioSession` port `.usbAudio`, name `UGREEN-25854`). The built-in mic is never used. The steady-state output buffer is 18 ms.
- Multitasking camera access: **not supported** on this iPad (A12Z). In Split View or Slide Over the app shows the Paused screen and resumes in full screen (confirmed by the user).

## 30-minute soak

Raw data: [`../soak/ipad-pro-11-2g-ugreen-25854-2026-10-21.csv`](../soak/ipad-pro-11-2g-ugreen-25854-2026-10-21.csv) (`-qaSoakMinutes 30`, one line a minute).

| Metric | Result |
|---|---|
| State | live for all 30 min, no restarts |
| Dropped frames | 4 during the first seconds, then 0 for 30 min |
| Memory (phys_footprint) | 80.2–85.0 MB (±3% around 82 MB) |
| Thermal state | `nominal` throughout (charging, screen on) |
| Audio buffer | constant 18 ms, so the drift controller kept sync (no audible drift) |
