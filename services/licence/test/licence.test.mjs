// node --test test/ — the Worker against an in-memory KV and a stubbed Stripe API.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import worker, { issueForSession } from '../src/worker.js';
import { generateKeyPair, importSigningKey, importVerifyKey, issueKey, parseKey, licenceIdFor } from '../src/keys.js';
import { signWebhook, verifyWebhook } from '../src/stripe.js';

async function setup({ max = '3' } = {}) {
  const { privateJwk, publicSpki } = await generateKeyPair();
  const store = new Map();
  const env = {
    LICENCES: { get: async (k) => store.get(k) ?? null, put: async (k, v) => void store.set(k, v) },
    STRIPE_SECRET_KEY: 'sk_test_x',
    STRIPE_WEBHOOK_SECRET: 'whsec_test',
    STRIPE_PRICE_ID: 'price_x',
    LICENCE_SIGNING_KEY: JSON.stringify(privateJwk),
    MAX_ACTIVATIONS: max,
  };
  return { env, store, publicSpki };
}

const call = (env, path, body) =>
  worker.fetch(new Request(`https://licence.test${path}`, { method: 'POST', body: JSON.stringify(body) }), env).then(async (r) => ({ status: r.status, body: await r.json() }));

async function webhook(env, type, object, secret = env.STRIPE_WEBHOOK_SECRET) {
  const body = JSON.stringify({ id: 'evt_1', type, data: { object } });
  return worker.fetch(new Request('https://licence.test/stripe/webhook', { method: 'POST', headers: { 'stripe-signature': await signWebhook(body, secret) }, body }), env);
}

const paidSession = { id: 'cs_test_abc', object: 'checkout.session', created: 1793000000, payment_status: 'paid', payment_intent: 'pi_abc' };

test('keys verify with the public key and fail when altered or signed by another key', async () => {
  const { privateJwk, publicSpki } = await generateKeyPair();
  const other = await generateKeyPair();
  const key = await issueKey(await importSigningKey(privateJwk), await licenceIdFor('cs_1'), new Date(Date.UTC(2026, 9, 26)));
  assert.match(key, /^HC1-[A-Za-z0-9_-]{107}$/);
  const parsed = await parseKey(key, await importVerifyKey(publicSpki));
  assert.equal(parsed.issued.toISOString().slice(0, 10), '2026-10-26');
  assert.equal(await parseKey(` ${key.slice(0, 40)}\n${key.slice(40)} `, await importVerifyKey(publicSpki)).then((p) => p?.id), parsed.id);
  const flipped = key.slice(0, 10) + (key[10] === 'A' ? 'B' : 'A') + key.slice(11);
  assert.equal(await parseKey(flipped, await importVerifyKey(publicSpki)), null);
  assert.equal(await parseKey(key, await importVerifyKey(other.publicSpki)), null);
  assert.equal(await parseKey('HC1-not a key', await importVerifyKey(publicSpki)), null);
});

test('webhook signatures are checked, including replays', async () => {
  const body = '{"type":"x"}';
  const header = await signWebhook(body, 'whsec_a', 1000);
  assert.deepEqual(await verifyWebhook(body, header, 'whsec_a', 1100), { type: 'x' });
  await assert.rejects(verifyWebhook(body, header, 'whsec_b', 1100), /bad signature/);
  await assert.rejects(verifyWebhook(body, header, 'whsec_a', 2000), /too old/);
  await assert.rejects(verifyWebhook(body + ' ', header, 'whsec_a', 1100), /bad signature/);
  const { env } = await setup();
  assert.equal((await webhook(env, 'checkout.session.completed', paidSession, 'whsec_wrong')).status, 400);
});

test('a paid checkout issues one licence, whether the webhook or the success page comes first', async () => {
  const { env, store, publicSpki } = await setup();
  assert.equal((await webhook(env, 'checkout.session.completed', paidSession)).status, 200);
  const key = store.get('session:cs_test_abc');
  assert.ok(key);
  const again = await issueForSession(env, paidSession);
  assert.equal(again, key);
  assert.equal([...store.keys()].filter((k) => k.startsWith('licence:')).length, 1);
  const parsed = await parseKey(key, await importVerifyKey(publicSpki));
  assert.ok(store.has(`licence:${parsed.id}`));
  // An unpaid (async) session issues nothing.
  await webhook(env, 'checkout.session.completed', { ...paidSession, id: 'cs_test_unpaid', payment_status: 'unpaid' });
  assert.equal(store.get('session:cs_test_unpaid'), undefined);
});

test('the success page shows the key, asking Stripe when the webhook is late', async (t) => {
  const { env } = await setup();
  const realFetch = globalThis.fetch;
  t.after(() => (globalThis.fetch = realFetch));
  globalThis.fetch = async (url) => {
    assert.match(String(url), /\/v1\/checkout\/sessions\/cs_test_late$/);
    return new Response(JSON.stringify({ ...paidSession, id: 'cs_test_late' }), { status: 200 });
  };
  const response = await worker.fetch(new Request('https://licence.test/success?session_id=cs_test_late'), env);
  assert.equal(response.status, 200);
  assert.match(await response.text(), /HC1-[A-Za-z0-9_-]{107}/);
  const bad = await worker.fetch(new Request('https://licence.test/success?session_id=<script>'), env);
  assert.equal(bad.status, 400);
});

test('activation is limited per licence, repeat activation on one machine reuses the seat, deactivate frees it', async () => {
  const { env } = await setup({ max: '2' });
  const key = await issueForSession(env, paidSession);
  const a = await call(env, '/v1/activate', { key, machine: 'a'.repeat(64), name: 'desk' });
  assert.equal(a.status, 200);
  assert.equal((await call(env, '/v1/activate', { key, machine: 'a'.repeat(64) })).body.instanceId, a.body.instanceId);
  assert.equal((await call(env, '/v1/activate', { key, machine: 'b'.repeat(64) })).status, 200);
  const third = await call(env, '/v1/activate', { key, machine: 'c'.repeat(64) });
  assert.deepEqual([third.status, third.body.error], [409, 'limit']);
  assert.equal((await call(env, '/v1/validate', { key, instanceId: a.body.instanceId })).body.status, 'active');
  await call(env, '/v1/deactivate', { key, instanceId: a.body.instanceId });
  assert.equal((await call(env, '/v1/validate', { key, instanceId: a.body.instanceId })).body.status, 'inactive');
  assert.equal((await call(env, '/v1/activate', { key, machine: 'c'.repeat(64) })).status, 200);
});

test('refunds and disputes revoke the licence', async () => {
  const { env } = await setup();
  const key = await issueForSession(env, paidSession);
  const a = await call(env, '/v1/activate', { key, machine: 'a'.repeat(64) });
  // A partial refund keeps it.
  await webhook(env, 'charge.refunded', { refunded: false, payment_intent: 'pi_abc' });
  assert.equal((await call(env, '/v1/validate', { key, instanceId: a.body.instanceId })).body.status, 'active');
  await webhook(env, 'charge.refunded', { refunded: true, payment_intent: 'pi_abc' });
  assert.equal((await call(env, '/v1/validate', { key, instanceId: a.body.instanceId })).body.status, 'revoked');
  assert.equal((await call(env, '/v1/activate', { key, machine: 'b'.repeat(64) })).status, 403);

  const other = await setup();
  const key2 = await issueForSession(other.env, paidSession);
  await webhook(other.env, 'charge.dispute.created', { payment_intent: 'pi_abc' });
  assert.equal((await call(other.env, '/v1/activate', { key: key2, machine: 'a'.repeat(64) })).status, 403);
});

test('bad requests', async () => {
  const { env } = await setup();
  assert.equal((await call(env, '/v1/activate', { key: 'HC1-nope', machine: 'a'.repeat(64) })).status, 400);
  const key = await issueForSession(env, paidSession);
  assert.equal((await call(env, '/v1/activate', { key, machine: 'not hex' })).status, 400);
  const { env: otherEnv } = await setup();
  // Signed by another service's key: invalid there.
  assert.equal((await call(otherEnv, '/v1/validate', { key, instanceId: 'x' })).status, 400);
});
