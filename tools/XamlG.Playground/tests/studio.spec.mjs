import { test, expect } from '@playwright/test';

test('compiles and runs the production compiler with a real Avalonia canvas', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await expect(page.locator('#avalonia-preview canvas').first()).toBeVisible();
  await page.getByRole('tab', { name: 'Visual tree', exact: true }).click();
  await expect(page.locator('.inspector-body')).toContainText('StackPanel');
  await page.getByRole('tab', { name: 'Syntax', exact: true }).click();
  await expect(page.locator('.inspector-body')).toContainText('xmlns');
  await page.screenshot({ path: '../../artifacts/playground-studio.png', fullPage: true });
});

test('source generator output handles code-behind and private event methods', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByLabel('Example', { exact: true }).selectOption('1');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.getByRole('tab', { name: 'Visual tree', exact: true }).click();
  await expect(page.locator('.inspector-body')).toContainText('CounterView');
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
