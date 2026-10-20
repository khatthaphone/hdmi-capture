# HDMI Captain

Native HDMI-capture viewer for macOS (iPadOS/Android/Windows later; mini-OBS later). Spec: `docs/spec.md`. Task plan: `docs/tasks/000.INIT.md` — follow its "How to work this plan" loop and keep statuses in sync.

## Layout

- `Packages/CaptureCore/` — Swift package shared by Apple targets: device discovery (`DeviceCatalog`, `AVDeviceProvider`), pure format choice (`FormatSelector`), `CaptureEngine` (AVCaptureSession + always-on recovery), `MetalRenderer` (CAMetalLayer, YCbCr→RGB shader compiled at runtime from source), `Presets`.
- `Apps/macOS/Sources/` — SwiftUI shell: `AppModel` (policy/orchestration), `MetalLayerView`, `WindowConfigurator`, `SettingsView`, menu commands in `HDMICaptainApp.swift`.
- `tools/device-inventory/` — CLI that lists capture devices/formats; outputs saved in `docs/research/devices/`.
- `project.yml` — XcodeGen; `HDMICaptain.xcodeproj` is generated and gitignored.

## Commands

- `make build` — xcodegen + xcodebuild (Debug, DerivedData in `.build/xcode`)
- `make run` — build and open the app
- `make test` — `swift test` in CaptureCore (Swift Testing)
- `make inventory` — run device-inventory
- `/usr/bin/log stream --level debug --predicate 'subsystem == "hdmicaptain"'` — engine logs (in zsh, `log` is a builtin; use the full path)

## Testing without hardware

- `open "<app>" --args -testPattern smpteBars|fullBars|grid|grayRamp` starts on a generated pattern.
- DEBUG builds: `--args -qaAction screenshot|copy|actualPixels|size50|record3s|openCards` (record3s needs a live device; openCards opens a window per card) runs the action 1.5 s after launch.
- Window screenshot: get the window id via `CGWindowListCopyWindowInfo` (owner "HDMI Captain", layer 0), then run `screencapture -o -x -l <id> out.png`.
- `make test-app` runs hosted tests: StoreKit, against `Apps/macOS/HDMICaptain.storekit`.

## Conventions

- Swift 5 language mode (AVFoundation delegate/queue model); `@MainActor @Observable` for UI-facing models.
- Capture config runs on `CaptureEngine`'s session queue; frames are rendered directly on the capture queue (no main-thread hop).
- Never stop the capture session for focus/Space/occlusion changes — that is the core requirement (spec §5.3).
- Signing: `Config/Signing.xcconfig` defaults to ad-hoc; local identity in gitignored `Config/Local.xcconfig`.
- Never decrypt or decompile Dongled (FairPlay); clean-room behaviour only.
