# App Privacy (App Store Connect answers)

Applies to HDMI Captain for macOS and iPadOS. Source of truth: `Apps/Shared/PrivacyInfo.xcprivacy`.

## Data collection

**"Do you or your third-party partners collect data from this app?" → No.**

- No analytics, crash-reporting SDKs, ads or accounts.
- Video and audio from the capture card are shown and played locally; nothing is uploaded.
- Screenshots and recordings are written only where the user can see them: Pictures/Movies › HDMI Captain on Mac; the app's Files folder and (with add-only permission) Photos on iPad.
- In-app purchase is handled by StoreKit; Apple processes the payment, and we receive no personal data.
- Live streaming (Pro, currently hidden) sends video only to the RTMP/SRT server the user enters. The stream key is stored in the Keychain. If streaming ships, it is user-initiated transmission, not "collection" (no data reaches us), so the answer stays No. Re-check this when 046 ships.

## Tracking

No tracking (`NSPrivacyTracking = false`, no tracking domains).

## Permissions and why

| Permission | Platform | Why (usage string) |
|---|---|---|
| Camera | Mac, iPad | Capture cards appear to the system as cameras: "HDMI Captain shows the video from your HDMI capture device." |
| Microphone | Mac, iPad | Capture cards deliver HDMI sound as a microphone input: "HDMI Captain plays the audio from your HDMI capture device." |
| Photo library (add only) | iPad | Saving screenshots and recordings to Photos: "HDMI Captain saves screenshots and recordings to your photo library." |

## Required-reason APIs

| Category | Reason | Use |
|---|---|---|
| UserDefaults | CA92.1 | App preferences and per-card presets |
| System boot time | 35F9.1 | `CACurrentMediaTime` for frame timing, latency and stall detection |
