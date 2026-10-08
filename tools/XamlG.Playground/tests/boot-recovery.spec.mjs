import { test, expect } from './studio-fixture.mjs';

test.use({ liveUpdates: true });
const frameworkImage = /\/_framework\/System\.IO\.Compression\.FileSystem\.[^/]+\.wasm(?:\?.*)?$/;
const ready = async page => {
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('preview updated automatically');
};

test('startup retries scripts and their dependencies while retaining one editor and agent registry', async ({ page }) => {
  const requests = new Map();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route(/\/(startup|studio|studio-storage|csharp-language|source-buffer|blazor\.webassembly)\.js(?:\?.*)?$/, route => {
    const name = new URL(route.request().url()).pathname.split('/').at(-1);
    const count = (requests.get(name) ?? 0) + 1;
    requests.set(name, count);
    return count === 1 ? route.fulfill({ status: 503, body: 'Temporary script outage' }) : route.fallback();
  });
  await page.goto('./');
  await ready(page);
  expect(Object.fromEntries(requests)).toEqual({ 'startup.js': 2, 'studio.js': 2, 'blazor.webassembly.js': 2, 'csharp-language.js': 2, 'source-buffer.js': 2, 'studio-storage.js': 2 });
  expect(await page.evaluate(() => monaco.editor.getModels().filter(model => /\/(View\.axaml|Code\.cs)$/.test(model.uri.path)).length)).toBe(2);
  await page.getByTestId('agent-workbench').click();
  await expect(page.getByRole('region', { name: 'Coding agent workbench' }).getByLabel('API key', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('startup retries transient framework assembly downloads without reloading', async ({ page }) => {
  let requests = 0, navigations = 0;
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
  await page.route(frameworkImage, route => ++requests <= 2
    ? route.fulfill({ status: 503, body: 'Temporary runtime outage' }) : route.fallback());
  await page.goto('./');
  await ready(page);
  expect({ requests, navigations }).toEqual({ requests: 3, navigations: 1 });
  expect(errors).toEqual([]);
});

test('an exhausted dependency download offers reload without multiplying parent retries', async ({ page }) => {
  let unavailable = true, requests = 0;
  await page.route(/\/source-buffer\.js(?:\?.*)?$/, route => {
    requests++;
    return unavailable ? route.fulfill({ status: 503, body: 'Unavailable' }) : route.fallback();
  });
  await page.goto('./');
  await expect(page.getByRole('link', { name: 'Reload the page', exact: true })).toBeVisible();
  await expect(page.locator('#boot-status')).toContainText('Could not load Studio');
  expect(requests).toBe(3);
  unavailable = false;
  await page.getByRole('link', { name: 'Reload the page', exact: true }).click();
  await ready(page);
  expect(requests).toBe(4);
});

for (const status of [503, 200]) {
  test(`failed framework loading stays bounded and preserves integrity checks for HTTP ${status}`, async ({ page }) => {
    let unavailable = true, requests = 0;
    await page.route(frameworkImage, route => {
      requests++;
      return unavailable ? route.fulfill({ status, body: 'Not the expected assembly', contentType: 'application/wasm' }) : route.fallback();
    });
    await page.goto('./');
    await expect(page.getByRole('link', { name: 'Reload the page', exact: true })).toBeVisible();
    await expect(page.locator('.studio[data-ready=true]')).toHaveCount(0);
    expect(requests).toBe(3);
    unavailable = false;
    await page.getByRole('link', { name: 'Reload the page', exact: true }).click();
    await ready(page);
    expect(requests).toBe(4);
  });
}
