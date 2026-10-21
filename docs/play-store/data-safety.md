# Play Console answers — Data safety, permissions, policies

## Data safety

**Does your app collect or share any of the required user data types? → No.**

- No analytics, crash reporting, ads or accounts. No network use except Google Play Billing.
- Video and audio from the capture card are shown and played on the device; nothing is uploaded.
- Screenshots and recordings are saved only to the device's Pictures / Movies › HDMI Captain (MediaStore).
- Purchases go through Google Play Billing; Google processes payment and we receive no personal data.
- Data encrypted in transit: not applicable (no data transmitted). Deletion request: not applicable.

## Permissions and why (for review notes)

| Permission | Why |
|---|---|
| `CAMERA` | Android treats a USB HDMI capture card as a camera; required before the USB grant for UVC devices. Used only to show the card's picture. |
| `RECORD_AUDIO` | The capture card's HDMI sound arrives as a USB microphone. Played back live; recorded only when the user records (Pro). |
| USB device access (runtime dialog) | Opening the capture card with our own UVC driver. |
| Foreground service | Not used. Capture stops when the app isn't visible. Picture-in-picture keeps it visible. |

## App content

- Ads: No. Target audience: 13+ (general). News app: No. Government app: No. Financial features: No. Health: No.
- In-app products: one managed product `com.example.hdmicaptain.pro` (one-time, non-consumable "HDMI Captain Pro"). Replace the placeholder application ID before release.

## Review notes

Testing needs a USB UVC HDMI capture card (for example UGREEN 1080p60 cards) and any HDMI source. Without one, the app shows "Connect a capture card". Built-in test patterns: ⋯ › Test Pattern. Diagnostics: ⋯ › Show Diagnostics. The Pro purchase unlocks Recording and Save Recent; Restore Purchase is in the upgrade sheet.
