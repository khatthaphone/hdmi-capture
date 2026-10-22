# Shared test fixtures

JSON test vectors read by the Swift tests (`Packages/CaptureCore/Tests`), the Kotlin tests (`Apps/Android/core/model`) and the C# tests (`Apps/Windows/tests/HDMICaptain.Core.Tests`), so the three ports of the same pure logic can't drift apart. Change a fixture → every suite that reads it must still pass.

| File | Logic | Read by |
|---|---|---|
| `format-selection.json` | `FormatSelector.select` (auto + pinned) | Swift, Kotlin, C# |
| `format-bandwidth.json` | Android: USB link-speed limits on top of `select` (C# reuses it for hub-reported link speed) | Kotlin, C# |
| `signal-classify.json` | `SignalAnalyzer.classify` | Swift, Kotlin, C# |
| `signal-monitor.json` | `SignalMonitor` persistence (loss after N s, recovery immediate) | Swift, Kotlin, C# |
| `recovery-backoff.json` | restart delay per attempt (0.25 s doubling, 4 s cap) | Swift, Kotlin, C# |
| `render-geometry.json` | `RenderGeometry.compute` (viewport + visible source rect per scale mode) | Swift, Kotlin, C# |
| `recovery-machine.json` | `RecoveryMachine` event → state sequences. **Not extracted yet**: the sequences live in the Kotlin and C# unit tests (`RecoveryMachineTest.kt`, `RecoveryMachineTests.cs`) | Kotlin, C# (as code) |
| `ms2109-audio.json` | Windows: capture endpoint format → reinterpret as 48 kHz stereo? (MS2109 reports 96 kHz mono 16-bit but sends 48 kHz stereo; every other format untouched) | C# |

Formats: `{"w", "h", "cc", "rates": [discrete fps…]}` or `"ranges": [[min, max]…]`. Four-character codes use Apple names (`420v`, `yuvs`, `dmb1`) or UVC names (`NV12`, `YUY2`, `MJPG`); all three selectors know both (C# maps Media Foundation subtype GUIDs in `MediaSubtypes`).
