import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './ui-app-tests', timeout: 30000, expect: { timeout: 10000 }, workers: 2, retries: 0,
  use: { viewport: { width: 1000, height: 800 }, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  reporter: [['list'], ['html', { outputFolder: 'ui-app-report', open: 'never' }]]
});
