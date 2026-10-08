import { test as base, expect } from '@playwright/test';

// The candidate is served with the real Pages base URI and browser origin. Only
// static site requests are redirected to local assets; companion HTTP/WebSocket
// traffic, CORS and browser origin checks run normally. Public verification uses
// the same tests without any interception.
export const test = base.extend({
  // Manual compilation scenarios intentionally inspect stale source/preview
  // boundaries. Live-update scenarios opt into the application's real defaults.
  liveUpdates: [false, { option: true }],
  context: async ({ context, baseURL, liveUpdates }, use) => {
    if (!liveUpdates) await context.addInitScript(() => {
      // Init scripts also run in about:blank and opaque sandbox documents.
      try { localStorage.setItem('xamlg.live-updates', JSON.stringify({ compile: false, preview: false })); } catch { }
    });
    const origin = new URL(baseURL).origin;
    if (process.env.XAMLG_TEST_MCP_URL && origin.startsWith('https:'))
      await context.grantPermissions(['local-network-access'], { origin });
    if (process.env.PLAYGROUND_PAGES_PREVIEW === '1') {
      // route.fetch bypasses the browser's per-origin connection pool. Bound the
      // hundreds of parallel WASM asset reads to the small local static server.
      let active = 0;
      const waiting = [];
      await context.route(`${origin}/**`, async route => {
        if (active < 8) active++; else await new Promise(resolve => waiting.push(resolve));
        try {
          const address = new URL(route.request().url());
          const response = await route.fetch({ url: `http://127.0.0.1:8765${address.pathname}${address.search}`, maxRedirects: 0 });
          try { await route.fulfill({ response }); }
          finally { await response.dispose(); }
        } finally {
          const next = waiting.shift();
          if (next) next(); else active--;
        }
      });
    }
    try { await use(context); }
    finally {
      // Startup-failure tests can finish while other WASM assets are still being
      // fetched. Finish our route callbacks, including response disposal, before
      // Playwright tears down their browser context.
      if (process.env.PLAYGROUND_PAGES_PREVIEW === '1')
        await context.unrouteAll({ behavior: 'wait' });
    }
  }
});
export { expect };
