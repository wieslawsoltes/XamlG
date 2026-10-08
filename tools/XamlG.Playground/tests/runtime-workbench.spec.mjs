import { test, expect } from './studio-fixture.mjs';
import { call, source, openStudio, setSourceAndRun, connectMcp, writeDocument, mutateRuntime } from './live-preview.mjs';

const ns = 'xmlns="https://github.com/avaloniaui"';
const controls = `<Canvas ${ns} Background="White">
<TextBox Name="editor" AutomationProperties.Name="Editor" Canvas.Left="10" Canvas.Top="10" Width="220" Height="40" Text="seed"/>
<Button Name="go" Canvas.Left="10" Canvas.Top="60" Width="100" Height="40" Content="Go"/>
<CheckBox Name="flag" Canvas.Left="10" Canvas.Top="110" Width="100" Height="30" Content="Flag"/>
<Slider Name="range" Canvas.Left="10" Canvas.Top="150" Width="200" Height="40" Minimum="0" Maximum="10" Value="2"/>
<ListBox Name="selection" Canvas.Left="10" Canvas.Top="200" Width="180" Height="80"><ListBoxItem Name="first" Content="First"/><ListBoxItem Name="second" Content="Second"/></ListBox>
</Canvas>`;

test('HTTP MCP sends real input and invokes accessibility providers with retained selection results', async ({ page, request }) => {
  test.skip(!process.env.XAMLG_TEST_MCP_URL, 'Requires the real companion.');
  await openStudio(page); const client = await connectMcp(page, request); const invoke = client.call;
  try {
    const edit = await writeDocument(invoke, 'View.axaml', controls);
    const tree = await invoke('xamlg_runtime_run', { expectedRevision: edit.revision });
    const id = name => tree.nodes.find(node => node.name === name).id;
    const mutate = (name, args) => mutateRuntime(invoke, name, args);
    const read = async (name, property) => (await invoke('xamlg_runtime_object_read', { objectId: id(name), path: [property] })).value.value;
    await mutate('xamlg_runtime_input_key', { objectId: id('editor'), key: 'A', modifiers: ['Control'] });
    await mutate('xamlg_runtime_input_text', { objectId: id('editor'), text: 'typed β' });
    expect(await read('editor', 'Text')).toBe('typed β');
    await invoke('xamlg_runtime_event_watch', { objectId: id('go'), event: 'Click' });
    const before = await invoke('xamlg_runtime_changes');
    await mutate('xamlg_runtime_input_pointer', { objectId: id('go'), action: 'click' });
    expect((await invoke('xamlg_runtime_changes', { afterSequence: before.sequence })).changes.some(change => change.kind === 'event' && change.name === 'Click')).toBe(true);
    await mutate('xamlg_runtime_input_touch', { objectId: id('go'), contactId: 7, action: 'begin', x: 20, y: 20 });
    await mutate('xamlg_runtime_input_touch', { objectId: id('go'), contactId: 7, action: 'move', x: 25, y: 20 });
    await mutate('xamlg_runtime_input_touch', { objectId: id('go'), contactId: 7, action: 'end', x: 25, y: 20 });
    await mutate('xamlg_runtime_input_reset', {});
    const state = await invoke('xamlg_designer_state');
    const enabled = await invoke('xamlg_designer_configure', { enabled: true, expectedDesignerRevision: state.designerRevision });
    await expect(mutate('xamlg_runtime_input_text', { objectId: id('editor'), text: 'blocked' })).rejects.toThrow(/design mode/i);
    await invoke('xamlg_designer_configure', { enabled: false, expectedDesignerRevision: enabled.designerRevision });

    const firstPage = await invoke('xamlg_runtime_accessibility', { count: 2 });
    expect(firstPage.nodes).toHaveLength(2); expect(firstPage.hasMore).toBe(true);
    const peers = (await invoke('xamlg_runtime_accessibility')).nodes;
    const peer = name => peers.find(item => item.objectId === id(name));
    const provider = async (name, contract) => {
      const item = peer(name); expect(item).toBeTruthy();
      const type = item.providers.find(value => value.endsWith(contract)); expect(type).toBeTruthy();
      const result = await invoke('xamlg_runtime_accessibility_provider', { peerId: item.id, provider: type });
      return { peerId: item.id, provider: type, methods: result.methods };
    };
    const invokeProvider = async (name, contract, signature, args = []) => {
      const contractInfo = await provider(name, contract); expect(contractInfo.methods).toContain(signature);
      return mutate('xamlg_runtime_accessibility_invoke', { peerId: contractInfo.peerId, provider: contractInfo.provider, signature, arguments: args });
    };
    await invokeProvider('flag', 'IToggleProvider', 'Toggle()'); expect(await read('flag', 'IsChecked')).toBe(true);
    await invokeProvider('range', 'IRangeValueProvider', 'SetValue(System.Double)', [{ value: 7 }]); expect(await read('range', 'Value')).toBe(7);
    await invokeProvider('editor', 'IValueProvider', 'SetValue(System.String)', [{ value: 'provider text' }]); expect(await read('editor', 'Text')).toBe('provider text');
    const eventSequence = (await invoke('xamlg_runtime_changes')).sequence;
    await invokeProvider('go', 'IInvokeProvider', 'Invoke()');
    await expect.poll(async () => (await invoke('xamlg_runtime_changes', { afterSequence: eventSequence })).changes.filter(change => change.kind === 'event').length).toBeGreaterThan(0);
    await invokeProvider('second', 'ISelectionItemProvider', 'Select()'); expect(await read('selection', 'SelectedIndex')).toBe(1);
    const result = await invokeProvider('selection', 'ISelectionProvider', 'GetSelection()');
    expect(result.result.objectId).toBeTruthy();
    const selection = await invoke('xamlg_runtime_object_read', { objectId: result.result.objectId, path: ['0'] });
    expect(selection.value.objectId).toBe(peer('second').id);
    expect((await invoke('xamlg_runtime_object_handles')).handles.some(handle => handle.id === result.result.objectId)).toBe(true);
    await invoke('xamlg_runtime_object_handles_release', { objectIds: [result.result.objectId] });
    await expect(invoke('xamlg_runtime_object_inspect', { objectId: result.result.objectId })).rejects.toThrow();
    expect((await invoke('xamlg_project_get')).revision).toBe(edit.revision);
    expect((await invoke('xamlg_document_read', { path: 'View.axaml' })).text).toBe(controls);
    await invoke('xamlg_runtime_run', { expectedRevision: edit.revision });
    await expect(invoke('xamlg_runtime_accessibility_provider', { peerId: peer('go').id, provider: 'IInvokeProvider' })).rejects.toThrow();
  } finally { await client.close(); }
});

const model = `using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
namespace LiveRuntime;
public interface ICounter { int Value { get; set; } int Add(int amount); }
public struct Position { public int X { get; set; } }
public class Model : INotifyPropertyChanged {
  public event PropertyChangedEventHandler? PropertyChanged;
  private string title = "initial";
  public string Title { get => title; set { title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); } }
  public Position Position { get; set; }
  public Dictionary<int, string> Labels { get; } = new() { [7] = "seven", [9] = "nine" };
  public ObservableCollection<string> Items { get; } = new();
  public ICounter Counter { get; } = new CounterImpl();
  public ICounter GetCounter() => Counter;
  public int Borrow(ICounter value) => value.Value;
  private class CounterImpl : ICounter { int ICounter.Value { get; set; } = 3; int ICounter.Add(int amount) => ((ICounter)this).Value += amount; }
}`;

test('HTTP MCP retains explicit-interface objects and observes typed dictionary, collection and struct edits', async ({ page, request }) => {
  test.skip(!process.env.XAMLG_TEST_MCP_URL, 'Requires the real companion.');
  await openStudio(page); const client = await connectMcp(page, request); const invoke = client.call;
  try {
    await writeDocument(invoke, 'Code.cs', model);
    const edit = await writeDocument(invoke, 'View.axaml', `<Border ${ns} Background="White"/>`);
    const tree = await invoke('xamlg_runtime_run', { expectedRevision: edit.revision });
    const objectId = tree.rootId, path = ['DataContext']; const mutate = (name, args) => mutateRuntime(invoke, name, args);
    await mutate('xamlg_runtime_object_create', { objectId, path, type: 'LiveRuntime.Model' });
    await invoke('xamlg_runtime_object_inspect', { objectId, path });
    const borrowed = await mutate('xamlg_runtime_method_invoke', { objectId, path, signature: 'GetCounter()', arguments: [] });
    const handle = borrowed.result.objectId; expect(handle).toBeTruthy();
    const info = await invoke('xamlg_runtime_object_inspect', { objectId: handle, interfaceName: 'LiveRuntime.ICounter' });
    expect(info.methods).toContain('Add(System.Int32)'); expect(info.members.find(member => member.name === 'Value').value.value).toBe(3);
    const result = await mutate('xamlg_runtime_method_invoke', { objectId: handle, interfaceName: 'LiveRuntime.ICounter', signature: 'Add(System.Int32)', arguments: [{ value: 4 }] });
    expect(result.result.value).toBe(7);
    expect((await mutate('xamlg_runtime_method_invoke', { objectId, path, signature: 'Borrow(LiveRuntime.ICounter)', arguments: [{ objectId: handle }] })).result.value).toBe(7);
    const entries = await invoke('xamlg_runtime_dictionary_entries', { objectId, path: [...path, 'Labels'], count: 1 });
    expect(entries.keyType).toBe('System.Int32'); expect(entries.hasMore).toBe(true);
    await mutate('xamlg_runtime_dictionary_set', { objectId, path: [...path, 'Labels'], key: { value: 7 }, value: { value: 'changed' } });
    expect((await invoke('xamlg_runtime_dictionary_read', { objectId, path: [...path, 'Labels'], key: { value: 7 } })).value.value).toBe('changed');
    await mutate('xamlg_runtime_object_set', { objectId, path: [...path, 'Position', 'X'], argument: { value: 42 } });
    expect((await invoke('xamlg_runtime_object_read', { objectId, path: [...path, 'Position', 'X'] })).value.value).toBe(42);
    await invoke('xamlg_runtime_object_inspect', { objectId, path: [...path, 'Items'] });
    const before = (await invoke('xamlg_runtime_changes')).sequence;
    await mutate('xamlg_runtime_object_set', { objectId, path: [...path, 'Title'], argument: { value: 'observed' } });
    await mutate('xamlg_runtime_method_invoke', { objectId, path: [...path, 'Items'], signature: 'Add(System.String)', arguments: [{ value: 'added' }] });
    const changes = (await invoke('xamlg_runtime_changes', { afterSequence: before })).changes;
    expect(changes.some(change => change.kind === 'object_property' && change.name === 'DataContext.Title')).toBe(true);
    expect(changes.some(change => change.kind === 'object_collection' && change.name === 'DataContext.Items.Add')).toBe(true);
    await invoke('xamlg_runtime_object_watches_clear');
    const cleared = (await invoke('xamlg_runtime_changes')).sequence;
    await mutate('xamlg_runtime_object_set', { objectId, path: [...path, 'Title'], argument: { value: 'unobserved' } });
    expect((await invoke('xamlg_runtime_changes', { afterSequence: cleared })).changes.some(change => change.kind === 'object_property')).toBe(false);
    await invoke('xamlg_runtime_object_handles_release', { objectIds: [handle] });
    await expect(invoke('xamlg_runtime_object_inspect', { objectId: handle })).rejects.toThrow();
    const next = await mutate('xamlg_runtime_method_invoke', { objectId, path, signature: 'GetCounter()', arguments: [] });
    expect(next.result.objectId).not.toBe(handle);
    expect((await invoke('xamlg_project_get')).revision).toBe(edit.revision);
    await invoke('xamlg_runtime_run', { expectedRevision: edit.revision });
    await expect(invoke('xamlg_runtime_object_inspect', { objectId: next.result.objectId })).rejects.toThrow();
  } finally { await client.close(); }
});

test('owner runtime workbench edits properties, sends input and uses accessibility with sharing disabled', async ({ page }) => {
  await openStudio(page, false); await setSourceAndRun(page, controls);
  await page.locator('[data-tab-id="runtime"]').click();
  const runtime = page.getByRole('region', { name: /^Avalonia runtime / }).filter({ visible: true });
  await runtime.getByRole('button', { name: 'editor · TextBox', exact: true }).click();
  await runtime.getByRole('combobox', { name: 'Tree', exact: true }).selectOption('logical');
  await runtime.getByLabel('Find property').fill('Text');
  const textProperty = runtime.locator('.runtime-property-list button').filter({ has: page.locator('strong').filter({ hasText: /^Text$/ }) });
  await textProperty.click();
  await runtime.getByLabel('Property value (JSON)').fill('"owner value"');
  await runtime.getByRole('button', { name: 'Set live property', exact: true }).click();
  await expect(textProperty).toContainText('owner value');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Tools$/ }).click();
  await page.locator('.studio-menu-items').getByRole('button', { name: 'Input', exact: true }).click();
  await runtime.getByLabel('Modifiers', { exact: true }).fill('Control'); await runtime.getByLabel('Key', { exact: true }).fill('A');
  await runtime.getByRole('button', { name: 'Press key', exact: true }).click();
  await runtime.getByLabel('Text', { exact: true }).fill('owner input β'); await runtime.getByRole('button', { name: 'Send text', exact: true }).click();
  await page.locator('.studio-menu > summary').filter({ hasText: /^Tools$/ }).click();
  await page.locator('.studio-menu-items').getByRole('button', { name: 'Runtime properties', exact: true }).click();
  await expect(textProperty).toContainText('owner input β');
  await page.locator('.studio-menu > summary').filter({ hasText: /^Tools$/ }).click();
  await page.locator('.studio-menu-items').getByRole('button', { name: 'Accessibility', exact: true }).click();
  await runtime.getByRole('button', { name: 'Inspect accessibility', exact: true }).click();
  const peers = runtime.getByRole('combobox', { name: 'Peer', exact: true });
  await peers.selectOption(await peers.locator('option').filter({ hasText: /^Editor · / }).getAttribute('value'));
  const providers = runtime.getByRole('combobox', { name: 'Provider', exact: true });
  await providers.selectOption(await providers.locator('option').filter({ hasText: /IValueProvider$/ }).textContent());
  await runtime.getByRole('button', { name: 'Inspect provider', exact: true }).click();
  await runtime.getByRole('combobox', { name: 'Provider method' }).selectOption('SetValue(System.String)');
  await runtime.getByLabel('Provider arguments').fill('[{"value":"owner provider"}]');
  await runtime.getByRole('button', { name: 'Invoke provider', exact: true }).click();
  await page.locator('.studio-menu > summary').filter({ hasText: /^Tools$/ }).click();
  await page.locator('.studio-menu-items').getByRole('button', { name: 'Runtime properties', exact: true }).click();
  await expect(textProperty).toContainText('owner provider'); await expect(runtime.getByRole('alert')).toHaveCount(0);
  await page.locator('[data-tab-id="runtime-accessibility"]').click();
  const downloadEvent = page.waitForEvent('download'); await runtime.getByRole('button', { name: 'Export result', exact: true }).click();
  expect((await downloadEvent).suggestedFilename()).toBe('xamlg-runtime-inspection.json');
  await page.locator('[data-tab-id="runtime"]').click();
  const oldHandle = await runtime.locator('h3 + code').textContent();
  await page.getByTestId('run-preview').click();
  await expect(runtime.locator('h3 + code')).not.toHaveText(oldHandle);
  await runtime.getByRole('button', { name: 'editor · TextBox', exact: true }).click();
  await expect(textProperty).toContainText('owner provider'); expect(await source(page)).toBe(controls);
  // Reload preserves input state when the declaration is unchanged; explicit
  // source changes must win while the docked inspector follows the new tree.
  const replacement = controls.replace('Text="seed"', 'Text="new declaration"');
  const previousHandle = await runtime.locator('h3 + code').textContent();
  await setSourceAndRun(page, replacement);
  await expect(runtime.locator('h3 + code')).not.toHaveText(previousHandle);
  await runtime.getByRole('button', { name: 'editor · TextBox', exact: true }).click();
  await expect(textProperty).toContainText('new declaration'); await expect(runtime.getByRole('alert')).toHaveCount(0);
  expect(await source(page)).toBe(replacement); await expect(call(page, 'xamlg_runtime_tree')).rejects.toThrow();
});
