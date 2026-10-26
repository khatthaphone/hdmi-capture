// Local licence service for development and end-to-end tests (Node 20+): the Worker's fetch handler over an in-memory
// KV, with a fake Stripe (Checkout page → paid → signed webhook) so nothing touches a Stripe account.
//
//   node dev/server.mjs [--port 8787] [--key .local/signing-key.jwk]
//
// Without --key it makes a throwaway signing key and prints its public half (pass it to the app or tests).
// Prints "listening <origin> <public key base64>" on the first line once it is ready.

import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import worker from '../src/worker.js';
import { generateKeyPair } from '../src/keys.js';
import { signWebhook } from '../src/stripe.js';

const args = process.argv.slice(2);
const arg = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : fallback;
};
const port = Number(arg('--port', '8787'));
let privateJwk;
let publicSpki = '';
const keyFile = arg('--key', null);
if (keyFile) {
  privateJwk = JSON.parse(readFileSync(keyFile, 'utf8'));
} else {
  ({ privateJwk, publicSpki } = await generateKeyPair());
}

const store = new Map();
const kv = {
  async get(k) {
    return store.has(k) ? store.get(k) : null;
  },
  async put(k, v) {
    store.set(k, v);
  },
};
const sessions = new Map();
let origin = '';
const webhookSecret = 'whsec_dev';
const env = {
  LICENCES: kv,
  STRIPE_SECRET_KEY: 'sk_test_dev',
  STRIPE_WEBHOOK_SECRET: webhookSecret,
  STRIPE_PRICE_ID: 'price_dev',
  LICENCE_SIGNING_KEY: JSON.stringify(privateJwk),
  MAX_ACTIVATIONS: arg('--max-activations', '3'),
};

// The fake Stripe answers under /fake-stripe; the Worker reaches it through STRIPE_API_BASE.
async function fakeStripe(url, method, body) {
  const path = url.pathname.slice('/fake-stripe'.length);
  if (method === 'POST' && path === '/v1/checkout/sessions') {
    const form = new URLSearchParams(body);
    const id = `cs_test_${Math.random().toString(36).slice(2, 12)}`;
    const session = {
      id,
      object: 'checkout.session',
      created: Math.floor(Date.now() / 1000),
      payment_status: 'unpaid',
      payment_intent: `pi_${id.slice(8)}`,
      success_url: form.get('success_url').replace('{CHECKOUT_SESSION_ID}', id),
      url: `${origin}/fake-stripe/pay/${id}`,
      metadata: { platform: form.get('metadata[platform]') },
    };
    sessions.set(id, session);
    return json(session);
  }
  const get = path.match(/^\/v1\/checkout\/sessions\/(cs_[A-Za-z0-9_]+)$/);
  if (method === 'GET' && get) return sessions.has(get[1]) ? json(sessions.get(get[1])) : json({ error: { message: 'No such session' } }, 404);
  const pay = path.match(/^\/pay\/(cs_[A-Za-z0-9_]+)$/);
  if (method === 'GET' && pay && sessions.has(pay[1])) {
    const session = sessions.get(pay[1]);
    session.payment_status = 'paid';
    await sendEvent('checkout.session.completed', session);
    return new Response(null, { status: 303, headers: { Location: session.success_url } });
  }
  const refund = path.match(/^\/refund\/(pi_[A-Za-z0-9_]+)$/);
  if (method === 'POST' && refund) {
    await sendEvent('charge.refunded', { id: `ch_${refund[1]}`, object: 'charge', refunded: true, payment_intent: refund[1] });
    return json({ ok: true });
  }
  return json({ error: { message: 'fake stripe: unknown call' } }, 404);
}

async function sendEvent(type, object) {
  const body = JSON.stringify({ id: `evt_${Math.random().toString(36).slice(2)}`, type, data: { object } });
  const request = new Request(`${origin}/stripe/webhook`, {
    method: 'POST',
    headers: { 'stripe-signature': await signWebhook(body, webhookSecret) },
    body,
  });
  const response = await worker.fetch(request, env);
  if (!response.ok) throw new Error(`webhook ${type}: ${response.status}`);
}

function json(value, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

const server = createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const body = Buffer.concat(chunks).toString('utf8');
  const url = new URL(req.url, origin);
  let response;
  try {
    if (url.pathname.startsWith('/fake-stripe/')) {
      response = await fakeStripe(url, req.method, body);
    } else {
      // Stripe API calls from the Worker go to the fake: patch fetch for this origin only.
      response = await worker.fetch(new Request(url, { method: req.method, headers: req.headers, body: ['GET', 'HEAD'].includes(req.method) ? undefined : body }), env);
    }
  } catch (e) {
    response = json({ error: String(e) }, 500);
  }
  res.writeHead(response.status, Object.fromEntries(response.headers));
  res.end(Buffer.from(await response.arrayBuffer()));
});

server.listen(port, '127.0.0.1', () => {
  origin = `http://127.0.0.1:${server.address().port}`;
  env.STRIPE_API_BASE = `${origin}/fake-stripe`;
  console.log(`listening ${origin} ${publicSpki || '(public key from --key file)'}`);
});
