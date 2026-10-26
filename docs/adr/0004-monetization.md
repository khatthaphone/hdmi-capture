# ADR 0004 — Monetization: free viewer + one-time Pro unlock

- **Status:** accepted (2026-10-20)
- **Context:** Competitor research (`docs/research/competitors.md`) shows subscriptions are the most common complaint, and every basic viewer feature is offered free somewhere. Users pay for capability (recording, streaming), not for a viewer.

## Decision

1. **The viewer is free and unrestricted.** That covers capture, audio, scaling, signal status, multi-view, profiles, monitoring tools and screenshots. There are no nags, timers or watermarks.
2. **"HDMI Captain Pro"** is one **non-consumable** in-app purchase that unlocks:
   - `recording`
   - `replayBuffer`
   - `streaming`
   - `virtualCamera`
3. **Gating lives in one place.** `ProFeature` and `EntitlementResolver` (pure, in CaptureCore) are used by `EntitlementStore` (app, StoreKit 2). Feature code asks `entitlements.isUnlocked(.feature)` and nothing else.
4. **Trust only verified transactions.** We use `VerificationResult.verified`, ignore revoked transactions, re-check `Transaction.currentEntitlements` at launch and listen to `Transaction.updates` for refunds and Family Sharing changes.
5. **Distribution:** StoreKit IAP requires the **Mac App Store** (task 045). Developer ID builds can sell Pro later through a license-key provider (Paddle, Lemon Squeezy and similar) implementing the same resolver inputs. That's not in scope now.
6. **Local development** uses `Apps/macOS/HDMICaptain.storekit` (Xcode StoreKit testing), so no App Store Connect account is needed until submission. DEBUG builds also have a "Developer: unlock Pro" toggle.

## Linux: Stripe with signed licence keys (2026-10-26)

The Linux app has no store with in-app purchases, so Pro is sold directly. The user chose **Stripe** (task 119).
Stripe has no licence keys, so a small service of our own sits between them (`services/licence`, a Cloudflare Worker):

- **Buy:** the app opens `<service>/buy`. The service creates a Stripe Checkout Session for the one-time Pro Price,
  so the price lives in Stripe (USD 9.99 placeholder, as on the App Store).
- **Key:** the webhook (or the success page, whichever arrives first) issues `HC1-…`, an ECDSA P-256-signed key. The
  licence id comes from the Checkout session, so each payment gives one licence. The app checks the signature offline
  with the embedded public key. The private key exists only as a Worker secret and in the owner's password manager.
- **Activate / validate:** 3 computers per key, each identified by a salted hash of `/etc/machine-id`. The app
  validates at launch and every 3 days, and keeps Pro for 30 days offline after the last good check. A full refund or
  a dispute (Stripe webhooks) revokes the key at the next validation.
- **Gate:** `LicenceStore.IsUnlocked(ProFeature)` is the only gate on Linux. Free features never ask it.
- **Data:** the service stores licence records only (session, payment intent, activations), no customer data.
  Support finds buyers in the Stripe dashboard.
- **Why not Lemon Squeezy / Paddle + Keygen:** the user preferred Stripe. The cost is owning the service (about 300
  lines, tested in `services/licence/test` and end to end from the app's tests). The Windows Store and App Store keep
  their own purchases. A later direct-sale Windows or Mac build can reuse this service as is.

## Consequences

- The product ID is `<bundle id>.pro`. The bundle ID is still the placeholder `com.example.hdmicaptain`, so the product ID will change at submission.
- Price is placeholder **USD 9.99** in the `.storekit` file. The real price is set in App Store Connect.
- If a license-key channel is added, `EntitlementResolver` gains a second source. Feature code stays unchanged.
