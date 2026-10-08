import { test, expect } from './studio-fixture.mjs';
import { captureEditorState } from './editor-state.mjs';

const draft = page => page.evaluate(() => JSON.parse(localStorage.getItem('xamlg.draft')));

test.afterEach(async ({ page }, testInfo) => {
  if (testInfo.status !== testInfo.expectedStatus) await captureEditorState(page, 'failed: ' + testInfo.title);
});

test('capturing renamed and switched resource editors does not add phantom project undo entries', async ({ page }) => {
  const failures = [];
  page.on('pageerror', error => failures.push(error.message));
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.locator('[data-tab-id="resources"]').click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
  const original = await draft(page);
  const selector = page.getByLabel('Project resource', { exact: true });
  await selector.selectOption('Resources/Palette.axaml');
  await captureEditorState(page, 'ownership before move');
  await page.getByTestId('rename-resource-file').click();
  const dialog = page.getByRole('dialog', { name: 'Move XAML resource', exact: true });
  await dialog.getByLabel('Destination resource path', { exact: true }).fill('Themes/Colors.axaml');
  await dialog.getByRole('button', { name: 'Preview file move', exact: true }).click();
  await captureEditorState(page, 'ownership after preview');
  await expect(dialog.getByTestId('file-move-preview')).toBeVisible();
  await dialog.getByRole('button', { name: 'Apply file move', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.locator('.statusbar')).toContainText('Resource moved');
  await expect(selector).toHaveValue('Themes/Colors.axaml');
  const moved = await draft(page);
  expect(moved.resources['Themes/Colors.axaml']).toBe(original.resources['Resources/Palette.axaml']);
  expect(moved.resources['Styles/Buttons.axaml']).toBe(original.resources['Styles/Buttons.axaml']);

  // Each Compile captures the active Monaco instance immediately after selection.
  // No user edit occurs, so none may create a project history entry.
  for (const path of ['Styles/Buttons.axaml', 'Themes/Colors.axaml', 'Styles/Buttons.axaml', 'Themes/Colors.axaml']) {
    await selector.selectOption(path);
    await page.getByRole('button', { name: 'Compile', exact: true }).click();
    await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
    await captureEditorState(page, 'ownership compiled ' + path);
    expect((await draft(page)).resources).toEqual(moved.resources);
  }
  await page.getByTitle('Undo source edit').click();
  await captureEditorState(page, 'ownership after undo');
  await expect.poll(async () => (await draft(page)).resources).toEqual(original.resources);
  await expect.poll(async () => (await draft(page)).xaml).toBe(original.xaml);
  expect((await draft(page)).code).toBe(original.code);
  await expect(selector).toHaveValue('Resources/Palette.axaml');
  await page.getByTitle('Redo source edit').click();
  await expect.poll(async () => (await draft(page)).resources).toEqual(moved.resources);
  await expect.poll(async () => (await draft(page)).xaml).toBe(moved.xaml);
  await expect(page.locator('.preview-placeholder')).toBeVisible();
  expect(failures).toEqual([]);
});
