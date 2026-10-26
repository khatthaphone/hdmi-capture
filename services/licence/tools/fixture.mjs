// Writes the cross-language test fixture (a key from a throwaway signing key) for the apps' verifier tests:
//   node tools/fixture.mjs > ../../Apps/Linux/tests/HDMICaptain.Linux.App.Tests/Fixtures/licence-key.json
import { generateKeyPair, importSigningKey, issueKey, licenceIdFor, toHex } from '../src/keys.js';

const { privateJwk, publicSpki } = await generateKeyPair();
const id = await licenceIdFor('cs_test_fixture');
const key = await issueKey(await importSigningKey(privateJwk), id, new Date(Date.UTC(2026, 9, 26)));
console.log(JSON.stringify({ publicKey: publicSpki, key, id: toHex(id), issued: '2026-10-26' }, null, 2));
