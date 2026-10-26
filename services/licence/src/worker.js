// HDMI Captain licence service (task 119): sells Pro through Stripe Checkout and issues signed licence keys.
//
//   GET  /buy                  → creates a Checkout Session for STRIPE_PRICE_ID and redirects to it
//   GET  /success?session_id=  → shows the key for a paid session (issues it if the webhook hasn't yet)
//   POST /stripe/webhook       → checkout.session.completed / async_payment_succeeded issue; charge.refunded and
//                                charge.dispute.created revoke
//   POST /v1/activate          {key, machine, name}  → {instanceId} | 403 revoked | 409 limit
//   POST /v1/validate          {key, instanceId}     → {status: active | revoked | inactive | unknown}
//   POST /v1/deactivate        {key, instanceId}     → {ok: true}
//
// State lives in the LICENCES KV namespace: licence:<id> (record), session:<checkout id> → key, pi:<payment intent> → id.
// No customer data is stored: support finds a buyer's session in the Stripe dashboard and reopens /success for it.
// Env: STRIPE_SECRET_KEY, STRIPE_WEBHOOK_SECRET, STRIPE_PRICE_ID, LICENCE_SIGNING_KEY (private JWK), MAX_ACTIVATIONS.

import { importSigningKey, issueKey, licenceIdFor, parseKey, toHex } from './keys.js';
import { stripeApi, verifyWebhook } from './stripe.js';
import { cancelledPage, keyPage, messagePage } from './pages.js';

const DEFAULT_MAX_ACTIVATIONS = 3;

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (e) {
      console.error(e);
      return json({ error: 'server' }, 500);
    }
  },
};

async function route(request, env) {
  const url = new URL(request.url);
  const path = url.pathname;
  if (request.method === 'GET' && path === '/buy') return buy(url, env);
  if (request.method === 'GET' && path === '/success') return success(url, env);
  if (request.method === 'GET' && path === '/cancelled') return html(cancelledPage(), 200);
  if (request.method === 'POST' && path === '/stripe/webhook') return webhook(request, env);
  if (request.method === 'POST' && path.startsWith('/v1/')) {
    let body;
    try {
      body = await request.json();
    } catch {
      return json({ error: 'bad_request' }, 400);
    }
    if (path === '/v1/activate') return activate(body, env);
    if (path === '/v1/validate') return validate(body, env);
    if (path === '/v1/deactivate') return deactivate(body, env);
  }
  return json({ error: 'not_found' }, 404);
}

async function buy(url, env) {
  const session = await stripeApi(env, 'POST', '/checkout/sessions', {
    mode: 'payment',
    'line_items[0][price]': env.STRIPE_PRICE_ID,
    'line_items[0][quantity]': '1',
    success_url: `${url.origin}/success?session_id={CHECKOUT_SESSION_ID}`,
    cancel_url: `${url.origin}/cancelled`,
    allow_promotion_codes: 'true',
    'metadata[product]': 'pro',
    'metadata[platform]': (url.searchParams.get('platform') ?? 'unknown').slice(0, 16),
  });
  return Response.redirect(session.url, 303);
}

async function success(url, env) {
  const id = url.searchParams.get('session_id');
  if (!id || !/^cs_[A-Za-z0-9_]+$/.test(id)) return html(messagePage('That link is incomplete', 'Open the link from your payment confirmation again.'), 400);
  let key = await env.LICENCES.get(`session:${id}`);
  if (!key) {
    // The browser can arrive before the webhook: ask Stripe directly.
    const session = await stripeApi(env, 'GET', `/checkout/sessions/${encodeURIComponent(id)}`);
    if (session.payment_status !== 'paid') {
      return html(messagePage('Payment not finished yet', 'Your licence key appears here as soon as the payment goes through. Reload this page in a moment.'), 202);
    }
    key = await issueForSession(env, session);
  }
  return html(keyPage(key), 200);
}

async function webhook(request, env) {
  const body = await request.text();
  let event;
  try {
    event = await verifyWebhook(body, request.headers.get('stripe-signature'), env.STRIPE_WEBHOOK_SECRET);
  } catch (e) {
    return json({ error: 'signature', detail: e.message }, 400);
  }
  const object = event.data?.object ?? {};
  switch (event.type) {
    case 'checkout.session.completed':
    case 'checkout.session.async_payment_succeeded':
      if (object.payment_status === 'paid') await issueForSession(env, object);
      break;
    case 'charge.refunded':
      if (object.refunded) await revokePayment(env, object.payment_intent, 'refunded');
      break;
    case 'charge.dispute.created':
      await revokePayment(env, object.payment_intent, 'disputed');
      break;
  }
  return json({ received: true }, 200);
}

/** Issues (or returns) the key for a paid Checkout Session. Idempotent: the licence id comes from the session id. */
export async function issueForSession(env, session) {
  const existing = await env.LICENCES.get(`session:${session.id}`);
  if (existing) return existing;
  const idBytes = await licenceIdFor(session.id);
  const id = toHex(idBytes);
  const signingKey = await importSigningKey(env.LICENCE_SIGNING_KEY);
  const key = await issueKey(signingKey, idBytes, new Date((session.created ?? Date.now() / 1000) * 1000));
  const record = (await getRecord(env, id)) ?? {
    id,
    session: session.id,
    paymentIntent: session.payment_intent ?? null,
    issuedAt: new Date().toISOString(),
    revoked: null,
    instances: [],
  };
  await putRecord(env, record);
  if (record.paymentIntent) await env.LICENCES.put(`pi:${record.paymentIntent}`, id);
  await env.LICENCES.put(`session:${session.id}`, key);
  return key;
}

async function revokePayment(env, paymentIntent, reason) {
  if (!paymentIntent) return;
  const id = await env.LICENCES.get(`pi:${paymentIntent}`);
  const record = id && (await getRecord(env, id));
  if (!record || record.revoked) return;
  record.revoked = { reason, at: new Date().toISOString() };
  await putRecord(env, record);
}

async function licenceFor(env, key) {
  const parsed = await parseKey(key, await verifyKeyFrom(env));
  if (!parsed) return { error: json({ error: 'invalid_key' }, 400) };
  const record = await getRecord(env, parsed.id);
  if (!record) return { error: json({ status: 'unknown', error: 'unknown_key' }, 404) };
  return { record };
}

async function activate(body, env) {
  const { record, error } = await licenceFor(env, body.key);
  if (error) return error;
  if (record.revoked) return json({ error: 'revoked', reason: record.revoked.reason }, 403);
  const machine = String(body.machine ?? '').slice(0, 128);
  if (!/^[0-9a-f]{16,128}$/.test(machine)) return json({ error: 'bad_request' }, 400);
  const existing = record.instances.find((i) => i.machine === machine);
  if (existing) return json({ instanceId: existing.id }, 200);
  const max = Number(env.MAX_ACTIVATIONS ?? DEFAULT_MAX_ACTIVATIONS);
  if (record.instances.length >= max) return json({ error: 'limit', max }, 409);
  const instance = {
    id: toHex(crypto.getRandomValues(new Uint8Array(12))),
    machine,
    name: String(body.name ?? '').slice(0, 64),
    activatedAt: new Date().toISOString(),
  };
  record.instances.push(instance);
  await putRecord(env, record);
  return json({ instanceId: instance.id }, 200);
}

async function validate(body, env) {
  const { record, error } = await licenceFor(env, body.key);
  if (error) return error;
  if (record.revoked) return json({ status: 'revoked' }, 200);
  const active = record.instances.some((i) => i.id === body.instanceId);
  return json({ status: active ? 'active' : 'inactive' }, 200);
}

async function deactivate(body, env) {
  const { record, error } = await licenceFor(env, body.key);
  if (error) return error;
  record.instances = record.instances.filter((i) => i.id !== body.instanceId);
  await putRecord(env, record);
  return json({ ok: true }, 200);
}

let cachedVerifyKey = null;
async function verifyKeyFrom(env) {
  if (cachedVerifyKey?.source === env.LICENCE_SIGNING_KEY) return cachedVerifyKey.key;
  // The public half of the signing JWK (drop d) verifies the keys this service issued.
  const { d, key_ops, ...publicJwk } = typeof env.LICENCE_SIGNING_KEY === 'string' ? JSON.parse(env.LICENCE_SIGNING_KEY) : env.LICENCE_SIGNING_KEY;
  const key = await crypto.subtle.importKey('jwk', publicJwk, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
  cachedVerifyKey = { source: env.LICENCE_SIGNING_KEY, key };
  return key;
}

async function getRecord(env, id) {
  const text = await env.LICENCES.get(`licence:${id}`);
  return text ? JSON.parse(text) : null;
}

async function putRecord(env, record) {
  await env.LICENCES.put(`licence:${record.id}`, JSON.stringify(record));
}

function json(value, status) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

function html(body, status) {
  return new Response(body, { status, headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' } });
}
