# OSS review: Dongled and Consolation (task 044)

Reviewed 2026-10-20 from public GitHub sources only. No binaries were inspected. Everything below describes what the code does; no code was copied.

## Summary

| App | App Store | Repo | License | Status |
|---|---|---|---|---|
| Dongled (oddforms, v1.5) | id6465788521 | https://github.com/oddforms-design/Dongled (tag `v1.5.0`, last commit `fc54dd2`, 2026-07-10) | MIT, © 2025 Oddforms Design LLC, plus a trademark notice for the name and logo | **Confirmed.** The README links to this App Store id. GitHub's API reports the license as `NOASSERTION` only because a trademark addendum follows the MIT text. The MIT text itself is standard. |
| Consolation | id1563856788 | https://github.com/centennial-oss/consolation-apple (last commit `e081cb4`, 2026-10-18) | Apache-2.0, with a `NOTICE` file (© 2026 The 'Consolation' Authors / Centennial OSS Inc.) | **Confirmed.** The README links to this App Store id. Sibling repos exist for Android, Windows and Linux. |

Note: the repo is under the `oddforms-design` org, not `oddforms`.

## Dongled

Dongled is a small UIKit iPad app (about 1,200 lines of Swift). It runs on the Mac as a **"Designed for iPad" app**, with `SUPPORTS_MAC_DESIGNED_FOR_IPHONE_IPAD = YES` and `SUPPORTS_MACCATALYST = NO`. It is not a native AppKit app.

- **Device discovery and hot-plug** (`Dongled/CaptureManager.swift`, `Dongled/ViewController.swift`)
  - Uses `DiscoverySession([.external])`.
  - On connect, it waits a fixed **2.2 s "boot" delay**, then checks the device is still present before configuring it.
  - On the Mac it de-duplicates devices by `modelID`, auto-selects when there is only one, and otherwise shows a picker.
  - It tracks device IDs so it can ignore duplicate connect notifications and disconnect notifications for devices it isn't using.
  - Before calling `startRunning` it confirms every input is still connected.
- **Format selection** (`selectBestFormat`, Mac only, with the `.inputPriority` preset)
  1. Prefer **16:9** modes. The code comment says dongles advertise 1920×1200-style modes that out-score 1080p but squash a 16:9 source.
  2. Then pick the highest pixels × max fps.
  3. Break ties by FourCC rank: x420 > 420v > 420f > 2vuy > yuvs/yuv2 > v210 > BGRA > H.264/HEVC > MJPEG.
  - It locks min and max frame duration to the range's `minFrameDuration`, which avoids NTSC rounding errors.
- **Audio** (`Dongled/AudioManager.swift`)
  - Uses `AVAudioEngine` input→mainMixer pass-through, driven by `AVAudioSession` (`.playAndRecord`, `.mixWithOthers`). This works because it is an iOS app.
  - It picks the USB-audio port and filters out names containing "Display", because monitors show up as USB audio. If there are several candidates it asks the user.
  - It uses the hardware format from `inputNode.outputFormat`.
  - There is no A/V sync logic, no delay and no meters.
- **Lifecycle: the focus-loss disconnect**
  - `ViewController` observes `UIApplication.didEnterBackgroundNotification` and calls `teardownSession()`, which stops the session, removes all inputs and outputs and stops audio. It sets `needsSessionRestart`.
  - On `didBecomeActive` it re-runs the whole authorize, discover and 2.2 s boot sequence.
  - Connect events that arrive while the app is not `.active` are deferred.
  - When an iPad app runs on the Mac, the window losing focus or being hidden maps onto these UIKit lifecycle transitions, so Dongled deliberately kills capture and rebuilds it. That fits the observed "disconnect on focus loss".
  - There is no `AVCaptureSessionWasInterrupted` or `.runtimeError` handling.
- **Rendering**
  - Uses `AVCaptureVideoPreviewLayer` (`.resizeAspect`). There is no Metal.
  - On the Mac it disables mirroring.
  - It applies a **180° rotation on OS version 27 and later**: the comment says "macOS 27 delivers the external stream rotated 180°". This may be specific to the iPad-on-Mac path.
- **Other**
  - Keeps the display awake with `ProcessInfo.beginActivity(.idleDisplaySleepDisabled)`.
  - Hides the window chrome by poking `NSWindow` through KVC from UIKit.
  - There is no no-signal detection.

## Consolation

Consolation is a native SwiftUI multiplatform app (macOS 15 and later, iPadOS 18 and later). The core code is under `Consolation/Capture/`.

- **Session architecture** (`Session/CaptureSessionBackend.swift`, `CaptureSessionManager.swift`)
  - A Swift `actor` serializes all session configuration and start/stop calls.
  - A `@MainActor @Observable` manager publishes state (`idle/requestingPermission/running/noDevice/failed`).
  - One shared `AVCaptureSession` feeds the preview layer.
- **Format selection** (`Video/CaptureVideoFormatConfiguration.swift`)
  - The default rule is a minimum of 60 fps: filter to formats at or above 60 fps (minus 0.5 tolerance), then those at the highest fps, then the widest resolution.
  - An explicit width × height × fps choice is stored per device.
  - Exact frame durations use a timescale of 60000.
  - **Important:** it re-applies the format and frame duration *after* `startRunning()`. The comment says the UVC driver resets both when the session starts, on macOS and on iPadOS.
- **Audio** (`Audio/CaptureAudioPlayback.swift`, `CaptureAudioDeviceSelection.swift`)
  - `AVCaptureAudioDataOutput` feeds an `AVAudioEngine` `AVAudioPlayerNode`. The authors chose this over `AVCaptureAudioPreviewOutput`, because the preview output exists only on macOS.
  - On the Mac it forces `audioSettings` to 32-bit float, non-interleaved PCM. The comment says this avoids garbled integer and interleaved formats.
  - The engine is wired lazily from the first buffer's format.
  - Latency is bounded by capping the number of pending scheduled buffers (options 1–64, default 8). When the cap is reached, new buffers are dropped.
  - Mute works by **not scheduling** buffers. The comment says mixer volume is unreliable on some macOS routes.
  - On iPad, the engine is rebuilt when the format changes.
  - The audio device is found by matching names: exact, then substring, then token overlap with the video device name. It excludes built-in, AirPods, Continuity and Studio Display mics, and returns none rather than guess.
- **Hot-plug** (`Session/CaptureSessionManagerVideoHardware.swift`)
  - Uses async `NotificationCenter` streams for `wasConnected` and `wasDisconnected`.
  - If the active device disappears it stops capture and goes to `noDevice` or `idle`.
  - **It does not auto-restart when the device is plugged back in.**
- **Lifecycle**
  - `scenePhase` only refreshes the permission state. Capture is **not** torn down on focus loss or backgrounding.
  - There is no sleep/wake or interruption handling.
  - A wake lock (`.idleDisplaySleepDisabled` + `.idleSystemSleepDisabled`) is held while the app is running (`Helpers/PlaybackDisplayWakeLock.swift`).
- **Signal health**
  - It has no no-signal detection.
  - A `AVCaptureVideoDataOutput` frame monitor (`Video/CaptureVideoFrameRateMonitor.swift`) reports, once per second: wall fps, presentation-timestamp fps, frames dropped through `didDrop`, and the largest presentation gap.
  - A low-FPS warning appears after 3 consecutive one-second samples that are more than 10 frames short.
  - `Device/CaptureDeviceCompatibilityIssues.swift` keeps a known-issue table. For example, the MacroSilicon 2109 runs internally at 30p and duplicates frames to present "1080p60"; the workaround is 720p.
- **Rendering and window** (`Components/Viewer/MacPreviewView.swift`, `Views/ContentViewWindowMac.swift`)
  - `AVCaptureVideoPreviewLayer` in an `NSView`, without Metal.
  - Zoom and pan work by scaling the layer's frame.
  - Per-device rotation and mirroring settings.
  - `window.contentAspectRatio` is set to the video size, and re-applied after leaving full screen.
  - An always-on-top `.floating` level.

## Learnings mapped to our tasks

- **006 capture engine**
  - Re-apply the format and frame duration after `startRunning()` (Consolation).
  - Prefer 16:9 so the dongle's 16:10 scaler modes aren't chosen (Dongled).
  - Rank FourCCs with 420v or x420 first.
  - Lock frame durations using the range's own `minFrameDuration`.
  - Serialize session changes through an actor.
- **031 audio pipeline**
  - On the Mac, request float32 non-interleaved PCM from `AVCaptureAudioDataOutput`.
  - Bound the player-node queue so latency doesn't grow.
  - Mute by dropping buffers rather than through mixer volume.
  - Pair audio to video by name matching, and blocklist display and built-in mics.
  - Neither app does A/V delay or meters, so our 031 already goes further.
- **032 signal status**
  - Neither app detects no-signal or HDCP, so we are ahead.
  - Worth adopting: Consolation's comparison of delivered fps with presentation-timestamp fps, the max-gap and dropped-frame stats, and the known-chipset table for the MS2109 fake-60p case. These belong in our requested-vs-actual display.
- **007 always-on resilience**
  - Dongled shows what to avoid: tying capture to app active/background state causes the focus-loss disconnect. We should never tear down on focus or occlusion.
  - Both apps lack interruption, sleep/wake and auto-reconnect handling. Our wasConnected → re-open-by-uniqueID path is a differentiator.
  - A settle delay after connect (Dongled uses 2.2 s) is worth considering.
- **008 renderer**
  - Both apps use only `AVCaptureVideoPreviewLayer`, so our Metal path has no OSS reference here.
  - Dongled's comment about a 180° rotation on 27-series OS versions is a quirk to test for.
- **033 window/menu bar**
  - Use `contentAspectRatio` and re-apply it after leaving full screen.
  - Offer an always-on-top floating level.
  - Use a power assertion while capturing.
- **018/034 multi-view**
  - Neither app supports multiple devices at once.
  - Dongled's de-duplication by `modelID` hints that some dongles expose duplicate device entries.

## License obligations if we reuse code

- **Dongled (MIT):** keep the copyright notice and the MIT text in the app (for example, an Acknowledgements screen) for any substantial portion we reuse. We may not use the "Dongled" name or logo.
- **Consolation (Apache-2.0):** include the Apache-2.0 text, keep the relevant contents of `NOTICE` in our attribution ("Copyright 2026 The 'Consolation' Authors / Centennial OSS Inc."; "includes software developed by James Ranson"), and mark any modified files as changed. The patent grant ends if we sue over patents. We may not use the Consolation trademark.
- Both licenses are compatible with a closed-source paid App Store app. Our current plan is to reimplement behaviors rather than copy code, which creates no obligations. Add attribution only if snippets are actually copied.
