import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../wwwroot/ui-archive-storage.js', import.meta.url), 'utf8');
async function initialize(page) {
  await page.route('https://ui-storage.test/**', route => route.fulfill({ contentType: route.request().url().endsWith('.js') ? 'text/javascript' : 'text/html', body: route.request().url().endsWith('.js') ? source : '<!doctype html><title>Private archive test</title>' }));
  await page.goto('https://ui-storage.test/');
  await page.evaluate(async () => { window.storage = await import('/storage.js'); });
}
test('IndexedDB archive compare-and-swap rejects concurrent stale writes and deletes', async ({ page, context }) => {
  await initialize(page); const other = await context.newPage(); await initialize(other);
  const version = await page.evaluate(() => window.storage.write('workspace', '{"value":1}', null));
  expect(await other.evaluate(() => window.storage.read('workspace'))).toEqual({ version, json: '{"value":1}' });
  const next = await other.evaluate(version => window.storage.write('workspace', '{"value":2}', version), version);
  await expect(page.evaluate(version => window.storage.write('workspace', '{}', version), version)).rejects.toThrow('storage_conflict');
  await expect(page.evaluate(version => window.storage.forget('workspace', version), version)).rejects.toThrow('storage_conflict');
  expect((await page.evaluate(() => window.storage.read('workspace'))).version).toBe(next);
  await page.evaluate(version => window.storage.forget('workspace', version), next);
  expect(await other.evaluate(() => window.storage.read('workspace'))).toBeNull();
});
test('private archives survive page reload and enforce workspace separation', async ({ page }) => {
  await initialize(page);
  await page.evaluate(() => window.storage.write('one', '{"state":42}', null));
  await initialize(page);
  expect((await page.evaluate(() => window.storage.read('one'))).json).toBe('{"state":42}');
  expect(await page.evaluate(() => window.storage.read('two'))).toBeNull();
  await expect(page.evaluate(() => window.storage.write('', '{}', null))).rejects.toThrow('invalid_workspace');
});
