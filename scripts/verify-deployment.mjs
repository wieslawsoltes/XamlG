import assert from 'node:assert/strict';
import { setTimeout } from 'node:timers/promises';

const base = process.env.PLAYGROUND_URL;
const expected = process.env.PLAYGROUND_EXPECTED_COMMIT;
assert(base && expected && /^[0-9a-f]{40}$/.test(expected), 'Provide the deployment URL and exact expected commit.');
let failure;
for (let attempt = 0; attempt < 12; attempt++) {
  try {
    const url = new URL('build.json', base);
    url.searchParams.set('revision', expected);
    url.searchParams.set('attempt', String(attempt));
    const response = await fetch(url, { cache: 'no-store', signal: AbortSignal.timeout(15000) });
    assert(response.ok, `Build identity returned HTTP ${response.status}.`);
    const identity = await response.json();
    assert.equal(identity.commit, expected, 'The CDN is not serving the expected source revision.');
    assert.equal(identity.application, 'XamlG Compiler Studio');
    console.log(`Verified public deployment commit ${identity.commit}`);
    process.exit(0);
  } catch (error) {
    failure = error;
    if (attempt < 11) await setTimeout(5000);
  }
}
throw failure;
