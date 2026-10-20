# UX/UI review — triage (task 059)

**Sources:**
- Codex design review of the Mac screens: [`ux/codex-review-mac.md`](ux/codex-review-mac.md), verbatim.
- Our own pass over the Mac and iPad screenshots in [`ux/shots/`](ux/shots/).
- The iPad paused/settings screens seen during Phase 3 (`.build/ipad-snapshot`).

**Decisions where we differ from Codex:**
- **Control bar surface:** Codex wants an opaque 72–78% black bar. The user explicitly asked for a see-through capsule (commit e709382). We keep it see-through but give it a darker base (black ~40% + material) and a text shadow. Under *Increase Contrast* it becomes a ~88% opaque dark bar.
- **Icon:** Codex suggests "HDMI connector forming a C", navy plus electric blue. We feed that into 060 as one of the briefs next to our own concepts; the user picks.

## P1 — before release

| # | Issue | Fix | Task |
|---|---|---|---|
| 1 | Camera and microphone prompts appear on launch with no explanation | Pre-permission explainer screen on first run, then the system prompt | 062 |
| 2 | Microphone denial is silent (video without sound looks broken) | Show an "Audio access is off" banner with Open Settings when the mic is denied | 062 |
| 3 | "No signal", "reconnecting" and "unplugged" blur together; "No signal — reconnecting…" suggests software can fix a source problem | Separate states and copy: **No HDMI signal** (rectangle.slash, no spinner), **Reconnecting to X…** (spinner), **X disconnected** (cable.connector.slash) | 061, 063 |
| 4 | Bar washes out over bright video | Darker see-through base; Increase Contrast → opaque (see decision above) | 061 |
| 5 | Every toast shows a success checkmark, errors included | Semantic toasts: success, progress, warning, error (icon, tint, duration 2.5 s vs 5 s) | 061 |
| 6 | Virtual Camera is sold in Pro as "Coming soon" | Hide it from the sheet and Settings, like streaming (`ProFeature.hidden`) until it ships | 064 |
| 7 | Settings disables the whole card form without a session, including the device picker | Keep Capture card enabled; show a "Connect or select a capture card" note; disable only card-dependent rows | 061 |
| 8 | iPad bar targets are 28 pt | 44×44 pt minimum on iPad and 8 pt spacing (`BarButtonStyle` per platform) | 065 |

## P2

| # | Issue | Fix | Task |
|---|---|---|---|
| 9 | Status screens are inconsistent (spinner vs symbol vs illustration, title too close to body) | One `StateCard`: 40 pt symbol or illustration, 20–22 pt semibold title, 13–14 pt secondary, max 420 pt wide | 061 |
| 10 | Spinners while waiting for the user ("Scanning for hardware") | Spinner only for real work (connecting/reconnecting). "No capture card found" plus a connect illustration and Connection Help | 061, 062 |
| 11 | Connecting can hang silently | After 8 s: "This is taking longer than expected." plus Reconnect / Help | 061 |
| 12 | Permission denied: weak hierarchy and a generic Try Again | "Camera access required" · Open Camera Settings · Check Again | 061, 063 |
| 13 | Unplugged copy is indirect | "X disconnected" · "Reconnect it to resume automatically." | 063 |
| 14 | Failed: "Can't show video" is generic | "X stopped responding" plus Reconnect; technical detail never surfaces | 061, 063 |
| 15 | No-signal hint is a long paragraph | Short body plus a **Troubleshoot…** sheet (HDCP / PS5 setting, cable, power) | 062 |
| 16 | `gobackward` reads as undo | Save Recent → `clock.arrow.circlepath` (or the 048 glyph) | 048 |
| 17 | Bar says "No Input" while the test pattern shows; Record then errors | The chip says **Test Pattern**; Record is disabled with help text | 061 |
| 18 | Recording state can be hidden entirely | Always keep a small red dot plus timer; the pref only controls the timer | 061 |
| 19 | Compact iPad bar relies on hover help | Accessible labels everywhere; keep a label on the primary action | 065 |
| 20 | Toasts don't account for bar height or zones | Fixed overlay zones; the toast sits above the measured bar | 061 |
| 21 | Error toasts vanish too fast and aren't actionable | Warnings/errors 5 s; actionable banner where there's an obvious fix | 061 |
| 22 | Pro sheet terms and CTA | "One-time purchase. No subscription." · "Buy Pro — $9.99" · benefit-first rows · quiet Restore | 064 |
| 23 | Settings window jumps size and clips the Pro footer | One content size (~560×620) with scrolling | 061 |
| 24 | A pale disabled Picture form with no card | `ContentUnavailableView` "No active capture card" | 061 |
| 25 | Settings wording inconsistent (Mac vs iPad) | Shared vocabulary from docs/copy.md | 063 |
| 26 | Profiles' "active window" is ambiguous | "Apply to <card>" button and footer | 063 |
| 27 | Locked Save Recent settings are editable | Lock the whole group behind Pro with Upgrade… | 064 |
| 28 | No VoiceOver announcements for state changes | Post announcements for connect, saved, failed | 065 |
| 29 | iPad: no USB-C / UVC guidance, the pause isn't explained ahead of time | First-run note plus a one-time tip | 062 |
| 30 | No app icon | 060 | 060 |

## P3

| # | Issue | Fix | Task |
|---|---|---|---|
| 31 | "Saved HDMI Captain 2026-10-21 at 03.10.59.png" | "Screenshot saved" / "Recording saved · 0:12" | 063 |
| 32 | The stats overlay reads like a developer HUD | Rename to **Show Diagnostics**; 2-line summary | 063 |
| 33 | Level meter has no L/R labels or accessible value | Add L/R labels and an accessibility value | 065 |
| 34 | Adjustment sliders show no values (Mac) | Trailing values (the iPad already has them) | 061 |
| 35 | Window settings are one long list | Sections: Viewer, App Presence, Guides | 063 |
