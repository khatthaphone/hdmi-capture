# Resilience test matrix (task 007)

Hardware: UGREEN HDMI Capture (MS2131, USB 2.0, 1280×720 @ 60), source Sony a6600, MacBook Pro (macOS 26.2).
Build: HDMI Captain Debug, commit `cdbb6a0`.

| Scenario | Expected | Result | Date / by |
|---|---|---|---|
| Click another app (window loses focus) | Live | ✅ Live | 2026-10-20 / user |
| Switch Space / virtual desktop | Live | ✅ Live | 2026-10-20 / user |
| Minimize and restore | Live | ✅ Live | 2026-10-20 / user |
| Other app in full screen | Live | ⏳ | |
| Screen lock 5 min | Live on return | ⏳ | |
| Display sleep / wake | Live on return | ⏳ | |
| System sleep / wake | Live on return (≤ 2 s) | ⏳ | |
| Unplug / replug card | Reconnects ≤ 2 s, no action | ⏳ | |
| HDMI cable pulled < 3 s | Banner over last frame | ⏳ | |

For comparison, Dongled (iPad app on Mac) tears down capture on `didEnterBackground` (confirmed from its MIT source, see `oss-review.md`). It drops in the first three scenarios.

Check for unrecovered interruptions: `/usr/bin/log show --last 30m --info --predicate 'subsystem == "hdmicaptain"' | grep -E "Restart|Failed"`.
