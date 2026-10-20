# Resilience test matrix (task 007)

Hardware: UGREEN HDMI Capture (MS2131, USB 2.0, 1280×720 @ 60), source Sony a6600, MacBook Pro (macOS 26.2).
Build: HDMI Captain Debug, commit `cdbb6a0`.

| Scenario | Expected | Result | Date / by |
|---|---|---|---|
| Click another app (window loses focus) | Live | ✅ Live | 2026-10-20 / user |
| Switch Space / virtual desktop | Live | ✅ Live | 2026-10-20 / user |
| Minimize and restore | Live | ✅ Live | 2026-10-20 / user |
| Other app in full screen | Live | ✅ \| 2026-10-20 / user |
| Screen lock 5 min | Live on return | ✅ \| 2026-10-20 / user |
| Display sleep / wake | Live on return | ✅ \| 2026-10-20 / user |
| System sleep / wake | Live on return (≤ 2 s) | ✅ \| 2026-10-20 / user |
| Unplug / replug card | Reconnects ≤ 2 s, no action | ✅ \| 2026-10-20 / user |
| HDMI cable pulled < 3 s | Banner over last frame | ✅ \| 2026-10-20 / user |

For comparison, Dongled (iPad app on Mac) tears down capture on `didEnterBackground` (confirmed from its MIT source, see `oss-review.md`). It drops in the first three scenarios.

Check for unrecovered interruptions: `/usr/bin/log show --last 30m --info --predicate 'subsystem == "hdmicaptain"' | grep -E "Restart|Failed"`.

## Engine log during the run (23:03, all recovered without user action)

- Screen lock, sleep and wake, and full screen: **no restarts**. The capture session never stopped.
- HDMI cable pulled: stall detected after 3 s, then "Restart #1 … No signal" and "Restart #2", then first frame again at 23:03:28 once the signal returned.
- USB unplug/replug: "Running" and "First frame" at 23:03:47. A second start 0.34 s later picks up the card's audio device, which enumerates after its video.
