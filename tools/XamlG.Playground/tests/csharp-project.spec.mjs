import { test, expect } from './studio-fixture.mjs';

const call = (page, name, args = {}) => page.evaluate(({ name, args }) => window.xamlgAutomation.call(name, args), { name, args });
async function share(page) {
  await page.getByTestId('agent-access').click();
  await page.getByLabel('Enable access to this live project').check();
  await page.getByLabel('Permission profile', { exact: true }).selectOption('FullAccess');
  await page.getByRole('button', { name: 'Close', exact: true }).click();
}
async function open(page) {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await share(page);
}
async function write(page, path, text) {
  const project = await call(page, 'xamlg_project_get');
  return call(page, 'xamlg_document_write', { path, text, expectedRevision: project.revision });
}

test('multiple C# files compile with XAML, support cross-file rename, runtime methods and source restoration', async ({ page }) => {
  await open(page);
  await write(page, 'Code.cs', 'namespace Multi; public partial class MultiView : Avalonia.Controls.StackPanel { public MultiView() { InitializeComponent(); } }');
  const handler = 'namespace Multi; public partial class MultiView { private void Handle(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => greeting.Text = Labels.Clicked; }';
  await write(page, 'Views/Handlers.cs', handler);
  const model = 'namespace Multi; public static class Labels { public const string Initial = "From another C# file"; public const string Clicked = "Partial handler ran"; }';
  await write(page, 'Models/Labels.cs', model);
  const xaml = '<StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="clr-namespace:Multi" x:Class="Multi.MultiView"><TextBlock x:Name="greeting" Text="{x:Static local:Labels.Initial}"/><Button x:Name="action" Click="Handle"/></StackPanel>';
  const source = await write(page, 'View.axaml', xaml);
  expect((await call(page, 'xamlg_compiler_compile')).success).toBe(true);
  const generated = await call(page, 'xamlg_generated_list');
  expect(generated.files.length).toBeGreaterThan(0);
  expect(generated.files.some(f => ['Code.cs', 'Views/Handlers.cs', 'Models/Labels.cs'].includes(f.path))).toBe(false);
  const syntax = await call(page, 'xamlg_csharp_syntax', { path: 'Views/Handlers.cs', maxDepth: 4 });
  expect(syntax.tree.kind).toBe('CompilationUnit');
  const symbol = await call(page, 'xamlg_csharp_symbol', { path: 'Models/Labels.cs', offset: model.indexOf('Initial') });
  expect(symbol.kind).toBe('Field');
  expect(symbol.locations.some(l => l.path === 'Models/Labels.cs')).toBe(true);
  let runtime = await call(page, 'xamlg_runtime_run', { expectedRevision: source.revision });
  const greeting = runtime.nodes.find(n => n.name === 'greeting');
  expect((await call(page, 'xamlg_runtime_properties', { objectId: greeting.id })).properties.find(p => p.name === 'Text').value.value).toBe('From another C# file');
  const button = runtime.nodes.find(n => n.name === 'action');
  const events = await call(page, 'xamlg_runtime_events', { objectId: button.id });
  runtime = await call(page, 'xamlg_runtime_tree');
  await call(page, 'xamlg_runtime_event_raise', { objectId: button.id, event: events.events.find(e => e.endsWith('.Click')), expectedRevision: runtime.revision });
  expect((await call(page, 'xamlg_runtime_properties', { objectId: greeting.id })).properties.find(p => p.name === 'Text').value.value).toBe('Partial handler ran');

  const renamed = await call(page, 'xamlg_xaml_rename', { path: 'View.axaml', offset: xaml.indexOf('greeting'), name: 'renamedGreeting', expectedRevision: source.revision });
  expect((await call(page, 'xamlg_document_read', { path: 'Views/Handlers.cs' })).text).toContain('renamedGreeting.Text');
  await call(page, 'xamlg_project_undo', { expectedRevision: renamed.revision });
  expect((await call(page, 'xamlg_document_read', { path: 'Views/Handlers.cs' })).text).toBe(handler);

  const revision = (await call(page, 'xamlg_project_get')).revision;
  await expect(call(page, 'xamlg_document_write', { path: 'Code.cs', text: 'not committed', expectedRevision: revision - 1 })).rejects.toThrow();
  expect((await call(page, 'xamlg_compiler_compile')).success).toBe(true);
  const project = await call(page, 'xamlg_project_export');
  expect(project.version).toBe(3);
  expect(project.codeFiles['Models/Labels.cs']).toBe(model);
  await page.reload(); await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByRole('button', { name: 'Restore draft', exact: true }).click();
  await expect(page.locator('.statusbar')).toContainText('Draft restored');
  await share(page);
  expect((await call(page, 'xamlg_document_read', { path: 'Views/Handlers.cs' })).text).toBe(handler);
  expect((await call(page, 'xamlg_compiler_compile')).success).toBe(true);
});

test('C# editor capture, move, remove and project undo preserve current buffers', async ({ page }) => {
  await open(page);
  await page.getByRole('tab', { name: 'C# files', exact: true }).click();
  const editor = page.getByRole('region', { name: 'C# project documents' });
  await editor.getByLabel('C# file path').fill('Models/Value.cs');
  await editor.getByRole('button', { name: 'Add C# file', exact: true }).click();
  await expect(editor.getByLabel('C# source document')).toHaveValue('Models/Value.cs');
  await expect(editor.locator('.monaco-editor')).toBeVisible();
  const code = 'namespace Extra; public class Value { public int Number => 42; }';
  // No debounce wait: the automation boundary must capture Monaco before compiling.
  const compiled = await page.evaluate(async code => {
    const model = self.monaco.editor.getModels().find(model => model.uri.path.endsWith('/Models/Value.cs'));
    model.setValue(code);
    return window.xamlgAutomation.call('xamlg_compiler_compile');
  }, code);
  expect(compiled.success).toBe(true);
  expect((await call(page, 'xamlg_document_read', { path: 'Models/Value.cs' })).text).toBe(code);
  await editor.getByLabel('C# file path').fill('Models/Renamed.cs');
  await editor.getByRole('button', { name: 'Move to path', exact: true }).click();
  await expect(editor.getByLabel('C# source document')).toHaveValue('Models/Renamed.cs');
  expect((await call(page, 'xamlg_document_read', { path: 'Models/Renamed.cs' })).text).toBe(code);
  await editor.getByRole('button', { name: 'Remove C# file', exact: true }).click();
  expect((await call(page, 'xamlg_project_get')).documents.some(d => d.path === 'Models/Renamed.cs')).toBe(false);
  await page.getByRole('button', { name: '↶ Undo', exact: true }).click();
  await expect(editor.getByLabel('C# source document')).toHaveValue('Models/Renamed.cs');
  await expect(page.getByRole('button', { name: 'Compile', exact: true })).toBeEnabled();
  expect((await call(page, 'xamlg_document_read', { path: 'Models/Renamed.cs' })).text).toBe(code);
});
