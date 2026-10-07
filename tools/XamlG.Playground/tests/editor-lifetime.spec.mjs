import { test, expect } from './studio-fixture.mjs';
import { readFile } from 'node:fs/promises';

async function ready(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByRole('tab', { name: 'Resources', exact: true }).click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
}
const resourceModels = page => page.evaluate(() => monaco.editor.getModels()
  .filter(model => model.getLanguageId() === 'xml' && /\/(Resources|Styles|Themes)\//.test(model.uri.path))
  .map(model => ({ uri: model.uri.toString(), text: model.getValue() })));

test('retiring resource editors preserves pending text and leaves sibling editors usable', async ({ page }) => {
  const failures = [];
  page.on('pageerror', error => failures.push(error.message));
  await ready(page);
  const selector = page.getByLabel('Project resource', { exact: true });
  await expect(selector).toHaveValue('Resources/Palette.axaml');
  await expect.poll(() => resourceModels(page)).toHaveLength(1);
  // Schedule a source edit and selection change in the same browser turn, before debounce.
  await page.evaluate(() => {
    const model = monaco.editor.getModels().find(model => model.uri.path.endsWith('/Resources/Palette.axaml'));
    model.setValue(model.getValue().replace('#5261D8', '#129944'));
    const select = document.querySelector('select[aria-label="Project resource"]');
    select.value = 'Styles/Buttons.axaml';
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });
  await expect(page.locator('.code-editor[data-document-path="Styles/Buttons.axaml"]')).toBeVisible();
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('xamlg.draft')).resources['Resources/Palette.axaml'])).toContain('#129944');
  await page.getByRole('button', { name: 'Compile', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  const pending = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export', exact: true }).click();
  const exported = JSON.parse(await readFile(await (await pending).path(), 'utf8'));
  expect(exported.resources['Resources/Palette.axaml']).toContain('#129944');
  expect(exported.resources['Styles/Buttons.axaml']).toContain('Button.primary');
  expect(exported.xaml).toContain('Resources/Palette.axaml');
  expect(Object.keys(exported.generatedFiles)).toHaveLength(3);
  expect(failures).toEqual([]);
});

test('repeated keyed resource replacement releases old Monaco models without disturbing root buffers', async ({ page }) => {
  await ready(page);
  const rootIdentities = await page.evaluate(() => monaco.editor.getModels()
    .filter(model => /\/(View\.axaml|Code\.cs)$/.test(model.uri.path)).map(model => model.uri.toString()).sort());
  const selector = page.getByLabel('Project resource', { exact: true });
  const identities = new Set();
  for (let i = 0; i < 6; i++) {
    const path = i % 2 === 0 ? 'Styles/Buttons.axaml' : 'Resources/Palette.axaml';
    await selector.selectOption(path);
    await expect(page.locator(`.code-editor[data-document-path="${path}"]`)).toBeVisible();
    await expect.poll(() => resourceModels(page)).toHaveLength(1);
    const [current] = await resourceModels(page);
    expect(current.uri.endsWith('/' + path)).toBe(true);
    expect(identities.has(current.uri)).toBe(false);
    identities.add(current.uri);
    await page.getByRole('button', { name: 'Compile', exact: true }).click();
    await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  }
  expect(await page.evaluate(() => monaco.editor.getModels()
    .filter(model => /\/(View\.axaml|Code\.cs)$/.test(model.uri.path)).map(model => model.uri.toString()).sort())).toEqual(rootIdentities);
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
});
