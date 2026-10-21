# iPad verification checklist (task 058) — iPad Pro 11" (2nd gen), iPadOS 18.7.3, UGREEN-25854

| # | Item | Result | Evidence |
|---|---|---|---|
| 1 | Card detected; live 1080p60 picture | ✅ | User + snapshots; 59 fps at 60 Hz source |
| 2 | Hot-plug: unplug/replug brings the picture back with the same preset | ✅ | User confirmed; stable preset key (052) |
| 3 | HDMI audio from the speakers, mute/volume | ✅ | User confirmed |
| 4 | No audio drift over 30 min | ✅ | Soak CSV: buffer flat at 18 ms |
| 5 | 30-min soak: no restarts, memory steady, thermal nominal | ✅ | `devices/ipad-pro-11-2g.md` |
| 6 | Background → foreground resumes < 500 ms | ✅ | `qa-events-background-2026-10-21.csv`: 14–22 ms |
| 7 | Split View / Slide Over: Paused screen names the reason, resumes in full screen | ✅ | User confirmed (A12Z: no multitasking camera access) |
| 8 | Recording across backgrounding leaves a playable file | ✅ | 6.8 s HEVC + AAC, clean stop |
| 9 | Screenshot, recording, Save Recent → Files + Photos | ✅ | User confirmed |
| 10 | Pro lock on Record / Save Recent when not purchased | ✅ | `ux/shots/ipad/bar-pro-locked.png` |
| 11 | Settings sheet, every section | ✅ | Snapshot via `-qaAction settings` |
| 12 | Narrow windows (320/507/700 pt): bar layouts, no clipping | ✅ | `ux/shots/ipad/bar-widths.png` |
| 13 | LUT applied and persisted across relaunch | ✅ | `-qaAction lut` / `lutOff` (041 log) |
| 14 | Rotation: upright, unmirrored, letterboxed | ⏳ → 068 | `devicectl orientation` unsupported on this iPad; needs a person |
| 15 | Headphones / AirPods take over the sound | ⏳ → 068 | No headphones at hand |
| 16 | iPadOS menu bar + hardware-keyboard shortcuts | ⏳ → 068 | Needs a keyboard |
| 17 | External display shows the clean feed | ⏳ → 068 (057) | Needs a USB-C hub with HDMI out |
| 18 | Second card (UGREEN MS2131) on the iPad | ⏳ → 068 | Not tried yet |
| 19 | New app icon on the home screen | ⏳ → 068 | Can't screenshot SpringBoard remotely |
| 20 | Simulator run | ❌ tooling | `simctl launch` hangs for our app in Xcode 26.6 (051 log); the device is used instead |
