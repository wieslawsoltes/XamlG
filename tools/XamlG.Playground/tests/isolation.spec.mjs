import { test, expect } from './studio-fixture.mjs';

test('isolated execution has a real Avalonia view but cannot access the editor DOM or storage', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.evaluate(() => localStorage.setItem('xamlg.isolation-canary', 'editor-only'));
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('run-isolated').click();
  await expect(page.locator('.statusbar')).toContainText('Isolated preview running');
  const frameElement = page.locator('iframe[title="XamlG isolated preview"]');
  await expect(frameElement).toHaveAttribute('sandbox', 'allow-scripts');
  const frame = page.frames().find(frame => new URL(frame.url()).pathname.endsWith('/sandbox.html'));
  const boundary = await frame.evaluate(() => {
    let parentBlocked = false, storageBlocked = false;
    try { parent.document.querySelector('.studio').remove(); } catch { parentBlocked = true; }
    try { localStorage.getItem('xamlg.isolation-canary'); } catch { storageBlocked = true; }
    return { origin: globalThis.origin, parentBlocked, storageBlocked };
  });
  expect(boundary).toEqual({ origin: 'null', parentBlocked: true, storageBlocked: true });
  await expect(frame.locator('canvas').first()).toBeVisible();
  await expect(page.locator('.studio')).toBeVisible();
  expect(await page.evaluate(() => localStorage.getItem('xamlg.isolation-canary'))).toBe('editor-only');
  await page.locator('[data-tab-id="visual-tree"]').click();
  await expect(page.locator('.inspector-body:visible')).toContainText('StackPanel');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('reset-isolation').click();
  await expect(frameElement).toHaveCount(0);
});
