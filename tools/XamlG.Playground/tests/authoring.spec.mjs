import { test, expect } from './studio-fixture.mjs';

async function start(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
  await page.getByLabel('Example', { exact: true }).selectOption('1');
  await page.keyboard.press('Escape');
  await expect.poll(() => source(page, 'View.axaml')).toContain('CounterView');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
}
function source(page, path) {
  return page.evaluate(path => monaco.editor.getModels().find(m => m.uri.path.endsWith('/' + path))?.getValue(), path);
}
async function command(page, path, word, action) {
  await expect(page.getByRole('button', { name: 'Compile', exact: true })).toBeEnabled();
  await page.evaluate(({ path, word, action }) => {
    const editor = monaco.editor.getEditors().find(e => e.getModel()?.uri.path.endsWith('/' + path));
    if (!editor) throw new Error('Missing source editor ' + path);
    const model = editor.getModel();
    const offset = word ? model.getValue().indexOf(word) : 0;
    if (offset < 0) throw new Error('Missing trigger ' + word);
    const position = model.getPositionAt(offset);
    editor.setSelection(new monaco.Range(position.lineNumber, position.column, position.lineNumber, position.column));
    return editor.getAction('xamlg.' + action).run();
  }, { path, word, action });
}

test('Monaco rename previews XAML and C# and project undo restores both atomically', async ({ page }) => {
  await start(page);
  const beforeXaml = await source(page, 'View.axaml');
  const beforeCode = await source(page, 'Code.cs');
  await command(page, 'View.axaml', 'counter"', 'rename');
  const dialog = page.getByRole('dialog', { name: 'Rename XAML symbol' });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('New name', { exact: true }).fill('renamedCounter');
  await dialog.getByRole('button', { name: 'Preview rename', exact: true }).click();
  await expect(dialog.getByTestId('rename-files')).toContainText('View.axaml');
  await expect(dialog.getByTestId('rename-files')).toContainText('Code.cs');
  await dialog.getByRole('button', { name: 'Apply rename', exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect.poll(() => source(page, 'View.axaml')).toContain('x:Name="renamedCounter"');
  await expect.poll(() => source(page, 'Code.cs')).toContain('renamedCounter.Text');
  await page.getByTitle('Undo source edit').click();
  await expect.poll(() => source(page, 'View.axaml')).toBe(beforeXaml);
  await expect.poll(() => source(page, 'Code.cs')).toBe(beforeCode);
  await page.getByTitle('Redo source edit').click();
  await expect.poll(() => source(page, 'Code.cs')).toContain('renamedCounter.Text');
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
});

test('source changes during rename preview are rejected without overwriting either buffer', async ({ page }) => {
  await start(page);
  await command(page, 'View.axaml', 'counter"', 'rename');
  const dialog = page.getByRole('dialog', { name: 'Rename XAML symbol' });
  await dialog.getByLabel('New name', { exact: true }).fill('renamedCounter');
  await dialog.getByRole('button', { name: 'Preview rename', exact: true }).click();
  await expect(dialog.getByTestId('rename-files')).toContainText('Code.cs');
  // Programmatic change simulates an external editor/extension; the UI itself is modal.
  await page.evaluate(() => {
    const model = monaco.editor.getModels().find(m => m.uri.path.endsWith('/Code.cs'));
    model.setValue('// newer unsaved edit\n' + model.getValue());
  });
  await dialog.getByRole('button', { name: 'Apply rename', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('stale');
  expect(await source(page, 'View.axaml')).toContain('x:Name="counter"');
  expect(await source(page, 'Code.cs')).toContain('// newer unsaved edit');
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
});

test('Monaco formatting and quick fixes use the shared source services and are undoable', async ({ page }) => {
  await start(page);
  const compact = '<StackPanel xmlns="https://github.com/avaloniaui"><TextBlock Txet="Keep &amp; preserve"/></StackPanel>';
  await page.evaluate(value => monaco.editor.getModels().find(m => m.uri.path.endsWith('/View.axaml')).setValue(value), compact);
  await command(page, 'View.axaml', 'Txet', 'actions');
  const actions = page.getByRole('dialog', { name: 'Source code actions' });
  await expect(actions).toBeVisible();
  await actions.getByRole('button', { name: "Change 'Txet' to 'Text'", exact: true }).click();
  await expect.poll(() => source(page, 'View.axaml')).toContain('Text="Keep &amp; preserve"');
  await command(page, 'View.axaml', null, 'format');
  await expect.poll(() => source(page, 'View.axaml')).toContain('\n  <TextBlock');
  await page.getByTitle('Undo source edit').click();
  await expect.poll(() => source(page, 'View.axaml')).toBe(compact.replace('Txet', 'Text'));
});
