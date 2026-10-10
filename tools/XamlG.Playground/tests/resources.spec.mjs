import { test, expect } from './studio-fixture.mjs';
import { readFile } from 'node:fs/promises';
import { savedProject } from './editor-state.mjs';

async function project(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.locator('[data-tab-id="resources"]').click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
  await expect(page.getByLabel('Project resource', { exact: true })).toHaveValue('Resources/Palette.axaml');
}

test('project resources compile, capture immediate edits, export and survive draft restoration', async ({ page }) => {
  await project(page);
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.evaluate(() => {
    const model = monaco.editor.getModels().find(model => model.getLanguageId() === 'xml' && model.getValue().includes('<SolidColorBrush'));
    if (!model) throw new Error('Resource source editor did not load.');
    model.setValue(model.getValue().replace('#5261D8', '#113399'));
    document.querySelector('[data-testid="run-preview"]').click();
  });
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await expect.poll(async () => (await savedProject(page)).resources['Resources/Palette.axaml']).toContain('#113399');
  const downloadPromise = page.waitForEvent('download');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
  await page.getByRole('button', { name: 'Export preview project', exact: true }).click();
  const download = await downloadPromise;
  const data = JSON.parse(await readFile(await download.path(), 'utf8'));
  expect(data.version).toBe(4);
  expect(data.compilerOptions).toEqual((await savedProject(page)).compilerOptions);
  expect(Object.keys(data.resources)).toHaveLength(2);
  expect(Object.keys(data.generatedFiles)).toHaveLength(3);
  expect(data.generated).toContain('XamlResourceServices.Enter');
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
  await page.getByRole('button', { name: 'Restore preview draft', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Draft restored without executing');
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.locator('[data-tab-id="visual-tree"]').click();
  await expect(page.locator('.inspector-body:visible')).toContainText('240 × 48');
});

test('linked projects run isolated and deleted dependencies report source errors', async ({ page }) => {
  await project(page);
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('run-isolated').click();
  await expect(page.locator('.statusbar')).toContainText('Isolated preview running');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
  await page.getByTestId('reset-isolation').click();
  await page.locator('[data-tab-id="resources"]').click();
  await page.locator('.project-resources').getByRole('button', { name: 'Remove', exact: true }).click();
  await expect(page.getByLabel('Project resource', { exact: true })).toHaveValue('Styles/Buttons.axaml');
  await page.getByRole('button', { name: 'Compile', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Compilation has errors');
  await page.locator('[data-tab-id="problems"]').click();
  await expect(page.locator('.diagnostics')).toContainText('XG3301');
  await expect(page.locator('.diagnostics')).toContainText('Resources/Palette.axaml');
});
