# Shared test fixtures

JSON test vectors read by **both** the Swift tests (`Packages/CaptureCore/Tests`) and the Kotlin tests (`Apps/Android/core/model`), so the two ports of the same pure logic can't drift apart. Change a fixture → both suites must still pass.

| File | Logic | Read by |
|---|---|---|
| `format-selection.json` | `FormatSelector.select` (auto + pinned) | Swift, Kotlin |
| `format-bandwidth.json` | Android: USB link-speed limits on top of `select` | Kotlin |
| `signal-classify.json` | `SignalAnalyzer.classify` | Swift, Kotlin |
| `signal-monitor.json` | `SignalMonitor` persistence (loss after N s, recovery immediate) | Swift, Kotlin |
| `recovery-backoff.json` | restart delay per attempt (0.25 s doubling, 4 s cap) | Swift, Kotlin |
| `render-geometry.json` | `RenderGeometry.compute` (viewport + visible source rect per scale mode) | Swift, Kotlin |
| `recovery-machine.json` | Android `RecoveryMachine` event → state sequences | Kotlin |

Formats: `{"w", "h", "cc", "rates": [discrete fps…]}` or `"ranges": [[min, max]…]`. Four-character codes use Apple names (`420v`, `yuvs`, `dmb1`) or UVC names (`NV12`, `YUY2`, `MJPG`); both selectors know both.
