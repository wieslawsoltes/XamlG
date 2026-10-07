import { test, expect } from './studio-fixture.mjs';

const mainSource = page => page.evaluate(() => monaco.editor.getModels()
  .find(model => model.getLanguageId() === 'xml' && model.getValue().includes('<StackPanel'))?.getValue());

 test('cancelling a resource move retires its plan and preserves project buffers', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByRole('tab', { name: 'Resources', exact: true }).click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
  const resources = page.getByLabel('Project resource', { exact: true });
  await resources.selectOption('Resources/Palette.axaml');
  const before = await mainSource(page);
  const originalResources = await page.evaluate(() => JSON.parse(localStorage.getItem('xamlg.draft')).resources);

  await page.getByTestId('rename-resource-file').click();
  const dialog = page.getByRole('dialog', { name: 'Move XAML resource', exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByTestId('file-move-preview')).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Apply file move', exact: true })).toHaveCount(0);
  await dialog.getByLabel('Destination resource path', { exact: true }).fill('Themes/Unapplied.axaml');
  await dialog.getByRole('button', { name: 'Preview file move', exact: true }).click();
  await expect(dialog.getByTestId('file-move-preview')).toContainText('View.axaml');
  await expect(dialog.getByRole('button', { name: 'Apply file move', exact: true })).toBeEnabled();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(resources).toHaveValue('Resources/Palette.axaml');
  expect(await mainSource(page)).toBe(before);
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('xamlg.draft')).resources)).toEqual(originalResources);

  await page.getByTestId('rename-resource-file').click();
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel('Destination resource path', { exact: true })).toHaveValue('Resources/Palette.axaml');
  await expect(dialog.getByTestId('file-move-preview')).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Apply file move', exact: true })).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Close file move dialog', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(await mainSource(page)).toBe(before);
  await expect(page.locator('.preview-placeholder')).toBeVisible();
});
