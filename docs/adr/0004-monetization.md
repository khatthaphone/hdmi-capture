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

## Consequences

- The product ID is `<bundle id>.pro`. The bundle ID is still the placeholder `com.example.hdmicaptain`, so the product ID will change at submission.
- Price is placeholder **USD 9.99** in the `.storekit` file. The real price is set in App Store Connect.
- If a license-key channel is added, `EntitlementResolver` gains a second source. Feature code stays unchanged.
