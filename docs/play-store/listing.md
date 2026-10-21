# Google Play listing — HDMI Captain (Android)

Adapted from `docs/app-store/listing.md` (same voice, `docs/copy.md`). Limits: title 30, short description 80, full description 4000.

| Field | Count / limit |
|---|---|
| Title | 12 / 30 |
| Short description | 76 / 80 |
| Full description | ≈ 1,900 / 4000 |

## Title

HDMI Captain

## Short description

See your console or camera on your phone through a USB HDMI capture card.

## Full description

HDMI Captain turns your Android phone, tablet or Chromebook into a monitor for anything with an HDMI output — game consoles, cameras, computers and media players — using an inexpensive USB HDMI capture card.

ALWAYS ON
• Plug in a card and the picture appears. Cards reconnect on their own and remember their settings.
• Rotate, resize, split screen or go picture-in-picture: the picture keeps running.
• Clear screens for no signal, HDCP-protected sources, USB access and missing permissions tell you exactly what to do.

LOW LATENCY, SHOWN NOT CLAIMED
• Our own USB video engine and GPU rendering straight to the screen, with no extra buffering.
• Phones with a USB 2.0 port automatically use the card's compressed mode to keep 60 fps.
• Built-in diagnostics show frame rate, dropped frames and latency.

PICTURE AND SOUND, YOUR WAY
• Fit, fill, stretch or pixel-perfect integer scaling; sharp or smooth filtering.
• Brightness, contrast, saturation, gamma and sharpness; .cube LUTs.
• HDMI sound with volume, audio delay to fix lip-sync, and left/right or mono fixes.
• Guides, safe areas and test patterns for creators.
• Keyboard shortcuts and mouse support on Chromebooks, tablets and Samsung DeX.

FREE VIEWER, ONE-TIME PRO
The viewer, screenshots and every picture setting are free, with no ads. HDMI Captain Pro is a single purchase — no subscription — and adds:
• Recording: save full-quality HEVC or H.264 video with the HDMI sound.
• Save Recent: missed a great moment? Save the last 15 seconds to 60 minutes after it happened.

PRIVATE
No accounts, no tracking, no data collection. Video and sound stay on your device.

Requirements: a UVC (driver-free) USB HDMI capture card and a phone or tablet with USB-C that supports USB host (OTG). Some cards need a powered USB-C hub. Sources that use HDCP (for example streaming apps) can't be captured by any capture card.

## Category and tags

Category: Video Players & Editors (alternative: Tools). Content rating: Everyone (no user content, no ads).

## Graphics

- Icon 512×512: from `Design/AppIcon/master-1024.png`.
- Feature graphic 1024×500 and phone/tablet screenshots: generate from test-pattern captures (`--es testPattern smpteBars --ez qaPinControls true`) on a device; replace with a live console picture before release.
