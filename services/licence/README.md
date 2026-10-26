# HDMI Captain licence service

Sells **HDMI Captain Pro** through Stripe Checkout and issues licence keys that the apps verify offline (task 119,
ADR 0004). It is one Cloudflare Worker in plain JavaScript with no dependencies. The same code runs locally under
Node for tests and development.

## How it works

1. The app's **Buy Pro…** button opens `<service>/buy?platform=linux`. The Worker creates a Checkout Session for
   `STRIPE_PRICE_ID` and redirects the buyer to Stripe.
2. After payment, Stripe sends `checkout.session.completed` to `/stripe/webhook` and returns the buyer to
   `/success?session_id=…`. Whichever arrives first issues the key; the licence id is derived from the session id,
   so the two can't produce two licences. The success page shows the key and how to paste it.
3. A key is `HC1-` followed by base64url of a 16-byte payload (version, product, licence id, issue day) and an
   ECDSA P-256 signature. The apps embed the public key and reject typos and forgeries without the network.
4. **Activate** binds the key to a computer: a SHA-256 of `/etc/machine-id`, never the raw id. A key works on 3
   computers (`MAX_ACTIVATIONS`); activating the same computer again reuses its seat. **Validate** runs at launch
   and every 3 days. A Stripe refund (full refund) or a dispute revokes the key at the next validation. When offline,
   the app keeps Pro for 30 days after the last good check.
5. No customer data is stored, only the licence record (id, Checkout session, payment intent, activations). Support
   finds a buyer by email in the Stripe dashboard and reopens `/success?session_id=<their session>` to show the key
   again.

## Develop and test

```sh
npm test                       # node --test (Node 20+)
node dev/server.mjs            # local service + fake Stripe on 127.0.0.1:8787; prints the throwaway public key
```

The dev server serves the real Worker against an in-memory KV and a fake Stripe at `/fake-stripe`. Opening `/buy`
goes through a fake checkout, sends a signed webhook and lands on the success page. To revoke a payment, run
`POST /fake-stripe/refund/<payment intent>`. The Linux app talks to it in DEBUG builds:

```sh
HDMICAPTAIN_LICENCE_API=http://127.0.0.1:8787 HDMICAPTAIN_LICENCE_PUBLIC_KEY=<printed key> make linux-run
```

`make linux-test` includes an end-to-end test that starts this server and buys, activates, validates, refunds and
deactivates through the app's own client.

## Deploy (one time)

1. **Signing key.** Run `node tools/genkey.mjs .local/signing-key.jwk`. It prints the public key, which goes into
   the apps (`PublicKey` in `Apps/Linux/src/HDMICaptain.Linux.App/StripeLicence.cs`). Back up the private JWK in a
   password manager. It is gitignored and must never be committed. Replacing it invalidates every key already sold.
   The key in `.local/` on the dev PC (created 2026-10-26) matches the public key in the app now.
2. **Stripe.** In test mode first, create a Product "HDMI Captain Pro" with a one-time Price (USD 9.99, the price
   placeholder in ADR 0004). Put its `price_…` id in `wrangler.toml`.
3. **Cloudflare.** Run `npx wrangler login`, then `npx wrangler kv namespace create LICENCES`, and put the namespace
   id in `wrangler.toml`. Then set the secrets:
   ```sh
   npx wrangler secret put STRIPE_SECRET_KEY        # sk_test_… (later sk_live_…, or a restricted key with
                                                    # Checkout Sessions write + read)
   npx wrangler secret put LICENCE_SIGNING_KEY < .local/signing-key.jwk
   npx wrangler deploy                              # prints https://hdmicaptain-licence.<account>.workers.dev
   ```
4. **Webhook.** In Stripe › Developers › Webhooks, add `<service>/stripe/webhook` with the events
   `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `charge.refunded` and
   `charge.dispute.created`. Then run `npx wrangler secret put STRIPE_WEBHOOK_SECRET` with the endpoint's `whsec_…`.
5. **App.** Set `ApiBase` in `StripeLicence.cs` to the service URL (or a custom domain), and test a purchase with
   Stripe's test card 4242 4242 4242 4242. Go live by repeating steps 2–4 with live-mode keys and the live Price.

Cloudflare KV is eventually consistent across locations. Two activations of one key at the same moment, in
different regions, could both take the last seat. That is acceptable for a seat limit. Moving the record to a
Durable Object would make it exact.
