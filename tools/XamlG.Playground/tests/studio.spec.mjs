import { test, expect } from './studio-fixture.mjs';

test.beforeEach(async ({ page }) => {
  page.on('pageerror', error => console.log('BROWSER ERROR:', error.stack ?? error.message));
  page.on('console', message => { if (message.type() === 'error') console.log('BROWSER CONSOLE:', message.text()); });
});
test.afterEach(async ({ page }, info) => {
  if (info.status !== info.expectedStatus) console.log('STUDIO STATE:', (await page.locator('body').innerText()).slice(-15000));
});

test('compiles and runs the production compiler with a real Avalonia canvas', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await expect(page.locator('#avalonia-preview canvas').first()).toBeVisible();
  await page.locator('[data-tab-id="visual-tree"]').click();
  await expect(page.locator('.inspector-body:visible')).toContainText('StackPanel');
  await page.locator('[data-tab-id="syntax"]').click();
  await expect(page.locator('.inspector-body:visible')).toContainText('xmlns');
  await page.screenshot({ path: '../../artifacts/playground-studio.png', fullPage: true });
});

test('source generator output handles code-behind and private event methods', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
  await page.getByLabel('Example', { exact: true }).selectOption('1');
  await page.keyboard.press('Escape');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.locator('[data-tab-id="visual-tree"]').click();
  await expect(page.locator('.inspector-body:visible')).toContainText('CounterView');
});

test('mobile layout stays within the viewport and supports light theme', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: 'Toggle color theme' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 2);
  expect(overflow).toBe(false);
});
