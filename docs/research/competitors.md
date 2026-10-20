# Competitor research — HDMI capture viewers & light streaming apps (Mac App Store)

_2026-10-20. Sources: App Store listings (`apps.apple.com/us/app/<name>/id<ID>`), iTunes lookup API, developer sites, a few forum posts. Most apps are 2025–26 releases with no reviews; latency claims are vendor claims. Not found: ACHT – Video Assist, CaptureDive, Connect HDMI._

## Landscape

| App (App Store ID) | Price | Native Mac? | Standout | Top complaints |
|---|---|---|---|---|
| Screenjack (6806005504) | $9.99 | Mac + iPad | Late-frame drop; fit/fill/stretch/**integer**; fixes bad stick audio (mono 96 kHz, **L/R swap**); last-device reconnect; **no-signal / HDCP / permission screens**; limited/full range; no recording on purpose | — |
| SyncZero (6773650015) | $3.99 | Mac + iPad | Requested vs **actual** resolution; aspect/rotate; image presets (Game/Office/Cinema/FPS) + brightness/contrast/saturation/sharpen; per-device memory | — |
| Console Link (6448843682) | $7.99 | Mac + iPad + Vision Pro | Display-synced low-latency mode; upscaling; frame generation; 4K120; color space; 21:9/32:9; perf overlay | Input lag, freezes, black borders |
| CaptureLink (6768070434) | Free / VIP sub | Mac/iPad/iPhone/AVP | HEVC/ProRes recording, instant replay, RTMP/SRT/WebRTC, multi-view, PiP | — |
| HDMI Bar (6766368499) | $4.99 | Mac-only | Always-on-top PiP on double-click; H.264 recording; **hold last frame on dropout**; frame-buffer setting | — |
| HDMI Link (6755496628) | $2.99 | Mac/iPad/AVP | Custom crops (21:9), presets, audio input picker, 3D SBS on Vision Pro | No audio on AVP |
| Consolation (1563856788) | Free, OSS (Apache-2.0) | Mac + iPad (SwiftUI/AppKit) | Mirror/rotate, perf stats, low-fps warning, menu-bar item; also Android/Windows/Linux | — |
| CapturePlay (6755561036) | Free | Mac-only | MOV recording, screenshots, **route audio to any output**, per-device image adjust, prevents sleep, hides cursor | — |
| Capture Passthru (1610925225) | $0.99 | Mac-only | Separate A/V source pick; format mode: Default / Best Quality / **Best Performance** | Crash, memory leak (fixed) |
| UVC Capture (6466091161) | Sub $1.99/mo | Mac/iPad/AVP | Photo+video capture, PiP | **Subscription**, recordings with no sound, record button can't be hidden |
| Console Capture (6748866452) | Free | Mac/iPad/AVP | Metal render, any card mode | **Freezes/crashes, black video, no fullscreen** (4.3★) |
| Dongled (6465788521) | Free (listed as OSS, MIT — verify) | iPad-on-Mac | Plug-and-play | "Close and reload if image/sound isn't right"; past A/V sync & hot-plug black-screen fixes; **our Mac disconnect bug** |
| Sixty Capture Control (6817503320) | $1.99 | Mac-only | HD60 X only: **HDR PQ/HLG detection**, HDR→SDR, EDID, menu bar | — |
| NDI Source — Sienna (1097524095) | $99.99 | Mac-only | Blackmagic → NDI | Blackmagic-only |
| CaptureLinker / Castaway | Free + Pro | Mac/iPad → Vision Pro | Stream HDMI to Vision Pro (4K, 120 fps) | Latency, no audio, grainy |
| Livedesk Studio (1217841826) | Sub / lifetime | Mac/iPad/iPhone | Multistream RTMP, auto-reconnect, adaptive bitrate, overlays | — |
| TopDirectorTitle (6798894839) | Free + IAP | Mac/iPad | Lower thirds/scoreboards/timers as browser sources for OBS | — |

Pro reference (iPad only): **Video Assist** (Auron, $129–199) — LUTs, peaking, false color, zebras, frame guides, desqueeze, ProRes, NDI.

## Feature tiers

**Table stakes:** auto device detection + format pick · HDMI audio with volume/mute · fit/fill/stretch + full screen with auto-hide UI · keep display awake · basic signal/fps readout · "low latency".

**Differentiators (few have, users value):** integer scaling · broken-audio fixes & L/R swap · explicit no-signal/HDCP screens + last-device reconnect · latency vs quality format mode · per-device memory · always-on-top/PiP · menu-bar mode · screenshot/recording · image adjust presets · color range/space · audio output routing · upscaling/frame-gen · Vision Pro.

**Pro/niche:** HDR detect/convert, EDID · scopes/LUTs/peaking/guides · NDI · streaming/replay/multi-view · titles · virtual camera (no capture viewer has one) · test patterns (none).

## Gaps nobody fills

1. **Measured latency** — everyone claims it, no one measures or shows it.
2. **A/V sync control** — no manual audio delay or USB clock-drift correction; audio complaints across Dongled, Orion, Genki, UVC Capture.
3. **Reliability across sleep / standby / hot-plug** — freezes and "no signal until replug" are common.
4. **Truly native Mac UX** — menu bar + float + size presets + shortcuts + multi-display; feature-rich rivals are iPad-shared builds.
5. **Game-console UX** — per-console profiles, auto-switch, unobtrusive recording.
6. **Signal truth** — real source mode/HDR/range vs. what the card delivers.
7. **Pro monitoring on Mac** — scopes/guides/test patterns exist only on iPad.
8. **One path viewer → record → stream** — users bounce to OBS.
9. **Fair pricing** — subscriptions are the #1 review complaint.
10. **Multiple cards at once** — only CaptureLink has multi-view.

## Where HDMI Captain stands (v0.1)

**Already have:** auto-detect + format pick (16:9-aware) · paired HDMI audio, volume, mute · fit/fill/stretch/**integer**, sharp/smooth · limited/full range · full screen, cursor auto-hide, double-click · keep display awake · stats overlay · per-device presets · auto-reconnect after errors, stalls, sleep, replug · **never disconnects on focus/Space change** · float on top, all Spaces, clean mode · device switching.

## Recommended additions (priority order)

| # | Feature | Why | Size |
|---|---|---|---|
| 1 | **Audio delay (ms) + L/R swap + mono→stereo fix** | Gap #2; most common audio complaint | M |
| 2 | **Signal screens**: no-signal / HDCP-black detection, hold last frame on short dropouts, "requested vs actual" mode | Screenjack/HDMI Bar parity; gap #6 | M |
| 3 | **Screenshot (⇧⌘S) + recording (⌘R)**, hideable controls | Table stakes for many; existing tasks 015/016 | M |
| 4 | **Window size presets** (50/100/150/200 %, 1:1 pixel) + **menu-bar extra** | Gap #4, cheap native win | S |
| 5 | **Multi-view**: window per card and a 2×2 grid | Gap #10; you already own two cards; task 018 | M |
| 6 | **Built-in latency test** (Mac HDMI-out → card → flash detection, shows ms) | Gap #1, unique, marketing claim backed by numbers | M |
| 7 | **Image adjust + presets** (brightness/contrast/saturation/sharpen; Game/Cinema) | SyncZero/CapturePlay parity | M |
| 8 | **Console profiles**: name + icon per card/mode, auto-apply | Gap #5 | M |
| 9 | **Virtual camera** (CMIO extension) | No viewer has it; pairs with mini-OBS | L (task 026) |
| 10 | **Streaming + replay buffer** | Gap #8; mini-OBS phase | L (tasks 022–025) |
| 11 | Pro: test patterns, waveform/vectorscope, frame guides, LUT | Gap #7, niche | L |

**Pricing hint:** a one-time purchase (or free viewer + one-time Pro unlock for recording/streaming/virtual camera) stands out against the subscription-heavy competitors.

**Follow-up:** Dongled is listed as open source (MIT). If that's confirmed, its source can be read legally, which beats behavioral cloning. Consolation (Apache-2.0) is also worth reading for cross-platform ideas.
