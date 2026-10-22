# Microsoft Store listing — HDMI Captain (Windows)

Adapted from `docs/app-store/listing.md` and `docs/play-store/listing.md` (same voice, `docs/copy.md`), with only features the Windows app has today. Partner Center limits: description 10,000 characters, short description 1,000, up to 20 product features of 200 characters each, up to 7 search terms of 30 characters each (21 words in total).

| Field | Count / limit |
|---|---|
| Product name | 12 (reserved in Partner Center, task 103 step 1) |
| Short description | ≈ 150 / 1,000 |
| Description | ≈ 2,300 / 10,000 |
| Product features | 12 / 20 |
| Search terms | 7 / 7 |

## Product name

HDMI Captain

## Short description

See your console, camera or laptop on your PC through a USB HDMI capture card. Plug it in and the picture and sound appear, with no setup and no added delay.

## Description

HDMI Captain turns your PC into a monitor for anything with an HDMI output — game consoles, cameras, laptops and media players — using an inexpensive USB HDMI capture card.

ALWAYS ON
• Plug in a card and the picture appears. Cards reconnect on their own after a cable pull, sleep or another app borrowing them, and remember their settings.
• Switch apps, minimize or cover the window: capture keeps running, so the picture is there the moment you look back.
• Clear screens for no signal, HDCP-protected sources, a card that's in use and camera privacy settings tell you exactly what to do.

LOW LATENCY
• Media Foundation capture and Direct3D 11 rendering straight to the screen, with no extra buffering and no frame queue.
• Sound plays through a low-latency path that stays in sync for hours.
• Built-in diagnostics show the format, frame rate and dropped frames.

PICTURE AND SOUND, YOUR WAY
• Fit, fill, stretch or pixel-perfect integer scaling; sharp or smooth filtering.
• Picture looks (Standard, Game, Cinema, Vivid), brightness, contrast, saturation, gamma and sharpness; .cube LUTs with adjustable strength.
• HDMI sound with volume, audio delay to fix lip-sync, and left/right or mono fixes.
• Frame guides, safe areas, test patterns and scopes (waveform, vectorscope, histogram) for creators.
• Several cards at once: a window per card, or all of them side by side in one grid.

MADE FOR WINDOWS
• Keep the window on top, clean borderless mode, full screen with F11, actual-pixel window sizes, and a tray icon.
• Keyboard shortcuts for everything, full keyboard navigation and Narrator support.
• Saves screenshots to Pictures › HDMI Captain and copies frames to the clipboard. On-screen controls never appear in screenshots or recordings.

FREE VIEWER, ONE-TIME PRO
The viewer, screenshots and every picture setting are free, with no ads. HDMI Captain Pro is a single purchase — no subscription — and adds:
• Recording: save full-quality H.264 video with the HDMI sound, encoded on your graphics card.
• Save Recent: missed a great moment? Save the last 15 seconds, 30 seconds or minute after it happened.

PRIVATE
No accounts, no tracking, no data collection. Video and sound stay on your PC.

Requirements: Windows 10 version 2004 or later, or Windows 11 (tested on Windows 11), and a USB Video Class (driver-free) HDMI capture card. Sources that use HDCP (for example streaming apps) can't be captured by any capture card.

## Product features

1. Plug in a USB HDMI capture card and see the picture and hear the sound at once
2. Reconnects automatically after unplugging, sleep or another app using the card
3. Low-latency Direct3D rendering with no frame queue
4. Keeps capturing while the window is minimized, covered or in the background
5. Fit, fill, stretch and integer scaling with sharp or smooth filtering
6. Picture looks, brightness, contrast, saturation, gamma, sharpness and .cube LUTs
7. Audio volume, lip-sync delay and channel fixes
8. Guides, safe areas, test patterns and scopes
9. A window per card, or every card in one grid
10. Always on top, clean mode, full screen, tray icon and keyboard shortcuts
11. Pro (one-time): recording with HDMI sound and Save Recent
12. No accounts, no ads, no tracking

## Search terms

capture card · HDMI capture · capture card viewer · game capture · HDMI monitor · console on PC · webcam viewer

## Category and age rating

- Category: Photo & video (alternative: Utilities & tools).
- IARC questionnaire (task 103 step 4): no user-generated content, no chat, no ads, no purchases of random items. *Estimate:* Everyone / 3+.

## What's new (first release)

First release of HDMI Captain for Windows.

## Screenshots

At least one is required, and four are planned, at 1920×1080 (the Store accepts 1366×768 and up). Take them with `-testPattern smpteBars -qaPinControls YES -qaAction screenshot` for the window chrome, and the grid with `-testPattern smpteBars -qaAction grid`. Replace the test patterns with a live console picture before release (a game we're allowed to show, or our own footage).

1. Main window with the control bar over a live picture.
2. The ⋯ menu open on Picture Look / Guides.
3. Grid with several cards.
4. Scopes window next to the viewer.

## Required URLs and notices

- Privacy policy URL (mandatory for desktop apps, Store policy 10.5.1): host `docs/microsoft-store/privacy.md` on the project site before submission.
- Support contact: to be decided with the user (task 103).
- Third-party notices: `Apps/Windows/THIRD-PARTY.md` ships in the package; link it from the listing's "Additional license terms".
