// The two Stripe pieces the licence service needs, without the Stripe SDK (Workers have fetch and WebCrypto):
// webhook signature checks (https://docs.stripe.com/webhooks#verify-manually) and form-encoded API calls.

const TOLERANCE_SECONDS = 300;

/**
 * Checks a webhook's Stripe-Signature header (t=…,v1=…) against the endpoint secret and returns the parsed event.
 * Throws when the signature is missing, wrong or older than five minutes (replays).
 */
export async function verifyWebhook(body, header, secret, nowSeconds = Date.now() / 1000) {
  if (!header || !secret) throw new Error('missing signature');
  let timestamp = null;
  const signatures = [];
  for (const part of header.split(',')) {
    const [k, v] = part.split('=', 2);
    if (k === 't') timestamp = Number(v);
    else if (k === 'v1' && v) signatures.push(v);
  }
  if (!Number.isFinite(timestamp) || signatures.length === 0) throw new Error('malformed signature');
  if (Math.abs(nowSeconds - timestamp) > TOLERANCE_SECONDS) throw new Error('signature too old');
  const expected = await hmacHex(secret, `${timestamp}.${body}`);
  if (!signatures.some((s) => constantTimeEqual(s, expected))) throw new Error('bad signature');
  return JSON.parse(body);
}

/** The header Stripe would send for `body` (tests and the local fake). */
export async function signWebhook(body, secret, timestamp = Math.floor(Date.now() / 1000)) {
  return `t=${timestamp},v1=${await hmacHex(secret, `${timestamp}.${body}`)}`;
}

/** A Stripe API call: form-encoded body, JSON answer. Throws with Stripe's message on errors. */
export async function stripeApi(env, method, path, params) {
  const base = env.STRIPE_API_BASE || 'https://api.stripe.com';
  const response = await fetch(`${base}/v1${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${env.STRIPE_SECRET_KEY}`,
      ...(params ? { 'Content-Type': 'application/x-www-form-urlencoded' } : {}),
    },
    body: params ? new URLSearchParams(params).toString() : undefined,
  });
  const json = await response.json();
  if (!response.ok) throw new Error(`stripe ${method} ${path}: ${json?.error?.message ?? response.status}`);
  return json;
}

async function hmacHex(secret, message) {
  const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const mac = new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(message)));
  return [...mac].map((b) => b.toString(16).padStart(2, '0')).join('');
}

function constantTimeEqual(a, b) {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
