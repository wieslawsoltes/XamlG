import { test, expect } from './studio-fixture.mjs';
import { call, connectMcp, openStudio, writeDocument } from './live-preview.mjs';

async function command(page, path, selection, action) {
  await expect(page.getByRole('button', { name: 'Compile', exact: true })).toBeEnabled();
  await page.evaluate(({ path, selection, action }) => {
    const editor = monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/' + path));
    const model = editor.getModel(), offset = model.getValue().indexOf(selection);
    if (offset < 0) throw new Error('Missing selection: ' + selection);
    editor.setPosition(model.getPositionAt(offset)); editor.focus();
    const selected = editor.getAction(action);
    if (selected) void selected.run();
    else editor.trigger('keyboard', action, null);
  }, { path, selection, action });
}
const editorText = (page, path) => page.evaluate(path => monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/' + path))?.getValue(), path);

test('HTTP MCP inspects nested flow graphs, data flow, symbols, generic overrides and generated operations', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectMcp(page, request);
  try {
    const contract = 'namespace Model; public interface IValue<T> { T Get(T value); }';
    const code = `using System;
namespace Model;
public class Base<T> : IValue<T> { public virtual T Get(T value) => value; }
public class Probe {
  public int Run(int seed) {
    int Local(int delta) {
      Func<int, int> map = value => seed + value + delta;
      return map(2);
    }
    return Local(3);
  }
  public int Data(int input) { int result = input; result += 2; return result; }
}`;
    const derived = 'namespace Model; public class Derived : Base<int> { public override int Get(int value) => value + 1; }';
    await writeDocument(mcp.call, 'Models/Contract.cs', contract);
    await writeDocument(mcp.call, 'Models/Derived.cs', derived);
    await writeDocument(mcp.call, 'Code.cs', code);
    expect((await mcp.call('xamlg_compiler_compile')).success).toBe(true);
    const implementations = (await mcp.call('xamlg_csharp_implementations', { path: 'Models/Contract.cs', offset: contract.indexOf('Get(') })).implementations;
    expect(implementations.truncated).toBe(false);
    expect(implementations.locations.map(location => location.path).sort()).toEqual(['Code.cs', 'Models/Derived.cs']);
    const hierarchy = (await mcp.call('xamlg_csharp_type_hierarchy', { path: 'Code.cs', offset: code.indexOf('Base<T>') })).hierarchy;
    expect(hierarchy.derivedTypes.map(type => type.name)).toContain('Derived');
    const members = (await mcp.call('xamlg_csharp_members', { path: 'Models/Derived.cs', offset: derived.indexOf('Derived'), includeInherited: false })).members;
    expect(members.symbols.map(symbol => symbol.name)).toEqual(['Get']);
    const definitions = (await mcp.call('xamlg_csharp_type_definitions', { path: 'Models/Derived.cs', offset: derived.indexOf('Base<int>') })).definitions;
    expect(definitions[0].path).toBe('Code.cs');
    const outline = (await mcp.call('xamlg_csharp_document_symbols', { path: 'Code.cs', includeLocals: true })).outline;
    const local = outline.symbols.find(symbol => symbol.name === 'Local');
    expect(outline.symbols[local.parentId].name).toBe('Run');
    const search = (await mcp.call('xamlg_csharp_symbols', { query: 'Derived' })).symbols;
    expect(search.symbols[0].locations[0].path).toBe('Models/Derived.cs');
    const operations = (await mcp.call('xamlg_csharp_operations', { path: 'Code.cs', maxDepth: 32 })).operations;
    expect(operations.truncated).toBe(false);
    expect(operations.nodes.some(node => node.kind === 'AnonymousFunction')).toBe(true);
    const graph = (await mcp.call('xamlg_csharp_control_flow', { path: 'Code.cs', offset: code.indexOf('seed +') })).controlFlow;
    expect(code.slice(graph.body.start, graph.body.start + graph.body.length)).toContain('seed + value + delta');
    expect(code.slice(graph.parentBody.start, graph.parentBody.start + graph.parentBody.length)).toContain('return map(2)');
    expect(graph.regions[0].parentId).toBeNull(); expect(graph.truncated).toBe(false);
    expect(graph.blocks[0].kind).toBe('Entry'); expect(graph.blocks.at(-1).kind).toBe('Exit');
    const range = 'int result = input; result += 2;';
    const flow = (await mcp.call('xamlg_csharp_data_flow', { path: 'Code.cs', offset: code.indexOf(range), length: range.length })).dataFlow;
    expect(flow.succeeded).toBe(true); expect(flow.regionKind).toBe('StatementRange');
    expect(flow.sets.dataFlowsIn.symbols.map(id => flow.symbols[id].name)).toEqual(['input']);
    expect(flow.sets.dataFlowsOut.symbols.map(id => flow.symbols[id].name)).toEqual(['result']);
    const limited = (await mcp.call('xamlg_csharp_control_flow', { path: 'Code.cs', offset: code.indexOf('Run('), maxBlocks: 1, maxNodes: 1 })).controlFlow;
    expect(limited.truncated).toBe(true); expect(limited.blocks).toHaveLength(1);
    const generated = (await mcp.call('xamlg_generated_list')).files[0].path;
    const generatedOperations = (await mcp.call('xamlg_csharp_operations', { path: generated, maxDepth: 32 })).operations;
    expect(generatedOperations.isGenerated).toBe(true); expect(generatedOperations.nodes.length).toBeGreaterThan(0);
    const generatedGraph = (await mcp.call('xamlg_csharp_control_flow', { path: generated, offset: generatedOperations.nodes[generatedOperations.roots[0]].span.start })).controlFlow;
    expect(generatedGraph.body.isGenerated).toBe(true);
    const revision = (await mcp.call('xamlg_project_get')).revision;
    await expect(mcp.call('xamlg_csharp_format', { path: generated, expectedRevision: revision })).rejects.toThrow();
  } finally { await mcp.close(); }
});

test('owner Monaco type and implementation navigation, outline and new source actions work with sharing disabled', async ({ page }) => {
  await openStudio(page);
  const invoke = (name, args) => call(page, name, args);
  await writeDocument(invoke, 'Models/Contract.cs', 'namespace Model; public interface IValue<T> { T Get(T value); }');
  const item = 'namespace Model; public class Item : IValue<int> { public int Get(int value) => value + 1; }';
  await writeDocument(invoke, 'Models/Item.cs', item);
  await writeDocument(invoke, 'Code.cs', 'namespace Model; public class Consumer { public IValue<int> Create() => new Item(); public int Read(IValue<int> value) => value.Get(1); }');
  await page.getByTestId('agent-access').click();
  await page.getByLabel('Enable access to this live project').uncheck();
  await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
  await page.locator('[data-tab-id="document:Code.cs"]').click();
  await command(page, 'Code.cs', 'Create()', 'editor.action.goToTypeDefinition');
  const files = page.getByRole('region', { name: 'C# project documents' });
  await expect(page.locator('[data-tab-id="document:Models%2FContract.cs"]')).toHaveAttribute('aria-selected', 'true');
  await command(page, 'Models/Contract.cs', 'Get(T', 'editor.action.goToImplementation');
  await expect(page.locator('[data-tab-id="document:Models%2FItem.cs"]')).toHaveAttribute('aria-selected', 'true');
  await command(page, 'Models/Item.cs', 'Item :', 'editor.action.quickOutline');
  await expect(page.locator('.quick-input-widget')).toBeVisible();
  // The docked pane's list is virtualized; search for the method before choosing it.
  await page.locator('.quick-input-widget').getByRole('combobox').fill('@Get');
  await expect(page.locator('.quick-input-widget')).toContainText('Get', { timeout: 15000 });
  await page.keyboard.press('Enter');
  await expect(page.locator('.quick-input-widget')).not.toBeVisible();
  await command(page, 'Models/Item.cs', 'Get(int', 'xamlg.actions');
  await page.getByRole('dialog', { name: 'Source code actions' }).getByRole('button', { name: 'Use block body', exact: true }).click();
  await expect.poll(() => editorText(page, 'Models/Item.cs')).toContain('return value + 1;');
  await page.getByRole('button', { name: 'Undo', exact: true }).click();
  await expect.poll(() => editorText(page, 'Models/Item.cs')).toBe(item);
});

test('cross-language rename UI rejects a stale preview and applies all source files as one Undo transaction', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectMcp(page, request);
  try {
    const code = 'namespace Demo; public partial class View : Avalonia.Controls.UserControl { public View() { InitializeComponent(); } public string Read() => Labels.Title; }';
    const labels = 'namespace Demo; public static class Labels { public const string Title = "Renamed successfully"; }';
    const xaml = '<UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:local="clr-namespace:Demo" x:Class="Demo.View"><TextBlock x:Name="label" Text="{x:Static local:Labels.Title}"/></UserControl>';
    await writeDocument(mcp.call, 'Code.cs', code);
    await writeDocument(mcp.call, 'Models/Labels.cs', labels);
    await writeDocument(mcp.call, 'View.axaml', xaml);
    expect((await mcp.call('xamlg_compiler_compile')).success).toBe(true);
    await page.locator('[data-tab-id="document:Code.cs"]').click();
    const dialog = page.getByRole('dialog', { name: 'Rename C# symbol', exact: true });
    async function preview() {
      await command(page, 'Code.cs', 'Title;', 'xamlg.rename');
      await dialog.getByLabel('New name', { exact: true }).fill('Caption');
      await dialog.getByRole('button', { name: 'Preview rename', exact: true }).click();
      await expect(dialog.getByTestId('rename-files').locator('article')).toHaveCount(3);
    }
    await preview();
    expect((await mcp.call('xamlg_document_read', { path: 'View.axaml' })).text).toBe(xaml);
    await writeDocument(mcp.call, 'Models/Unrelated.cs', 'namespace Demo; public class Unrelated {}');
    await dialog.getByRole('button', { name: 'Apply rename', exact: true }).click();
    await expect(dialog.getByRole('alert')).toContainText(/revision|changed/i);
    expect((await mcp.call('xamlg_document_read', { path: 'Models/Labels.cs' })).text).toBe(labels);
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await preview();
    await dialog.getByRole('button', { name: 'Apply rename', exact: true }).click();
    await expect(dialog).not.toBeVisible();
    await expect(page.getByRole('button', { name: 'Compile', exact: true })).toBeEnabled();
    expect((await mcp.call('xamlg_document_read', { path: 'View.axaml' })).text).toContain('local:Labels.Caption');
    expect((await mcp.call('xamlg_document_read', { path: 'Code.cs' })).text).toContain('Labels.Caption');
    const revision = (await mcp.call('xamlg_project_get')).revision;
    const tree = await mcp.call('xamlg_runtime_run', { expectedRevision: revision });
    const label = tree.nodes.find(node => node.name === 'label');
    expect((await mcp.call('xamlg_runtime_properties', { objectId: label.id })).properties.find(property => property.name === 'Text').value.value).toBe('Renamed successfully');
    await page.getByRole('button', { name: 'Undo', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Compile', exact: true })).toBeEnabled();
    for (const [path, text] of [['Code.cs', code], ['Models/Labels.cs', labels], ['View.axaml', xaml]])
      expect((await mcp.call('xamlg_document_read', { path })).text).toBe(text);
    expect((await mcp.call('xamlg_document_read', { path: 'Models/Unrelated.cs' })).text).toContain('class Unrelated');
  } finally { await mcp.close(); }
});
