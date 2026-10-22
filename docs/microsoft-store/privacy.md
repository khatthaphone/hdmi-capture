# Privacy — HDMI Captain for Windows

Partner Center answers, plus the text for the privacy policy URL that Store policy 10.5.1 requires for desktop apps. Matches `docs/app-store/privacy.md` (Mac/iPad).

## Data collection

**HDMI Captain collects no personal data.**

- There are no analytics, crash-reporting SDKs, ads or accounts.
- Video and sound from the capture card are shown and played on the PC; nothing is uploaded.
- Screenshots go to Pictures › HDMI Captain and recordings to Videos › HDMI Captain. Copy Frame puts a frame on the clipboard. All of these happen only when the user asks.
- Settings and per-card presets are stored in `%LOCALAPPDATA%\HDMICaptain` (prefs.json, state.json). In a Store install, Windows redirects that folder into the app's private package data, which is removed when the app is uninstalled.
- The Pro purchase is handled by the Microsoft Store (`Windows.Services.Store`); Microsoft processes the payment and we receive no personal data. A copy of the entitlement is cached locally, encrypted with DPAPI for the current user, so Pro works offline.

## Capabilities and why

| Capability (manifest) | Why |
|---|---|
| `webcam` | Capture cards appear to Windows as cameras. The app reads only the card's video. |
| `microphone` | Capture cards deliver HDMI sound as a microphone input. The app reads only the card's own audio endpoint (matched by its USB container). If microphone access is off, the picture still works without sound. |
| `runFullTrust` | Desktop app (WinUI 3) using Media Foundation, Direct3D 11 and WASAPI. |

## Policy text (for the hosted privacy policy page)

> HDMI Captain does not collect, store or share any personal information. Video and sound from your capture card are shown on your PC and never leave it. Screenshots and recordings are saved only to folders on your PC when you ask for them. Purchases are processed by Microsoft; we receive no payment or personal details. The app has no accounts, ads, analytics or tracking. If you have questions, contact us at the support address on the Store listing.
