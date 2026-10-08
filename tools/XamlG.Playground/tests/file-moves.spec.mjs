import { test, expect } from './studio-fixture.mjs';
import { captureEditorState } from './editor-state.mjs';

test.afterEach(async ({ page }, testInfo) => {
  if (testInfo.status !== testInfo.expectedStatus) await captureEditorState(page, 'failed: ' + testInfo.title);
});

async function ready(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.locator('[data-tab-id="resources"]').click();
  await page.getByTestId('resource-example').click();
  await expect(page.locator('.statusbar')).toContainText('3 documents');
  await expect(page.getByLabel('Project resource', { exact: true })).toHaveValue('Resources/Palette.axaml');
}
const draft = page => page.evaluate(() => JSON.parse(localStorage.getItem('xamlg.draft')));
const mainSource = page => page.evaluate(() => monaco.editor.getModels().find(m => m.getLanguageId() === 'xml' && m.getValue().includes('<StackPanel')).getValue());

async function preview(page, destination = 'Themes/Colors.axaml') {
  await page.getByTestId('rename-resource-file').click();
  const dialog = page.getByRole('dialog', { name: 'Move XAML resource' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('Destination resource path').fill(destination);
  await dialog.getByRole('button', { name: 'Preview file move', exact: true }).click();
  return dialog;
}

test('resource identity and its source links move in one undoable non-executing transaction', async ({ page }) => {
  await ready(page);
  const original = await draft(page);
  await captureEditorState(page, 'before move');
  const dialog = await preview(page);
  await expect(dialog.getByTestId('file-move-preview')).toContainText('View.axaml');
  expect(await mainSource(page)).toContain('Resources/Palette.axaml');
  await dialog.getByRole('button', { name: 'Apply file move', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(page.locator('.statusbar')).toContainText('Resource moved');
  await expect(page.getByLabel('Project resource', { exact: true })).toHaveValue('Themes/Colors.axaml');
  await expect.poll(() => mainSource(page)).toContain('Themes/Colors.axaml');
  const moved = await draft(page);
  expect(moved.resources['Resources/Palette.axaml']).toBeUndefined();
  expect(moved.resources['Themes/Colors.axaml']).toBe(original.resources['Resources/Palette.axaml']);
  await expect(page.locator('.preview-placeholder')).toBeVisible();
  await captureEditorState(page, 'after move');
  await page.getByTitle('Undo source edit').click();
  await captureEditorState(page, 'after undo');
  await expect.poll(() => mainSource(page)).toContain('Resources/Palette.axaml');
  expect((await draft(page)).resources).toEqual(original.resources);
  await page.getByTitle('Redo source edit').click();
  await expect.poll(() => mainSource(page)).toContain('Themes/Colors.axaml');
  expect((await draft(page)).resources).toEqual(moved.resources);
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
});

test('source edits during file-move preview are retained and invalidate the entire move', async ({ page }) => {
  await ready(page);
  const dialog = await preview(page);
  await expect(dialog.getByTestId('file-move-preview')).toBeVisible();
  await page.evaluate(() => {
    const source = monaco.editor.getModels().find(m => m.getLanguageId() === 'xml' && m.getValue().includes('<StackPanel'));
    source.setValue(source.getValue() + '\n<!-- newer user edit -->');
  });
  await dialog.getByRole('button', { name: 'Apply file move', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('source changed');
  expect(await mainSource(page)).toContain('<!-- newer user edit -->');
  expect((await draft(page)).resources['Resources/Palette.axaml']).toBeDefined();
  expect((await draft(page)).resources['Themes/Colors.axaml']).toBeUndefined();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
});

test('file-move preview rejects occupied paths before committing any source change', async ({ page }) => {
  await ready(page);
  const original = await draft(page);
  const dialog = await preview(page, 'Styles/Buttons.axaml');
  await expect(dialog.getByRole('alert')).toContainText('collides');
  await expect(dialog.getByRole('button', { name: 'Apply file move', exact: true })).toHaveCount(0);
  expect((await draft(page)).resources).toEqual(original.resources);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
});
