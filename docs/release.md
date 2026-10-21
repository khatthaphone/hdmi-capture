# Releasing HDMI Captain

Two channels: the **Mac App Store + iPad App Store** (one universal purchase: same bundle id `com.example.hdmicaptain` and product `Pro`), and optionally a **notarized DMG** for direct download (Mac only).

## Prerequisites (blocked until the Apple Developer Program membership is active)

1. Change the bundle-id prefix in `project.yml` (`com.example` → your reverse domain). Bundle IDs can't change after the first upload.
2. In `Config/Local.xcconfig` (gitignored), set `DEVELOPER_ID_TEAM`/`DEVELOPMENT_TEAM` to the paid team (not the Personal Team `9AYLG7F7W2`).
3. App Store Connect: create the app record (macOS + iOS, universal purchase) and the non-consumable IAP `Pro` (same id as `Apps/macOS/HDMICaptain.storekit`).
4. Notarization profile, once: `xcrun notarytool store-credentials hdmicaptain --apple-id … --team-id …` (stored in the keychain, never in the repo).

## Checklist for each release

1. `make test && make test-ios-core && make test-app` all pass. `make build`, `make build-ipad`, and a **Release** build (`tools/release/release.sh` archives Release) succeed.
2. Bump `MARKETING_VERSION` / `CURRENT_PROJECT_VERSION` in `project.yml`.
3. Smoke-test the Release app: first run (Welcome → camera prompt), live card, record, Save Recent, the Pro sheet.
4. **App Store:** `UPLOAD=1 tools/release/appstore.sh` → TestFlight (Mac + iPad) → submit with:
   - listing text: `docs/app-store/listing.md`
   - screenshots: `docs/app-store/screenshots/` (swap the viewer shots for live-source captures)
   - App Privacy answers: `docs/app-store/privacy.md` ("Data Not Collected")
   - review notes: in `listing.md` (capture-card requirement, test patterns, hidden features)
5. **Direct DMG (optional):** `DEVELOPER_ID=1 NOTARY_PROFILE=hdmicaptain UPDATE_URL_PREFIX=https://hdmicaptain.app/updates/ tools/release/release.sh`. It builds the **HDMICaptain-Direct** target (the same app plus Sparkle), then the DMG, `updates/HDMI-Captain-<version>.zip` and a signed `updates/appcast.xml`. Upload `updates/*` so the appcast is served at `SPARKLE_FEED_URL` (default `https://hdmicaptain.app/appcast.xml`). Check: `spctl -a -vv` says *Notarized Developer ID*. Note: StoreKit (Pro) only works in App Store builds; the DMG build is the free viewer unless a licence-key path is added (spec §10).

## Sparkle (Direct target only)

- **Key:** EdDSA, created with Sparkle's `generate_keys`. The private key lives in the release Mac's login keychain (*Private key for signing Sparkle updates*); back it up, because losing it means users can't update. The public key is `SPARKLE_PUBLIC_ED_KEY` in `project.yml`.
- **Sandbox:** `SUEnableInstallerLauncherService` plus the `-spks`/`-spki` mach-lookup exceptions are in the Direct entitlements. `NSAllowsLocalNetworking` is there only for local update tests.
- **Local test** (done 2026-10-21, task 019 log): build v1 and v2 with `SPARKLE_FEED_URL=http://127.0.0.1:8765/appcast.xml`, run `generate_appcast` on the v2 zip, serve it with `python3 -m http.server 8765`, then v1 › Check for Updates… › Install and Relaunch. The result was build 2, relaunched, with a valid signature.

## Notes

- The Mac app is **universal** (spec §5.7). SRTHaishinKit is not linked, because its libsrt has no x86_64 slice and broke the universal Release link. Streaming (hidden, 046) uses RTMP only until SRT is rebuilt or the Mac goes arm64-only.
- Release builds contain no QA hooks (`-qaAction`, soak, snapshot): they're all `#if DEBUG`.
- Without the paid account, `tools/release/release.sh` still produces a development-signed DMG (Gatekeeper rejects it on other Macs), and `tools/release/appstore.sh` archives both apps.
