import { test, expect } from '@playwright/test';

const call = (page, name, args = {}) => page.evaluate(({ name, args }) => window.xamlgAutomation.call(name, args), { name, args });
const source = (page, path = 'Code.cs') => page.evaluate(path => monaco.editor.getEditors().find(e => e.getModel()?.uri.path.endsWith('/' + path))?.getValue(), path);
async function ready(page, share = true) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.getByRole('button', { name: 'Compile', exact: true })).toBeEnabled();
  if (share) {
    await page.getByTestId('agent-access').click();
    await page.getByLabel('Enable access to this live project').check();
    await page.getByLabel('Permission profile', { exact: true }).selectOption('FullAccess');
    await page.getByRole('button', { name: 'Close', exact: true }).click();
  }
}
async function write(page, path, text) {
  const { revision } = await call(page, 'xamlg_project_get');
  return call(page, 'xamlg_document_write', { path, text, expectedRevision: revision });
}
async function command(page, word, action, delta = 0) {
  await expect(page.getByRole('button', { name: 'Compile', exact: true })).toBeEnabled();
  await page.evaluate(async ({ word, action, delta }) => {
    const editor = monaco.editor.getEditors().find(e => e.getModel()?.uri.path.endsWith('/Code.cs'));
    const model = editor.getModel(); const offset = model.getValue().indexOf(word);
    if (offset < 0) throw new Error('Missing trigger: ' + word);
    editor.setPosition(model.getPositionAt(offset + delta)); editor.focus();
    const command = editor.getAction(action);
    if (command) await command.run();
    else editor.trigger('keyboard', action, null);
  }, { word, action, delta });
}

test('C# MCP authoring resolves other files, previews renames and applies undoable semantic actions', async ({ page }) => {
  await ready(page);
  const code = 'namespace Model; public class Page { public int Read(Item item) => item.Number; public void Local() { var local = 1; } }';
  const model = 'namespace Model; public class Item { public int Number = 42; }';
  await write(page, 'Code.cs', code); await write(page, 'Models/Item.cs', model);
  expect((await call(page, 'xamlg_compiler_compile')).success).toBe(true);
  const offset = code.indexOf('Number');
  expect((await call(page, 'xamlg_csharp_hover', { path: 'Code.cs', offset })).hover.kind).toBe('Field');
  const definitions = (await call(page, 'xamlg_csharp_definitions', { path: 'Code.cs', offset })).definitions;
  expect(definitions[0].path).toBe('Models/Item.cs'); expect(definitions[0].isGenerated).toBe(false);
  const references = (await call(page, 'xamlg_csharp_references', { path: 'Code.cs', offset })).references;
  expect(references.truncated).toBe(false); expect(references.locations).toHaveLength(2);
  const completion = (await call(page, 'xamlg_csharp_complete', { path: 'Code.cs', offset: offset + 3 })).completions;
  expect(completion.items.some(i => i.label === 'Number')).toBe(true);
  expect(code.slice(completion.span.start, completion.span.start + completion.span.length)).toBe('Number');
  let { revision } = await call(page, 'xamlg_project_get');
  const plan = await call(page, 'xamlg_csharp_rename_preview', { path: 'Code.cs', offset, name: 'Count', expectedRevision: revision });
  expect(plan.plan.documents).toHaveLength(2);
  expect((await call(page, 'xamlg_document_read', { path: 'Code.cs' })).text).toBe(code);
  const renamed = await call(page, 'xamlg_csharp_rename', { path: 'Code.cs', offset, name: 'Count', expectedRevision: revision });
  expect((await call(page, 'xamlg_document_read', { path: 'Models/Item.cs' })).text).toContain('Count = 42');
  await expect(call(page, 'xamlg_csharp_format', { path: 'Code.cs', expectedRevision: revision })).rejects.toThrow();
  await call(page, 'xamlg_project_undo', { expectedRevision: renamed.revision });
  const actions = await call(page, 'xamlg_csharp_actions', { path: 'Code.cs', offset: code.indexOf('local =') });
  expect(actions.actions[0].title).toBe('Use explicit type');
  ({ revision } = await call(page, 'xamlg_project_get'));
  const applied = await call(page, 'xamlg_csharp_action_apply', { path: 'Code.cs', offset: code.indexOf('local ='), title: actions.actions[0].title, expectedRevision: revision });
  expect((await call(page, 'xamlg_document_read', { path: 'Code.cs' })).text).toContain('int local = 1;');
  await call(page, 'xamlg_project_undo', { expectedRevision: applied.revision });
  expect((await call(page, 'xamlg_document_read', { path: 'Code.cs' })).text).toBe(code);
});

test('Monaco C# completion, rename and formatting work without enabling agent access', async ({ page }) => {
  await ready(page, false);
  await page.getByRole('tab', { name: /^#.*Code.cs$/ }).click();
  const code = 'public class Sample { public int Answer = 42; public int Read(){var result=Answer;return result;} }';
  await page.evaluate(code => monaco.editor.getEditors().find(e => e.getModel()?.uri.path.endsWith('/Code.cs')).setValue(code), code);
  await command(page, 'Answer;', 'editor.action.triggerSuggest', 3);
  await expect(page.locator('.suggest-widget.visible')).toContainText('Answer');
  await page.keyboard.press('Escape');
  await command(page, 'Answer =', 'xamlg.rename');
  const dialog = page.getByRole('dialog', { name: 'Rename C# symbol', exact: true });
  await expect(dialog).toBeVisible();
  await dialog.getByLabel('New name', { exact: true }).fill('Total');
  await dialog.getByRole('button', { name: 'Preview rename', exact: true }).click();
  await expect(dialog.getByTestId('rename-files')).toContainText('2 changes');
  await dialog.getByRole('button', { name: 'Apply rename', exact: true }).click();
  await expect.poll(() => source(page)).toContain('result=Total;');
  await command(page, 'Sample', 'xamlg.format');
  await expect.poll(() => source(page)).toContain('\n  public int Total');
  await command(page, 'result =', 'xamlg.actions');
  const actions = page.getByRole('dialog', { name: 'Source code actions' });
  await actions.getByRole('button', { name: 'Use explicit type', exact: true }).click();
  await expect.poll(() => source(page)).toContain('int result = Total;');
});

test('Monaco definitions open an unopened C# file through Dockyard', async ({ page }) => {
  await ready(page);
  await write(page, 'Models/Alpha.cs', 'namespace Model; public class Alpha {}');
  await write(page, 'Models/Zebra.cs', 'namespace Model; public class Item { public int Number = 42; }');
  await write(page, 'Code.cs', 'namespace Model; public class Page { public int Read(Item item) => item.Number; }');
  await page.getByRole('tab', { name: /^#.*Code.cs$/ }).click();
  await command(page, 'Number;', 'editor.action.revealDefinition', 2);
  const files = page.getByRole('region', { name: 'C# project documents' });
  await expect(files).toBeVisible();
  await expect(files.getByLabel('C# source document')).toHaveValue('Models/Zebra.cs');
  await expect(files.locator('.monaco-editor')).toBeVisible();
  await expect.poll(() => page.evaluate(() => {
    const editor = monaco.editor.getEditors().find(e => e.getModel()?.uri.path.endsWith('/Models/Zebra.cs'));
    return editor.getModel().getWordAtPosition(editor.getPosition())?.word;
  })).toBe('Number');
});

test('C# generated field rename edits its XAML declaration and generated files can be selected', async ({ page }) => {
  await ready(page);
  await page.getByLabel('Example', { exact: true }).selectOption('1');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  const code = (await call(page, 'xamlg_document_read', { path: 'Code.cs' })).text;
  const { revision } = await call(page, 'xamlg_project_get');
  const definitions = (await call(page, 'xamlg_csharp_definitions', { path: 'Code.cs', offset: code.indexOf('counter.Text') })).definitions;
  expect(definitions.some(d => d.isGenerated)).toBe(true);
  await call(page, 'xamlg_csharp_rename', { path: 'Code.cs', offset: code.indexOf('counter.Text'), name: 'caption', expectedRevision: revision });
  expect((await call(page, 'xamlg_document_read', { path: 'View.axaml' })).text).toContain('x:Name="caption"');
  expect((await call(page, 'xamlg_document_read', { path: 'Code.cs' })).text).toContain('caption.Text');
  expect((await call(page, 'xamlg_compiler_compile')).success).toBe(true);
  const files = (await call(page, 'xamlg_generated_list')).files;
  await page.getByRole('tab', { name: 'C# output', exact: true }).click();
  const picker = page.getByLabel('Generated C# file', { exact: true });
  await expect(picker.locator('option')).toHaveCount(files.length);
  await picker.selectOption(files.at(-1).path);
  await expect.poll(() => page.evaluate(path => monaco.editor.getEditors().some(e => e.getOption(monaco.editor.EditorOption.readOnly) && e.getModel()?.uri.path.endsWith('/' + path)), files.at(-1).path)).toBe(true);
});
