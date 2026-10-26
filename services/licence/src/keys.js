// Licence keys (task 119): "HC1-" + base64url(payload ‖ signature).
// payload, 16 bytes: version (1), product (1 = Pro), licence id (12 bytes), issued day (uint16 BE, days since 2026-01-01).
// signature: ECDSA P-256 / SHA-256 over the payload as raw r ‖ s (64 bytes), the format WebCrypto produces and .NET reads
// as DSASignatureFormat.IeeeP1363FixedFieldConcatenation. The apps verify keys offline with the public key; only this
// service holds the private one.

export const PREFIX = 'HC1-';
export const PRODUCT_PRO = 1;
const VERSION = 1;
const PAYLOAD_BYTES = 16;
const SIGNATURE_BYTES = 64;
const EPOCH_MS = Date.UTC(2026, 0, 1);
const ECDSA = { name: 'ECDSA', namedCurve: 'P-256' };
const SIGN = { name: 'ECDSA', hash: 'SHA-256' };

export async function importSigningKey(jwk) {
  return crypto.subtle.importKey('jwk', typeof jwk === 'string' ? JSON.parse(jwk) : jwk, ECDSA, false, ['sign']);
}

/** The public key as base64 SPKI (what the apps embed). */
export async function importVerifyKey(spkiBase64) {
  return crypto.subtle.importKey('spki', fromBase64(spkiBase64), ECDSA, false, ['verify']);
}

export async function generateKeyPair() {
  const pair = await crypto.subtle.generateKey(ECDSA, true, ['sign', 'verify']);
  return {
    privateJwk: await crypto.subtle.exportKey('jwk', pair.privateKey),
    publicSpki: toBase64(new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey))),
  };
}

/** The 12-byte licence id for a Stripe Checkout session: the same session always gives the same licence. */
export async function licenceIdFor(sessionId) {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(`hdmicaptain-licence:${sessionId}`)));
  return digest.slice(0, 12);
}

export async function issueKey(signingKey, id, issuedAt = new Date()) {
  if (id.length !== 12) throw new Error('licence id must be 12 bytes');
  const payload = new Uint8Array(PAYLOAD_BYTES);
  payload[0] = VERSION;
  payload[1] = PRODUCT_PRO;
  payload.set(id, 2);
  const day = Math.max(0, Math.min(0xffff, Math.floor((issuedAt.getTime() - EPOCH_MS) / 86_400_000)));
  payload[14] = day >> 8;
  payload[15] = day & 0xff;
  const signature = new Uint8Array(await crypto.subtle.sign(SIGN, signingKey, payload));
  const bytes = new Uint8Array(PAYLOAD_BYTES + SIGNATURE_BYTES);
  bytes.set(payload);
  bytes.set(signature, PAYLOAD_BYTES);
  return PREFIX + toBase64Url(bytes);
}

/** { id (hex), product, issued (Date) } for a genuine key, else null. Whitespace (from copy/paste) is ignored. */
export async function parseKey(key, verifyKey) {
  if (typeof key !== 'string') return null;
  const text = key.replace(/\s+/g, '');
  if (!text.startsWith(PREFIX)) return null;
  let bytes;
  try {
    bytes = fromBase64Url(text.slice(PREFIX.length));
  } catch {
    return null;
  }
  if (bytes.length !== PAYLOAD_BYTES + SIGNATURE_BYTES || bytes[0] !== VERSION) return null;
  const payload = bytes.slice(0, PAYLOAD_BYTES);
  const ok = await crypto.subtle.verify(SIGN, verifyKey, bytes.slice(PAYLOAD_BYTES), payload);
  if (!ok) return null;
  return {
    id: toHex(payload.slice(2, 14)),
    product: payload[1],
    issued: new Date(EPOCH_MS + ((payload[14] << 8) | payload[15]) * 86_400_000),
  };
}

export function toHex(bytes) {
  return [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export function toBase64(bytes) {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s);
}

export function fromBase64(text) {
  return Uint8Array.from(atob(text), (c) => c.charCodeAt(0));
}

function toBase64Url(bytes) {
  return toBase64(bytes).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromBase64Url(text) {
  if (!/^[A-Za-z0-9_-]+$/.test(text)) throw new Error('not base64url');
  const b64 = text.replace(/-/g, '+').replace(/_/g, '/');
  return fromBase64(b64 + '='.repeat((4 - (b64.length % 4)) % 4));
}
