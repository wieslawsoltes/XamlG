import { test, expect } from '@playwright/test';

const call = (page, name, args = {}) => page.evaluate(({ name, args }) => window.xamlgAutomation.call(name, args), { name, args });

async function enable(page, profile = 'FullAccess') {
  await page.goto('./');
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByTestId('agent-access').click();
  await page.getByLabel('Enable access to this live project').check();
  await page.getByLabel('Permission profile').selectOption(profile);
  await page.getByRole('button', { name: 'Close', exact: true }).click();
}

test('shared automation edits, compiles, inspects Roslyn and manipulates real Avalonia objects', async ({ page }) => {
  await enable(page);
  const project = await call(page, 'xamlg_project_get');
  const original = await call(page, 'xamlg_document_read', { path: 'View.axaml' });
  const xaml = '<StackPanel xmlns="https://github.com/avaloniaui"><TextBlock Name="greeting" Text="Before"/><Button Content="Click"/></StackPanel>';
  const changed = await call(page, 'xamlg_document_write', { path: 'View.axaml', text: xaml, expectedRevision: project.revision });
  await expect(call(page, 'xamlg_document_write', { path: 'View.axaml', text: '<bad/>', expectedRevision: project.revision })).rejects.toThrow();
  const compiled = await call(page, 'xamlg_compiler_compile');
  expect(compiled.success).toBe(true);
  const files = await call(page, 'xamlg_generated_list');
  expect(files.files.length).toBeGreaterThan(0);
  const generated = await call(page, 'xamlg_generated_read', { path: files.files[0].path });
  expect(generated.text).toContain('TextBlock');
  const syntax = await call(page, 'xamlg_csharp_syntax', { path: files.files[0].path, maxDepth: 3 });
  expect(syntax.tree.kind).toBe('CompilationUnit');
  const runtime = await call(page, 'xamlg_runtime_run', { expectedRevision: changed.revision });
  const greeting = runtime.nodes.find(n => n.name === 'greeting');
  expect(greeting).toBeTruthy();
  expect(greeting.source).toBeTruthy();
  expect(greeting.logicalParent).toBe(runtime.rootId);
  const properties = await call(page, 'xamlg_runtime_properties', { objectId: greeting.id });
  const text = properties.properties.find(p => p.name === 'Text');
  expect(text.value.value).toBe('Before');
  const current = await call(page, 'xamlg_runtime_tree');
  await call(page, 'xamlg_runtime_property_set', { objectId: greeting.id, property: text.key, value: 'After', expectedRevision: current.revision });
  const after = await call(page, 'xamlg_runtime_properties', { objectId: greeting.id });
  expect(after.properties.find(p => p.name === 'Text').value.value).toBe('After');
  expect((await call(page, 'xamlg_document_read', { path: 'View.axaml' })).text).toBe(xaml);
  await expect(page.locator('#avalonia-preview canvas').first()).toBeVisible();
  await call(page, 'xamlg_project_undo', { expectedRevision: changed.revision });
  expect((await call(page, 'xamlg_document_read', { path: 'View.axaml' })).text).toBe(original.text);
});

test('read-only access and revocation block mutation while Dockyard retains live editors', async ({ page }) => {
  await enable(page, 'ReadOnly');
  const project = await call(page, 'xamlg_project_get');
  await expect(call(page, 'xamlg_document_write', { path: 'View.axaml', text: '<bad/>', expectedRevision: project.revision })).rejects.toThrow();
  const layout = await call(page, 'xamlg_layout_get');
  expect(layout.layout).toContain('explorer');
  expect(layout.layout).toContain('preview');
  await page.getByTestId('agent-access').click();
  await page.getByRole('button', { name: 'Revoke & disconnect' }).click();
  await expect(call(page, 'xamlg_project_get')).rejects.toThrow();
});

test('HTTP MCP reaches the paired browser project through the companion', async ({ page, request }) => {
  test.skip(!process.env.XAMLG_TEST_MCP_URL, 'Run with the local companion for transport coverage.');
  await enable(page);
  await page.getByTestId('agent-access').click();
  const base = process.env.XAMLG_TEST_MCP_URL;
  const token = process.env.XAMLG_TEST_MCP_TOKEN;
  await page.getByLabel('Companion WebSocket').fill(base.replace('http:', 'ws:') + '/bridge');
  await page.getByLabel('Local access token').fill(token);
  await page.getByRole('button', { name: 'Connect companion' }).click();
  await expect(page.getByRole('dialog', { name: 'Agent access' }).getByRole('status')).toContainText('Connected');
  let session, sequence = 0;
  async function rpc(method, params) {
    const response = await request.post(base + '/mcp', {
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/json, text/event-stream',
        'MCP-Protocol-Version': '2025-11-25', ...(session ? { 'Mcp-Session-Id': session } : {}) },
      data: { jsonrpc: '2.0', id: ++sequence, method, params }
    });
    expect(response.ok()).toBe(true);
    session ||= response.headers()['mcp-session-id'];
    const body = await response.text();
    const json = body.startsWith('{') ? JSON.parse(body) : JSON.parse(body.split('\n').find(line => line.startsWith('data:')).slice(5));
    expect(json.error).toBeUndefined(); return json.result;
  }
  await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'XamlG browser test', version: '1' } });
  const catalog = await rpc('tools/list', {});
  expect(catalog.tools.some(tool => tool.name === 'xamlg_runtime_properties')).toBe(true);
  const result = await rpc('tools/call', { name: 'xamlg_project_get', arguments: {} });
  expect(result.isError).not.toBe(true);
  expect(result.structuredContent.documents.some(d => d.path === 'View.axaml')).toBe(true);
  await page.getByRole('button', { name: 'Revoke & disconnect' }).click();
});

test('floating, docking and layout restoration retain the source buffers and Avalonia preview', async ({ page }) => {
  await enable(page);
  const initial = await call(page, 'xamlg_project_get');
  const text = '<TextBlock xmlns="https://github.com/avaloniaui" Name="retained" Text="Dockyard retained this" />';
  const changed = await call(page, 'xamlg_document_write', { path: 'View.axaml', text, expectedRevision: initial.revision });
  const runtime = await call(page, 'xamlg_runtime_run', { expectedRevision: changed.revision });
  const layout = await call(page, 'xamlg_layout_get');
  const floated = await call(page, 'xamlg_layout_content', { contentId: 'source', operation: 'floatInPage' });
  expect(floated.layout).not.toBe(layout.layout);
  expect((await call(page, 'xamlg_document_read', { path: 'View.axaml' })).text).toBe(text);
  await call(page, 'xamlg_layout_content', { contentId: 'source', operation: 'dock' });
  await call(page, 'xamlg_layout_set', { layout: layout.layout });
  await expect(page.locator('.source-pane .editor-page.visible')).toBeVisible();
  expect((await call(page, 'xamlg_document_read', { path: 'View.axaml' })).text).toBe(text);
  expect((await call(page, 'xamlg_runtime_tree')).sessionId).toBe(runtime.sessionId);
  await expect(page.locator('#avalonia-preview canvas').first()).toBeVisible();
  await call(page, 'xamlg_layout_content', { contentId: 'explorer', operation: 'hide' });
  await call(page, 'xamlg_layout_content', { contentId: 'explorer', operation: 'show' });
  await expect(page.getByRole('complementary')).toContainText('EXPLORER');
});

test('runtime object paths, bindings, styles and tree mutations operate on the real preview', async ({ page }) => {
  await enable(page);
  const project = await call(page, 'xamlg_project_get');
  const code = 'namespace RuntimeDemo; public class Model { public string Title { get; set; } = "Initial"; public string Rename(string value) => Title = value; }';
  const codeWrite = await call(page, 'xamlg_document_write', { path: 'Code.cs', text: code, expectedRevision: project.revision });
  const xaml = '<StackPanel xmlns="https://github.com/avaloniaui"><TextBlock Name="target" Text="Source text" /></StackPanel>';
  const source = await call(page, 'xamlg_document_write', { path: 'View.axaml', text: xaml, expectedRevision: codeWrite.revision });
  let tree = await call(page, 'xamlg_runtime_run', { expectedRevision: source.revision });
  const rootId = tree.rootId, targetId = tree.nodes.find(node => node.name === 'target').id;
  const mutate = async (name, args) => {
    tree = await call(page, 'xamlg_runtime_tree');
    return call(page, name, { ...args, expectedRevision: tree.revision });
  };
  await mutate('xamlg_runtime_object_create', { objectId: rootId, path: ['DataContext'], type: 'RuntimeDemo.Model', initialValues: { Title: { value: 'Bound at runtime' } } });
  const model = await call(page, 'xamlg_runtime_object_inspect', { objectId: rootId, path: ['DataContext'] });
  expect(model.members.find(member => member.name === 'Title').value.value).toBe('Bound at runtime');
  expect(model.methods).toContain('Rename(System.String)');
  const properties = await call(page, 'xamlg_runtime_properties', { objectId: targetId });
  const textKey = properties.properties.find(property => property.name === 'Text' && property.owner === 'Avalonia.Controls.TextBlock').key;
  const fontKey = properties.properties.find(property => property.name === 'FontSize' && property.kind !== 'clr').key;
  await mutate('xamlg_runtime_binding_set', { objectId: targetId, property: textKey, path: 'Title', mode: 'OneWay' });
  expect((await call(page, 'xamlg_runtime_bindings', { objectId: targetId })).bindings.find(binding => binding.property === textKey).value.value).toBe('Bound at runtime');
  await mutate('xamlg_runtime_method_invoke', { objectId: rootId, path: ['DataContext'], signature: 'Rename(System.String)', arguments: [{ value: 'Method invoked' }] });
  await mutate('xamlg_runtime_binding_update', { objectId: targetId, property: textKey });
  expect((await call(page, 'xamlg_runtime_object_read', { objectId: rootId, path: ['DataContext', 'Title'] })).value.value).toBe('Method invoked');
  const style = await mutate('xamlg_runtime_style_add', { objectId: rootId, targetType: 'Avalonia.Controls.TextBlock', setters: { [fontKey]: { value: 37 } } });
  const frames = await call(page, 'xamlg_runtime_value_frames', { objectId: targetId });
  expect(frames.frames.some(frame => frame.active && frame.values.some(value => value.property === fontKey && value.value.value === 37))).toBe(true);
  await mutate('xamlg_runtime_style_remove', { objectId: rootId, index: style.index });
  const created = await mutate('xamlg_runtime_child_create', { parentId: rootId, type: 'Avalonia.Controls.Border', initialValues: { Name: { value: 'newParent' } } });
  const parentId = created.nodes.find(node => node.name === 'newParent').id;
  await mutate('xamlg_runtime_child_reparent', { objectId: targetId, parentId });
  const moved = await call(page, 'xamlg_runtime_tree');
  expect(moved.nodes.find(node => node.id === targetId).logicalParent).toBe(parentId);
  await mutate('xamlg_runtime_child_remove', { objectId: targetId });
  await expect(call(page, 'xamlg_runtime_properties', { objectId: targetId })).rejects.toThrow();
  expect((await call(page, 'xamlg_project_get')).revision).toBe(source.revision);
  expect((await call(page, 'xamlg_document_read', { path: 'View.axaml' })).text).toBe(xaml);
});
