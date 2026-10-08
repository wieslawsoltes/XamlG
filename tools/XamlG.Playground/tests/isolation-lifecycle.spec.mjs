import { test, expect } from './studio-fixture.mjs';

async function studio(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
}

test('reset cancels a runtime which has not sent its ready message', async ({ page }) => {
  await page.route('**/sandbox.html', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Startup deliberately paused</title>' }));
  await studio(page);
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('run-isolated').click();
  const frame = page.locator('iframe[title="XamlG isolated preview"]');
  await expect(frame).toHaveCount(1);
  await expect(page.locator('.statusbar')).toContainText('Starting isolated execution');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('reset-isolation').click();
  await expect(frame).toHaveCount(0);
  await expect(page.locator('.statusbar')).toContainText('Isolated execution frame discarded');
  await expect(page.getByTestId('run-preview')).toBeEnabled();
  // A new execution must work without reloading the editor after the cancelled startup.
  await page.unroute('**/sandbox.html');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('run-isolated').click();
  await expect(page.locator('.statusbar')).toContainText('Isolated preview running');
  await expect(frame).toHaveCount(1);
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('reset-isolation').click();
  await expect(frame).toHaveCount(0);
});

test('startup requests are bounded and all settle when their owning frame is discarded', async ({ page }) => {
  await page.route('**/sandbox.html', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>No handshake</title>' }));
  await studio(page);
  const result = await page.evaluate(async () => {
    const transport = await import(new URL('sandbox-client.js', document.baseURI).href);
    const host = document.createElement('div');
    document.body.appendChild(host);
    const id = transport.create(host, new URL('sandbox.html', document.baseURI).href);
    const queued = Array.from({ length: 4 }, () => transport.request(id, 'inspect', {}).then(() => 'unexpected success', error => error.message));
    const overflow = await transport.request(id, 'inspect', {}).then(() => 'unexpected success', error => error.message);
    transport.dispose(id);
    transport.dispose(id);
    const settled = await Promise.all(queued);
    const frames = host.querySelectorAll('iframe').length;
    host.remove();
    return { immediateHandle: Number.isSafeInteger(id), overflow, settled, frames };
  });
  expect(result.immediateHandle).toBe(true);
  expect(result.overflow).toContain('Too many pending');
  expect(result.settled).toHaveLength(4);
  expect(result.settled.every(message => message.includes('was reset'))).toBe(true);
  expect(result.frames).toBe(0);
});
