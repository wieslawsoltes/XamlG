import { test as base, expect } from '@playwright/test';

// The candidate is served with the real Pages base URI and browser origin. Only
// static site requests are redirected to local assets; companion HTTP/WebSocket
// traffic, CORS and browser origin checks run normally. Public verification uses
// the same tests without any interception.
export const test = base.extend({
  context: async ({ context, baseURL }, use) => {
    const origin = new URL(baseURL).origin;
    if (process.env.XAMLG_TEST_MCP_URL && origin.startsWith('https:'))
      await context.grantPermissions(['local-network-access'], { origin });
    if (process.env.PLAYGROUND_PAGES_PREVIEW === '1') {
      await context.route(`${origin}/**`, async route => {
        const address = new URL(route.request().url());
        const response = await route.fetch({ url: `http://127.0.0.1:8765${address.pathname}${address.search}`, maxRedirects: 0 });
        try { await route.fulfill({ response }); }
        finally { await response.dispose(); }
      });
    }
    await use(context);
  }
});
export { expect };
