import { readFile } from 'node:fs/promises';
import { test, expect } from './studio-fixture.mjs';
import { call, connectMcp, openStudio, writeDocument } from './live-preview.mjs';

async function compilerPane(page) {
  await page.getByRole('tab', { name: 'Compiler', exact: true }).click();
  const pane = page.getByRole('region', { name: 'Roslyn compiler workbench' });
  await expect(pane.getByRole('checkbox', { name: 'Check arithmetic overflow' })).toBeVisible();
  return pane;
}
async function saveOptions(invoke, changes) {
  const state = await invoke('xamlg_compiler_options_get');
  return invoke('xamlg_compiler_options_set', { options: { ...state.options, ...changes }, expectedRevision: state.revision });
}

test('HTTP MCP compiler options affect diagnostics, conditional code, references and exports atomically', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectMcp(page, request);
  try {
    await writeDocument(mcp.call, 'Code.cs', `namespace Settings;
public static class Flags {
#if FEATURE
  public const string Text = "enabled";
#else
  public const string Text = "disabled";
#endif
  public static string Read() => null;
}`);
    await writeDocument(mcp.call, 'View.axaml', '<TextBlock xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="clr-namespace:Settings" x:Name="label" Text="{x:Static local:Flags.Text}"/>');
    const before = await mcp.call('xamlg_compiler_options_get');
    expect(before.options.referenceNames).toBeNull();
    expect(before.availableReferences).toContain('System.Text.RegularExpressions.dll');
    const changed = await saveOptions(mcp.call, { languageVersion: 'CSharp12', preprocessorSymbols: ['FEATURE', 'FEATURE'], checkOverflow: true, diagnosticOptions: { CS8603: 'Error' } });
    expect(changed.options.preprocessorSymbols).toEqual(['FEATURE']);
    expect(changed.revision).toBe(before.revision + 1);
    const failed = await mcp.call('xamlg_compiler_compile');
    expect(failed.success).toBe(false);
    expect(failed.diagnostics).toEqual(expect.arrayContaining([expect.objectContaining({ code: 'CS8603', severity: 'Error', isWarningAsError: true })]));
    const saved = await saveOptions(mcp.call, { diagnosticOptions: { CS8603: 'Suppress' }, referenceNames: before.availableReferences.filter(name => name !== 'System.Text.RegularExpressions.dll') });
    const snapshot = await mcp.call('xamlg_compiler_snapshot');
    expect(snapshot.success).toBe(true);
    expect(snapshot.settings).toEqual(saved.options);
    expect(snapshot.trees.every(tree => tree.languageVersion === 'CSharp12')).toBe(true);
    expect(snapshot.trees.some(tree => tree.generated)).toBe(true);
    expect(snapshot.references.map(reference => reference.name).sort()).toEqual(saved.options.referenceNames);
    const tree = await mcp.call('xamlg_runtime_run', { expectedRevision: saved.revision });
    const label = tree.nodes.find(node => node.name === 'label');
    const properties = await mcp.call('xamlg_runtime_properties', { objectId: label.id });
    expect(properties.properties.find(property => property.name === 'Text').value.value).toBe('enabled');
    const exported = await mcp.call('xamlg_project_export');
    expect(exported.version).toBe(4);
    expect(exported.compilerOptions).toEqual(saved.options);
    expect(JSON.parse(exported.documents['CompilerSettings.json'])).toEqual(saved.options);
    await mcp.call('xamlg_project_undo', { expectedRevision: exported.revision });
    expect((await mcp.call('xamlg_compiler_options_get')).options).toEqual(changed.options);
  } finally { await mcp.close(); }
});

test('compiler option writes reject malformed JSON, unknown references and stale revisions without changing the workspace', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectMcp(page, request);
  try {
    const initial = await mcp.call('xamlg_compiler_options_get');
    for (const text of ['{"allowUnsafe":true,"allowUnsafe":false}', '{"unknown":true}', '{"LanguageVersion":"Preview"}', '{"languageVersion":"999"}', '{"referenceNames":["missing.dll"]}', '{"diagnosticOptions":{"CS8603":"Error","CS8603":"Suppress"}}', 'null']) {
      await expect(mcp.call('xamlg_compiler_options_write', { text, expectedRevision: initial.revision })).rejects.toThrow();
      const current = await mcp.call('xamlg_compiler_options_get');
      expect(current.revision).toBe(initial.revision); expect(current.text).toBe(initial.text);
    }
    await writeDocument(mcp.call, 'Code.cs', 'public class Another {}');
    await expect(mcp.call('xamlg_compiler_options_set', { options: { ...initial.options, checkOverflow: true }, expectedRevision: initial.revision })).rejects.toThrow();
    expect((await mcp.call('xamlg_compiler_options_get')).options).toEqual(initial.options);
    const noReferences = await saveOptions(mcp.call, { referenceNames: [] });
    expect(noReferences.options.referenceNames).toEqual([]);
    const empty = await mcp.call('xamlg_compiler_snapshot');
    expect(empty.references).toEqual([]); expect(empty.success).toBe(false);
    await mcp.call('xamlg_project_undo', { expectedRevision: noReferences.revision });
    expect((await mcp.call('xamlg_compiler_compile')).success).toBe(true);
  } finally { await mcp.close(); }
});

test('non-browser compiler targets emit executable artifacts and reject preview execution', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectMcp(page, request);
  try {
    await writeDocument(mcp.call, 'Code.cs', 'public static class Entry { public static int Main() => 42; }');
    const state = await saveOptions(mcp.call, { outputKind: 'ConsoleApplication', mainTypeName: 'Entry', platform: 'X64', moduleName: 'Settings.exe' });
    const snapshot = await mcp.call('xamlg_compiler_snapshot');
    expect(snapshot.success).toBe(true);
    expect(snapshot.settings.outputKind).toBe('ConsoleApplication');
    await expect(mcp.call('xamlg_runtime_run', { expectedRevision: state.revision })).rejects.toThrow('DynamicallyLinkedLibrary');
    const artifact = await mcp.call('xamlg_build_create', { target: 'assembly', expectedRevision: state.revision });
    expect(artifact.name).toBe('XamlG.Preview.exe');
    const chunk = await mcp.call('xamlg_build_read', { id: artifact.id });
    const image = Buffer.from(chunk.base64, 'base64');
    expect(image.subarray(0, 2).toString()).toBe('MZ');
    const pe = image.readUInt32LE(0x3c);
    expect(image.subarray(pe, pe + 4).toString()).toBe('PE\0\0');
    expect(image.readUInt16LE(pe + 4)).toBe(0x8664); // AMD64
    expect(image.readUInt16LE(pe + 22) & 0x2000).toBe(0); // Executable, without IMAGE_FILE_DLL.
    await mcp.call('xamlg_build_release', { id: artifact.id });
    await mcp.call('xamlg_project_undo', { expectedRevision: state.revision });
    expect((await mcp.call('xamlg_compiler_options_get')).options.outputKind).toBe('DynamicallyLinkedLibrary');
  } finally { await mcp.close(); }
});

test('owner compiler UI supports Undo, Redo, draft restoration and version-4 export with sharing disabled', async ({ page }) => {
  await openStudio(page, false);
  let pane = await compilerPane(page);
  const overflow = () => pane.getByRole('checkbox', { name: 'Check arithmetic overflow' });
  await overflow().check();
  await pane.getByRole('combobox', { name: 'Optimization' }).selectOption('Debug');
  await pane.getByLabel('Conditional symbols').fill('FEATURE, TRACE');
  await pane.getByLabel('Conditional symbols').press('Tab');
  await pane.getByRole('button', { name: 'Save compiler options', exact: true }).click();
  await expect(pane.locator('.runtime-result')).toContainText('"saved": true');
  await page.getByRole('button', { name: '↶ Undo', exact: true }).click();
  await expect(overflow()).not.toBeChecked({ timeout: 15000 });
  await expect(pane.getByRole('alert')).toHaveCount(0);
  await page.getByRole('button', { name: 'Redo', exact: false }).click();
  await expect(overflow()).toBeChecked({ timeout: 15000 });
  await expect(pane.getByRole('combobox', { name: 'Optimization' })).toHaveValue('Debug');
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: 'Restore draft', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Draft restored without executing');
  pane = await compilerPane(page);
  await expect(overflow()).toBeChecked();
  await expect(pane.getByLabel('Conditional symbols')).toHaveValue('FEATURE, TRACE');
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export', exact: true }).click();
  const download = await downloadPromise;
  const project = JSON.parse(await readFile(await download.path(), 'utf8'));
  expect(project.version).toBe(4);
  expect(project.compilerOptions).toMatchObject({ checkOverflow: true, optimization: 'Debug', preprocessorSymbols: ['FEATURE', 'TRACE'] });
  // Older drafts have no settings document and must restore the defaults.
  const legacyDraft = await page.evaluate(() => {
    const draft = JSON.parse(localStorage.getItem('xamlg.draft'));
    draft.version = 3; delete draft.compilerOptions;
    return draft;
  });
  // Install the old-version draft before startup, as on an upgrade. A live
  // compiler refresh also captures/saves editors and may overwrite storage.
  await page.addInitScript(draft => localStorage.setItem('xamlg.draft', JSON.stringify(draft)), legacyDraft);
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: 'Restore draft', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Draft restored without executing');
  pane = await compilerPane(page);
  await expect(overflow()).not.toBeChecked({ timeout: 15000 });
  await expect(pane.getByLabel('Conditional symbols')).toHaveValue('');
  await expect(pane.getByRole('alert')).toHaveCount(0);
});

test('compiler UI preserves a conflicting draft and exports live Roslyn results', async ({ page }) => {
  await openStudio(page);
  const pane = await compilerPane(page);
  await pane.getByRole('checkbox', { name: 'Check arithmetic overflow' }).check();
  await saveOptions((name, args) => call(page, name, args), { nullable: 'Disable' });
  await expect(pane.getByRole('status')).toContainText('Compiler settings changed while this draft was open');
  await expect(pane.getByRole('button', { name: 'Save compiler options', exact: true })).toBeDisabled();
  await expect(pane.getByRole('checkbox', { name: 'Check arithmetic overflow' })).toBeChecked();
  await pane.getByRole('button', { name: 'Discard draft and reload options' }).click();
  await expect(pane.getByRole('checkbox', { name: 'Check arithmetic overflow' })).not.toBeChecked();
  await expect(pane.getByRole('combobox', { name: 'Nullable context' })).toHaveValue('Disable');
  await pane.locator('summary', { hasText: 'Compiler and Roslyn operations' }).click();
  await pane.getByRole('combobox', { name: 'Tool', exact: false }).selectOption('xamlg_compiler_snapshot');
  await pane.getByRole('button', { name: 'Execute compiler operation' }).click();
  await expect(pane.locator('.runtime-result')).toContainText('"references":');
  const downloadPromise = page.waitForEvent('download');
  await pane.getByRole('button', { name: 'Export result', exact: true }).click();
  const download = await downloadPromise;
  const snapshot = JSON.parse(await readFile(await download.path(), 'utf8'));
  expect(snapshot.success).toBe(true); expect(snapshot.settings.nullable).toBe('Disable');
  expect(snapshot.references.length).toBeGreaterThan(0);
  expect(snapshot.trees.some(tree => tree.path === 'Code.cs')).toBe(true);
});
