import { defineConfig } from '@playwright/test';
const pagesPreview = process.env.PLAYGROUND_PAGES_PREVIEW === '1';
if (pagesPreview && process.env.PLAYGROUND_URL) throw new Error('Choose a local Pages preview or a deployed PLAYGROUND_URL.');
const localURL = pagesPreview ? 'http://127.0.0.1:8765/XamlG/' : 'http://127.0.0.1:8765/';
export default defineConfig({
  testDir: './tests', timeout: 180000, expect: { timeout: 120000 }, workers: 1,
  // Public Pages/CDN requests can return transient 503s for otherwise valid
  // assets. Keep one complete retry and its failure evidence on that host;
  // candidate builds remain strict and never retry.
  retries: process.env.CI && process.env.PLAYGROUND_URL ? 1 : 0,
  use: { baseURL: process.env.PLAYGROUND_URL || (pagesPreview ? 'https://wieslawsoltes.github.io/XamlG/' : localURL), viewport: { width: 1440, height: 1000 }, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  reporter: [['list'], ['html', { open: 'never' }]],
  webServer: process.env.PLAYGROUND_URL ? undefined : {
    command: `python3 ../../scripts/serve-playground.py --port 8765 --directory ../../artifacts/playground/wwwroot --base-path ${pagesPreview ? '/XamlG/' : '/'}`,
    url: localURL, reuseExistingServer: !pagesPreview && !process.env.CI, timeout: 10000
  }
});
