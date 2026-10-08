import { test, expect } from './studio-fixture.mjs';

test('file move state binds actual paths and clears errors on destination changes', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.locator('[data-tab-id="resources"]').click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
  await page.getByLabel('Project resource', { exact: true }).selectOption('Resources/Palette.axaml');
  await page.getByTestId('rename-resource-file').click();
  const dialog = page.getByRole('dialog', { name: 'Move XAML resource', exact: true });
  await expect(dialog.getByTestId('file-move-source')).toHaveText('Resources/Palette.axaml');
  await expect(dialog.getByLabel('Destination resource path', { exact: true })).toHaveValue('Resources/Palette.axaml');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Preview file move', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('different resource path');
  await expect(dialog.getByTestId('file-move-preview')).toHaveCount(0);
  await dialog.getByLabel('Destination resource path', { exact: true }).fill('Themes/MovedPalette.axaml');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Preview file move', exact: true }).click();
  await expect(dialog.getByTestId('file-move-preview')).toContainText('View.axaml');
  await expect(dialog.getByTestId('file-move-preview')).toContainText('Themes/MovedPalette.axaml');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByLabel('Project resource', { exact: true })).toHaveValue('Resources/Palette.axaml');
  await expect(page.locator('.preview-placeholder')).toBeVisible();
});
