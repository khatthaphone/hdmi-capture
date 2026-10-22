# HDMI Captain

Native HDMI-capture viewer for macOS (iPadOS/Android/Windows later; mini-OBS later). Spec: `docs/spec.md`. Task plan: `docs/tasks/000.INIT.md` — follow its "How to work this plan" loop and keep statuses in sync.

## Layout

- `Packages/CaptureCore/` — Swift package shared by Apple targets: device discovery (`DeviceCatalog`, `AVDeviceProvider`), pure format choice (`FormatSelector`), `CaptureEngine` (AVCaptureSession + always-on recovery), `MetalRenderer` (CAMetalLayer, YCbCr→RGB shader compiled at runtime from source), `Presets`.
- `Apps/Shared/` — app layer compiled into both apps: `AppModel` (policy/orchestration), `ViewerModel`, `DeviceSession`, prefs, Pro store, `ContentView`, `ControlBar`, shared menu items. Platform-only code sits inline in `#if os(macOS)` / `#if os(iOS)`.
- Targets: `HDMICaptain` (Mac, App Store), `HDMICaptain-Direct` (same Mac app + Sparkle, `DIRECT_DISTRIBUTION`, for the notarized DMG; build it with its own `-derivedDataPath .build/xcode-direct` because the product name is the same), `HDMICaptain-iPad`. Release tooling: `tools/release/`, `docs/release.md`.
- `Apps/macOS/Sources/` — Mac shell: `MetalLayerView` (NSView), `WindowConfigurator`, `SettingsView`, Multi-View, menu-bar extra, streaming, menu commands in `HDMICaptainApp.swift`.
- `Apps/iPadOS/Sources/` — iPad shell (iPadOS 17+, USB-C iPads): App struct, `MetalLayerView` (UIView) with the same `MetalPreview` API.
- `Apps/Android/` — Android app (spec §12, plan tasks 069–085): Gradle, AGP 9 + Kotlin 2.4 + Compose, compileSdk 37 / targetSdk 36 / minSdk 29, NDK 28.2. Modules `app`, `feature:{viewer,settings}`, `core:model` (pure JVM: ports of CaptureCore's platform-free logic), `core:{capture,render,audio,billing}`. `core:render` is native (CMake, GLES 3); libraries must stay 16 KB page aligned.
- `Apps/Windows/` — Windows app (spec §13, plan tasks 086–104): .NET 10 solution `HDMICaptain.Windows.slnx`, central package versions in `Directory.Packages.props`. `src/HDMICaptain.Core` (net10.0, pure ports of CaptureCore logic + `LaunchOptions`), `src/HDMICaptain.Media` (MF capture, D3D11 render, WASAPI; interop via CsWin32 `NativeMethods.txt` + Vortice), `src/HDMICaptain.App` (WinUI 3 / Windows App SDK 2.5, self-contained; Debug runs unpackaged, Release is an MSIX), xUnit v3 tests on Microsoft.Testing.Platform (opted in via `Apps/Windows/global.json`, so run `dotnet` from `Apps/Windows`). `native/vcpkg.json` builds libjpeg-turbo; licences in `THIRD-PARTY.md`.
- `tools/device-inventory/` — CLI that lists capture devices/formats; outputs saved in `docs/research/devices/`.
- `project.yml` — XcodeGen; `HDMICaptain.xcodeproj` is generated and gitignored.

## Commands

- `make build` — xcodegen + xcodebuild (Debug, DerivedData in `.build/xcode`)
- `make run` — build and open the app
- `make test` — `swift test` in CaptureCore (Swift Testing)
- `make build-ios-core` / `make test-ios-core` — CaptureCore for iOS (device build / iPad simulator tests; `IOS_SIM=` picks the simulator). Use `#if os(macOS)` for macOS-only APIs and `#if !targetEnvironment(simulator)` for device-only ones (e.g. `addPresentedHandler`)
- `make android-build` / `make android-test` / `make android-run ANDROID_ARGS="--es testPattern smpteBars"` — Android (JDK 21 from Android Studio's JBR; the system JDK 26 is too new). If the Gradle wrapper can't download its distribution, pass `GRADLE=/path/to/gradle-9.8.2/bin/gradle`. Check 16 KB alignment with `zipalign -c -P 16 -v 4 <apk>`. Debug on a phone over wireless adb (`adb pair`/`adb connect`), because the card holds the USB-C port
- `make win-build` / `make win-test` / `make win-run WIN_ARGS="-testPattern smpteBars"` / `make win-package` — Windows, from GNU make in Git Bash on the Windows dev PC (`WIN_PLATFORM=ARM64` for Arm). `win-deps` builds libjpeg-turbo with vcpkg (`VCPKG=` overrides `C:/vcpkg/vcpkg`). `win-package` signs a sideload MSIX with a self-signed `CN=HDMI Captain Dev` cert (created by `tools/new-dev-cert.ps1`, PFX copy in gitignored `Apps/Windows/.local/`); trust it once from an elevated prompt with the command the script prints
- `make inventory` — run device-inventory
- `/usr/bin/log stream --level debug --predicate 'subsystem == "hdmicaptain"'` — engine logs (in zsh, `log` is a builtin; use the full path)

## Testing without hardware

- `open "<app>" --args -testPattern smpteBars|fullBars|grid|grayRamp` starts on a generated pattern.
- DEBUG builds: `--args -qaBuiltInCameras YES` / `-qaShowStats YES` override prefs in memory only. **Never edit the prefs plist for QA** (cfprefsd caches and can write stale values back).
- DEBUG builds: `--args -qaAction screenshot|copy|actualPixels|size50|fullScreen|record3s|openCards|grid|scopes|stream10s|recent15` (recent15 keeps 25 s then saves the last 15 s; stream10s needs `-qaStreamURL rtmp://…`; record3s needs a live device; openCards opens a window per card; grid opens Multi-View) runs the action 1.5 s after launch.
- DEBUG builds: `--args -qaPinControls YES` keeps the hover control bar visible (combine with any `-qaAction`).
- DEBUG builds: `-qaSoakMinutes 30` logs fps/drops/memory/thermal/audio buffer once a minute to `Documents/qa-soak.csv` (+ `qa-formats.txt`); `-debugUnlockPro YES` unlocks Pro in memory.
- iPad (device): `make run-ipad IPAD_ARGS="…"` builds, installs and launches over the network; `make ipad-snapshot` saves the window + raw frame (DEBUG Darwin-notification hook) to `.build/ipad-snapshot/`; `make ipad-soak` copies the soak CSV. iPad-only QA action: `-qaAction settings`. The iOS simulator's `simctl launch` hangs for our app in this Xcode (see task 051), so use the device.
- Window screenshot: get the window id via `CGWindowListCopyWindowInfo` (owner "HDMI Captain", layer 0), then run `screencapture -o -x -l <id> out.png`.
- Windows: the same launch arguments work (`make win-run WIN_ARGS="-testPattern grid -qaShowStats YES"`); `-qa*` and `-debugUnlockPro` only in Debug. Windows `-qaAction` list: screenshot, copy, size50, actualPixels, fullScreen, settings, scopes, upgrade, record3s, recent15, openCards, grid (with `-testPattern` the grid shows the four patterns as tiles), patternCycle (Windows only: bars for 3 s, then the card is reopened). Windows `-qaPhase welcome|scanning|permission|connecting|waiting|noSignal|black|reconnecting|disconnected|inUse|sleep|privacyOff|failed` shows that status screen without opening a card (screenshots, Narrator checks). Window screenshot: `PrintWindow` on the `HDMICaptain` process's main window (PowerShell + System.Drawing).
- `make test-app` runs hosted tests: StoreKit, against `Apps/macOS/HDMICaptain.storekit`.

## Conventions

- Swift 5 language mode (AVFoundation delegate/queue model); `@MainActor @Observable` for UI-facing models.
- Capture config runs on `CaptureEngine`'s session queue; frames are rendered directly on the capture queue (no main-thread hop).
- UI overlays (control bar, indicators, guides) are never in recordings, Save Recent, streams or screenshots: outputs read `CaptureEngine` sinks / `MetalRenderer.currentFrame` only. Keep it that way.
- Never stop the capture session for focus/Space/occlusion changes — that is the core requirement (spec §5.3).
- Signing: `Config/Signing.xcconfig` defaults to ad-hoc; local identity in gitignored `Config/Local.xcconfig`.
- Never decrypt or decompile Dongled (FairPlay); clean-room behaviour only.
