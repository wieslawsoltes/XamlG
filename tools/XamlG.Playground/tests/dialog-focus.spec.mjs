import { test, expect } from './studio-fixture.mjs';

async function ready(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.locator('[data-tab-id="resources"]').click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
}

test('file move restores focus after rejected and successful previews and can reopen after Escape', async ({ page }) => {
  await ready(page);
  await page.getByTestId('rename-resource-file').click();
  const dialog = page.getByRole('dialog', { name: 'Move XAML resource' });
  const input = dialog.getByLabel('Destination resource path');
  await expect(input).toBeFocused();
  await input.fill('Styles/Buttons.axaml');
  await dialog.getByRole('button', { name: 'Preview file move', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('collides');
  await expect(input).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await page.getByTestId('rename-resource-file').click();
  await expect(input).toBeFocused();
  await input.fill('Themes/Palette.axaml');
  await dialog.getByRole('button', { name: 'Preview file move', exact: true }).click();
  await expect(dialog.getByTestId('file-move-preview')).toBeVisible();
  await expect(input).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(page.getByLabel('Project resource', { exact: true })).toHaveValue('Resources/Palette.axaml');
});
