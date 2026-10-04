import { test, expect } from '@playwright/test';

async function viewSource(page) {
  return page.evaluate(() => monaco.editor.getModels()
    .find(model => model.getLanguageId() === 'xml' && model.getValue().includes('<StackPanel'))?.getValue());
}

test('resource move opens without a plan and cancellation never reuses an old preview', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.getByRole('tab', { name: 'Resources', exact: true }).click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
  const resources = page.getByLabel('Project resource', { exact: true });
  await resources.selectOption('Resources/Palette.axaml');
  const originalSource = await viewSource(page);

  await page.getByRole('button', { name: 'Rename file', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Rename resource file', exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('.authoring-preview')).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Apply rename', exact: true })).toBeDisabled();
  await dialog.getByLabel('New resource path', { exact: true }).fill('Themes/Unapplied.axaml');
  await dialog.getByRole('button', { name: 'Preview changes', exact: true }).click();
  await expect(dialog.locator('.authoring-preview')).toContainText('View.axaml');
  await expect(dialog.getByRole('button', { name: 'Apply rename', exact: true })).toBeEnabled();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(resources).toHaveValue('Resources/Palette.axaml');
  expect(await viewSource(page)).toBe(originalSource);

  await page.getByRole('button', { name: 'Rename file', exact: true }).click();
  await expect(dialog).toBeVisible();
  await expect(dialog.getByLabel('New resource path', { exact: true })).toHaveValue('Resources/Palette.axaml');
  await expect(dialog.locator('.authoring-preview')).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Apply rename', exact: true })).toBeDisabled();
  await dialog.getByRole('button', { name: 'Close dialog', exact: true }).click();
  await expect(dialog).toBeHidden();
  expect(await viewSource(page)).toBe(originalSource);
  await expect(page.locator('.preview-placeholder')).toBeVisible();
});
