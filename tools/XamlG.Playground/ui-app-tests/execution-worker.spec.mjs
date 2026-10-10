import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

const supervisor = readFileSync(new URL('../wwwroot/ui-execution-worker-host.js', import.meta.url), 'utf8');

async function mount(page) {
  await page.setContent('<iframe title="Worker supervisor" sandbox="allow-scripts"></iframe>');
  await page.evaluate(source => {
    const frame = document.querySelector('iframe');
    const testHost = window.workerTest = { messages: [] };
    const channel = new MessageChannel();
    channel.port1.onmessage = event => testHost.messages.push(event.data);
    channel.port1.start();
    testHost.send = message => channel.port1.postMessage(message);
    addEventListener('message', event => {
      if (event.source === frame.contentWindow && event.data?.type === 'xamlg-worker-supervisor-ready')
        frame.contentWindow.postMessage({ type: 'xamlg-worker-connect' }, '*', [channel.port2]);
    });
    frame.srcdoc = '<script type="module">' + source + '</script>';
  }, supervisor);
  await expect.poll(() => page.evaluate(() => window.workerTest.messages)).toContainEqual({ type: 'connected' });
}

// This executes the actual supervisor, in its actual opaque-origin/CSP topology.
// A module-worker entry script fails here on Chromium before any .NET code starts.
test('opaque supervisor starts a classic worker which dynamically imports trusted blob modules', async ({ page }) => {
  await mount(page);
  await page.evaluate(() => window.workerTest.send({ type: 'bootstrap', files: [], source: `
    addEventListener('message', async event => {
      if (event.data.type !== 'bootstrap') return;
      const url = URL.createObjectURL(new Blob(['export const value = 42;'], { type: 'text/javascript' }));
      try {
        const imported = await import(url);
        postMessage({ type: 'ready', limits: { value: imported.value, origin: globalThis.origin } });
      } catch (error) { postMessage({ type: 'fatal', error: String(error) }); }
      finally { URL.revokeObjectURL(url); }
    }, { once: true });` }));
  await expect.poll(() => page.evaluate(() => window.workerTest.messages)).toContainEqual({
    type: 'ready', limits: { value: 42, origin: 'null' }
  });
});

test('supervisor terminates a CPU-bound worker without blocking the host or granting an external command', async ({ page }) => {
  await mount(page);
  await page.evaluate(() => window.workerTest.send({ type: 'bootstrap', files: [], source: `
    addEventListener('message', event => {
      if (event.data.type === 'bootstrap') postMessage({ type: 'ready', limits: {} });
      else if (event.data.type === 'call') { while (true) {} }
    });` }));
  await expect.poll(() => page.evaluate(() => window.workerTest.messages.some(m => m.type === 'ready'))).toBe(true);
  await page.evaluate(() => window.workerTest.send({ type: 'call', id: 1, method: 'state', json: '{}' }));
  await expect.poll(() => page.evaluate(() => window.workerTest.messages.find(m => m.type === 'fatal')?.error)).toContain('deadline');
  expect(await page.evaluate(() => 21 * 2)).toBe(42);
  await page.evaluate(() => window.workerTest.send({ type: 'call', id: 2, method: 'tool', json: '{}' }));
  expect(await page.evaluate(() => window.workerTest.messages.filter(m => m.type === 'result'))).toEqual([]);
});
