// Makes the licence signing key pair (task 119). Run once per environment:
//   node tools/genkey.mjs .local/signing-key.jwk
// The private JWK goes to the Worker as a secret (wrangler secret put LICENCE_SIGNING_KEY < .local/signing-key.jwk) and
// into a password manager; it never goes into git or an app. The printed public key (base64 SPKI) goes into the apps
// (Apps/Linux/src/HDMICaptain.Linux.App/StripeLicence.cs, PublicKey).
import { writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { generateKeyPair } from '../src/keys.js';

const out = process.argv[2];
if (!out) {
  console.error('usage: node tools/genkey.mjs <private-key.jwk>');
  process.exit(2);
}
if (existsSync(out)) {
  console.error(`${out} exists; refusing to replace a signing key (every key it issued would stop verifying)`);
  process.exit(1);
}
const { privateJwk, publicSpki } = await generateKeyPair();
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, JSON.stringify(privateJwk), { mode: 0o600 });
console.log(publicSpki);
