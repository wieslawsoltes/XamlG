import { test, expect } from '@playwright/test';
import { createHash } from 'node:crypto';

// Fetch SRI is a browser primitive and remains available when opaque contexts do
// not expose crypto.subtle. Corrupt bytes must never reach the execution worker.
test('opaque asset loader enforces integrity without depending on Web Crypto', async ({ page }) => {
  const text = 'trusted runtime bytes';
  const integrity = 'sha256-' + createHash('sha256').update(text).digest('base64');
  await page.setContent('<iframe title="Integrity fixture" sandbox="allow-scripts"></iframe>');
  await page.evaluate(() => { document.querySelector('iframe').srcdoc = '<body>Integrity</body>'; });
  const frame = page.frameLocator('iframe');
  const result = await frame.locator('body').evaluate(async (_, { text, integrity }) => {
    const url = URL.createObjectURL(new Blob([text]));
    try {
      const valid = await (await fetch(url, { integrity })).text();
      let rejected = false;
      try { await fetch(url, { integrity: 'sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=' }); }
      catch { rejected = true; }
      return { valid, rejected, origin: globalThis.origin };
    } finally { URL.revokeObjectURL(url); }
  }, { text, integrity });
  expect(result).toEqual({ valid: text, rejected: true, origin: 'null' });
});
