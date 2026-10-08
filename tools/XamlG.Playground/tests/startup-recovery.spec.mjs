import { test, expect } from './studio-fixture.mjs';

test.use({ liveUpdates: true });

test('startup recovers from transient manifest and metadata failures without a page reload', async ({ page }) => {
  let manifests = 0, images = 0, navigations = 0;
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
  await page.route('**/references/index.txt', route => ++manifests === 1
    ? route.fulfill({ status: 503, body: 'Temporarily unavailable', headers: { 'Retry-After': '0' } }) : route.fallback());
  await page.route('**/references/Avalonia.Skia.dll', route => ++images <= 2
    ? route.fulfill({ status: 503, body: 'Temporarily unavailable' }) : route.fallback());
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('preview updated automatically');
  await expect(page.getByTestId('retry-startup')).toHaveCount(0);
  expect({ manifests, images, navigations }).toEqual({ manifests: 2, images: 3, navigations: 1 });
  expect(errors).toEqual([]);
});

test('exhausted metadata retries offer recovery that preserves immediate source edits', async ({ page }) => {
  let unavailable = true, images = 0, navigations = 0;
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('framenavigated', frame => { if (frame === page.mainFrame()) navigations++; });
  await page.route('**/references/Avalonia.Skia.dll', route => {
    images++;
    return unavailable ? route.fulfill({ status: 503, body: 'Temporarily unavailable' }) : route.fallback();
  });
  await page.goto('./');
  const retry = page.getByTestId('retry-startup');
  await expect(retry).toBeEnabled();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'false');
  await expect(page.locator('.runtime-error')).toContainText("Avalonia.Skia.dll' (HTTP 503)");
  expect(images).toBe(3);
  unavailable = false;
  const xaml = '<TextBlock xmlns="https://github.com/avaloniaui" Name="retryRecovered" Text="Preserved source" />';
  // Retry must capture the current editor buffer even before its edit callback.
  await page.evaluate(text => {
    monaco.editor.getModels().find(model => model.uri.path.endsWith('/View.axaml')).setValue(text);
    document.querySelector('[data-testid="retry-startup"]').click();
  }, xaml);
  await expect(retry).toBeDisabled();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('preview updated automatically');
  expect(await page.evaluate(() => monaco.editor.getModels().find(model => model.uri.path.endsWith('/View.axaml')).getValue())).toBe(xaml);
  await page.locator('[data-tab-id="visual-tree"]').click();
  await expect(page.getByRole('tabpanel', { name: 'Visual tree', exact: true })).toContainText('retryRecovered');
  await expect(retry).toHaveCount(0);
  expect({ images, navigations }).toEqual({ images: 4, navigations: 1 });
  expect(errors).toEqual([]);
});

for (const [status, headers] of [[404, {}], [429, { 'Retry-After': '3600' }]]) {
  test(`startup stops automatic retries for HTTP ${status} and offers manual recovery`, async ({ page }) => {
    let requests = 0;
    await page.route('**/references/index.txt', route => {
      requests++;
      return route.fulfill({ status, body: 'Unavailable', headers });
    });
    await page.goto('./');
    await expect(page.getByTestId('retry-startup')).toBeEnabled();
    await expect(page.locator('.runtime-error')).toContainText(`HTTP ${status}`);
    expect(requests).toBe(1);
  });
}
