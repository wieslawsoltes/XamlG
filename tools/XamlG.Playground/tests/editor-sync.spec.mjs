import { test, expect } from './studio-fixture.mjs';
import { call, openStudio, writeDocument } from './live-preview.mjs';

test('Run captures an edit before its debounce timer fires', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.evaluate(() => {
    const model = monaco.editor.getModels().find(model => model.getLanguageId() === 'xml');
    model.setValue(model.getValue().replace('Made of XAML. Compiled to C#.', 'Immediate snapshot marker'));
    document.querySelector('[data-testid="run-preview"]').click();
  });
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.locator('[data-tab-id="generated"]').click();
  await page.locator('.generated-files button').first().click();
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().some(model =>
    model.getLanguageId() === 'csharp' && model.getValue().includes('Immediate snapshot marker')))).toBe(true);
  await page.getByRole('button', { name: 'Toggle color theme' }).click();
  expect(await page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('Immediate snapshot marker');
});

test('document switching, source undo and redo use the managed revision', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
  await page.getByLabel('Example', { exact: true }).selectOption('1');
  await page.keyboard.press('Escape');
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('CounterView');
  await page.evaluate(() => {
    const model = monaco.editor.getModels().find(model => model.getLanguageId() === 'xml');
    model.setValue(model.getValue().replace('0 clicks', 'Edited counter'));
    document.querySelector('[data-testid="run-preview"]').click();
  });
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  await page.getByTitle('Undo source edit').click();
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('0 clicks');
  await page.getByTitle('Redo source edit').click();
  await expect.poll(() => page.evaluate(() => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').getValue())).toContain('Edited counter');
});

test('exact mixed line endings survive captures, typing, Roslyn navigation and rename Undo', async ({ page }) => {
  test.setTimeout(90000);
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await openStudio(page);
  const invoke = (name, args) => call(page, name, args);
  const source = 'public static class Mixed {\r\n' +
    Array.from({ length: 12 }, (_, i) => `  // LF ${i}\n`).join('') +
    Array.from({ length: 16 }, (_, i) => `  // CRLF ${i}\r\n`).join('') +
    '  public const int TokenName = 3;\r\n  public static int Read() => TokenName;\r\n}';
  await writeDocument(invoke, 'Code.cs', source);
  await page.locator('[data-tab-id="document:Code.cs"]').click();
  await expect.poll(() => page.evaluate(() => monaco.editor.getEditors().some(editor => editor.getModel()?.uri.path.endsWith('/Code.cs')))).toBe(true);
  expect((await invoke('xamlg_document_read', { path: 'Code.cs' })).text).toBe(source);
  await page.evaluate(() => {
    const editor = monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/Code.cs'));
    const model = editor.getModel(), offset = model.getValue().indexOf('= 3') + 2, point = model.getPositionAt(offset);
    editor.executeEdits('fixture', [{ range: new monaco.Range(point.lineNumber, point.column, point.lineNumber, point.column + 1), text: '7' }]);
  });
  const typed = source.replace('= 3', '= 7');
  // No debounce delay: source capture must already contain the exact edited buffer.
  expect((await invoke('xamlg_document_read', { path: 'Code.cs' })).text).toBe(typed);
  expect((await invoke('xamlg_csharp_definitions', { path: 'Code.cs', offset: typed.lastIndexOf('TokenName') })).definitions[0].startLine).toBe(29);
  await page.evaluate(() => {
    const editor = monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/Code.cs'));
    const model = editor.getModel(); editor.setPosition(model.getPositionAt(model.getValue().lastIndexOf('TokenName') + 2)); editor.focus();
  });
  await page.keyboard.press('F12');
  await expect.poll(() => page.evaluate(() => {
    const editor = monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/Code.cs'));
    return editor.getPosition().lineNumber;
  }), { timeout: 15000 }).toBe(30);
  await page.evaluate(() => {
    const editor = monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/Code.cs'));
    const model = editor.getModel(); editor.setPosition(model.getPositionAt(model.getValue().lastIndexOf('TokenName'))); editor.focus();
    void editor.getAction('xamlg.rename').run();
  });
  const rename = page.getByRole('dialog', { name: 'Rename C# symbol', exact: true });
  await expect(rename.getByLabel('New name', { exact: true })).toHaveValue('TokenName');
  await rename.getByLabel('New name', { exact: true }).fill('RenamedToken');
  await rename.getByRole('button', { name: 'Preview rename', exact: true }).click();
  await rename.getByRole('button', { name: 'Apply rename', exact: true }).click();
  await expect(rename).not.toBeVisible();
  expect((await invoke('xamlg_document_read', { path: 'Code.cs' })).text).toBe(typed.replaceAll('TokenName', 'RenamedToken'));
  const project = await invoke('xamlg_project_get');
  await invoke('xamlg_project_undo', { expectedRevision: project.revision });
  expect((await invoke('xamlg_document_read', { path: 'Code.cs' })).text).toBe(typed);
  expect(errors).toEqual([]);
});

test('textarea fallback preserves exact source through reads and local edits', async ({ page }) => {
  await page.route('**/monaco/vs/loader.js', route => route.abort());
  await openStudio(page);
  const invoke = (name, args) => call(page, name, args);
  const source = '<StackPanel xmlns="https://github.com/avaloniaui">\r\n  <Border Height="8" />\n  <TextBlock Text="Before" />\r\n</StackPanel>';
  await writeDocument(invoke, 'View.axaml', source);
  expect((await invoke('xamlg_document_read', { path: 'View.axaml' })).text).toBe(source);
  const editor = page.getByRole('textbox', { name: 'xml source editor', exact: true });
  await editor.fill((await editor.inputValue()).replace('Before', 'After'));
  expect((await invoke('xamlg_document_read', { path: 'View.axaml' })).text).toBe(source.replace('Before', 'After'));
});
