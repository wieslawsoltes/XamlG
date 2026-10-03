import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests', timeout: 180000, expect: { timeout: 120000 }, workers: 1,
  use: { baseURL: process.env.PLAYGROUND_URL || 'http://127.0.0.1:8765/', viewport: { width: 1440, height: 1000 }, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  reporter: [['list'], ['html', { open: 'never' }]],
  webServer: process.env.PLAYGROUND_URL ? undefined : {
    command: 'python3 ../../scripts/serve-playground.py --port 8765 --directory ../../artifacts/playground/wwwroot',
    url: 'http://127.0.0.1:8765/', reuseExistingServer: true, timeout: 10000
  }
});
