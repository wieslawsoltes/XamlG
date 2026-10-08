import { test, expect } from './studio-fixture.mjs';
import { call, source, openStudio, setSourceAndRun, connectMcp, writeDocument, mutateRuntime } from './live-preview.mjs';

const ns = 'xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"';
const canvas = `<Canvas ${ns} Background="White"><Border Name="a" Canvas.Left="20" Canvas.Top="20" Width="60" Height="40" MaxWidth="120" Background="Red"/><Border Name="b" Canvas.Left="200" Canvas.Top="100" Width="40" Height="30" Background="Blue"/></Canvas>`;
const named = tree => tree.nodes.filter(node => node.name === 'a' || node.name === 'b');

test('HTTP MCP designer plans check live revisions, constraints and explicit reload before source undo', async ({ page, request }) => {
  test.skip(!process.env.XAMLG_TEST_MCP_URL, 'Requires the real companion.');
  await openStudio(page); const client = await connectMcp(page, request); const invoke = client.call;
  try {
    const edit = await writeDocument(invoke, 'View.axaml', canvas);
    let tree = await invoke('xamlg_runtime_run', { expectedRevision: edit.revision });
    let nodes = named(tree), state = await invoke('xamlg_designer_state');
    expect(state.previewMatchesSource).toBe(true);
    await invoke('xamlg_designer_select', { objectIds: nodes.map(node => node.id), expectedDesignerRevision: state.designerRevision, expectedRuntimeRevision: state.runtimeRevision });
    await expect(invoke('xamlg_designer_configure', { enabled: true, expectedDesignerRevision: state.designerRevision })).rejects.toThrow(/changed/i);
    const hit = await invoke('xamlg_designer_hit_test', { x: 45, y: 40 });
    expect(hit.node.id).toBe(nodes[0].id);
    tree = await invoke('xamlg_runtime_tree');
    const args = { objectIds: nodes.map(node => node.id), operation: 'alignRight', anchorId: nodes[1].id, expectedSourceRevision: edit.revision, expectedRuntimeRevision: tree.revision };
    const plan = await invoke('xamlg_designer_arrange_plan', args);
    expect(plan.applied).toBe(false); expect(plan.documents.map(document => document.path)).toEqual(['View.axaml']);
    expect((await invoke('xamlg_document_read', { path: 'View.axaml' })).text).toBe(canvas);
    const target = (await invoke('xamlg_designer_targets', { objectIds: [nodes[0].id], expectedRuntimeRevision: tree.revision })).targets[0];
    await expect(invoke('xamlg_designer_geometry_apply', { items: [{ objectId: nodes[0].id, bounds: { ...target.bounds, width: 121 } }], expectedSourceRevision: edit.revision, expectedRuntimeRevision: tree.revision })).rejects.toThrow(/constraint|maximum/i);
    await mutateRuntime(invoke, 'xamlg_runtime_object_set', { objectId: nodes[0].id, path: ['Width'], argument: { value: 70 } });
    await expect(invoke('xamlg_designer_arrange_apply', args)).rejects.toThrow(/revision|changed/i);
    tree = await invoke('xamlg_runtime_run', { expectedRevision: edit.revision }); nodes = named(tree);
    await expect(invoke('xamlg_designer_targets', { objectIds: [target.objectId], expectedRuntimeRevision: tree.revision })).rejects.toThrow();
    const applied = await invoke('xamlg_designer_arrange_apply', { ...args, objectIds: nodes.map(node => node.id), anchorId: nodes[1].id, expectedRuntimeRevision: tree.revision });
    expect(applied.sourceRevision).toBe(edit.revision + 1);
    expect((await invoke('xamlg_runtime_tree')).sessionId).toBe(tree.sessionId);
    expect((await invoke('xamlg_designer_state')).previewMatchesSource).toBe(false);
    tree = await invoke('xamlg_runtime_run', { expectedRevision: applied.sourceRevision }); nodes = named(tree);
    expect(nodes[0].bounds.rootX + nodes[0].bounds.width).toBe(nodes[1].bounds.rootX + nodes[1].bounds.width);
    await invoke('xamlg_project_undo', { expectedRevision: applied.sourceRevision });
    expect((await invoke('xamlg_document_read', { path: 'View.axaml' })).text).toBe(canvas);
  } finally { await client.close(); }
});

test('HTTP MCP edits main and template resource source in one undo and rejects shared instances', async ({ page, request }) => {
  test.skip(!process.env.XAMLG_TEST_MCP_URL, 'Requires the real companion.');
  await openStudio(page); const client = await connectMcp(page, request); const invoke = client.call;
  try {
    const resource = `<ResourceDictionary ${ns}>\r\n<!--template--><DataTemplate x:Key="Card"><Border Name="card" Width="60" Height="30" Background="Blue" HorizontalAlignment="Left"/></DataTemplate></ResourceDictionary>`;
    const content = '<ContentControl Content="one" ContentTemplate="{StaticResource Card}"/>';
    const xaml = `<StackPanel ${ns}><StackPanel.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceInclude Source="Cards.axaml"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></StackPanel.Resources><Border Name="main" Width="40" Height="20" Background="Red"/>${content}</StackPanel>`;
    await writeDocument(invoke, 'Cards.axaml', resource);
    const edit = await writeDocument(invoke, 'View.axaml', xaml);
    let tree = await invoke('xamlg_runtime_run', { expectedRevision: edit.revision });
    const controls = tree.nodes.filter(node => node.name === 'main' || node.name === 'card');
    expect(controls).toHaveLength(2); expect(controls.map(node => node.source.path).sort()).toEqual(['Cards.axaml', 'View.axaml']);
    const state = await invoke('xamlg_designer_state'); const card = controls.find(node => node.name === 'card');
    await invoke('xamlg_designer_select', { path: 'Cards.axaml', offset: card.source.start, expectedSourceRevision: edit.revision, expectedDesignerRevision: state.designerRevision, expectedRuntimeRevision: state.runtimeRevision });
    expect((await invoke('xamlg_designer_state')).selectedObjectIds).toEqual([card.id]);
    tree = await invoke('xamlg_runtime_tree');
    const targets = await invoke('xamlg_designer_targets', { objectIds: controls.map(node => node.id), expectedRuntimeRevision: tree.revision });
    const args = { items: targets.targets.map(target => ({ objectId: target.objectId, bounds: { ...target.bounds, x: target.bounds.x + 8, y: target.bounds.y + 8 } })), expectedSourceRevision: edit.revision, expectedRuntimeRevision: tree.revision };
    const plan = await invoke('xamlg_designer_geometry_plan', args);
    expect(plan.documents.map(document => document.path).sort()).toEqual(['Cards.axaml', 'View.axaml']);
    const applied = await invoke('xamlg_designer_geometry_apply', args);
    expect(applied.sourceRevision).toBe(edit.revision + 1);
    expect((await invoke('xamlg_document_read', { path: 'Cards.axaml' })).text).toContain('\r\n<!--template-->');
    tree = await invoke('xamlg_runtime_run', { expectedRevision: applied.sourceRevision });
    for (const original of controls) {
      const after = tree.nodes.find(node => node.name === original.name);
      expect(after.bounds.rootX).toBeCloseTo(original.bounds.rootX + 8, 5);
      expect(after.bounds.rootY).toBeCloseTo(original.bounds.rootY + 8, 5);
    }
    await invoke('xamlg_project_undo', { expectedRevision: applied.sourceRevision });
    expect((await invoke('xamlg_document_read', { path: 'View.axaml' })).text).toBe(xaml);
    expect((await invoke('xamlg_document_read', { path: 'Cards.axaml' })).text).toBe(resource);
    const duplicated = await writeDocument(invoke, 'View.axaml', xaml.replace('</StackPanel>', content + '</StackPanel>'));
    tree = await invoke('xamlg_runtime_run', { expectedRevision: duplicated.revision });
    const cards = tree.nodes.filter(node => node.name === 'card'); expect(cards).toHaveLength(2);
    await expect(invoke('xamlg_designer_targets', { objectIds: [cards[0].id], expectedRuntimeRevision: tree.revision })).rejects.toThrow(/shared|same source|instances/i);
  } finally { await client.close(); }
});

test('owner designer workbench reviews plans and detects source conflicts while remote access is disabled', async ({ page }) => {
  await openStudio(page, false); await setSourceAndRun(page, canvas);
  await expect(call(page, 'xamlg_designer_state')).rejects.toThrow();
  await page.getByRole('tab', { name: 'Designer', exact: true }).click();
  const designer = page.getByRole('region', { name: 'XAML visual designer', exact: true });
  for (const name of ['a', 'b']) await designer.locator('[aria-label="Designer controls"] label').filter({ hasText: `${name} · Border` }).getByRole('checkbox').check();
  await designer.getByRole('button', { name: 'Select in preview', exact: true }).click();
  await designer.getByRole('combobox', { name: 'Arrange', exact: true }).selectOption('SameWidth');
  await designer.getByRole('combobox', { name: 'Alignment / size anchor' }).selectOption({ label: 'b · Border' });
  await designer.getByRole('button', { name: 'Preview arrangement', exact: true }).click();
  await expect(designer).toContainText('Planned XAML changes'); expect(await source(page)).toBe(canvas);
  await designer.getByRole('button', { name: 'Apply source edits', exact: true }).click();
  await expect(designer).toContainText('Applied to XAML');
  await expect.poll(() => source(page)).toContain('Width="40" Height="40"');
  await designer.getByRole('button', { name: 'Reload trusted preview', exact: true }).click();
  await expect(designer.getByRole('alert')).toHaveCount(0);
  await page.getByTitle('Undo source edit').click(); await expect.poll(() => source(page)).toBe(canvas);
  await designer.getByRole('button', { name: 'Reload trusted preview', exact: true }).click();
  for (const name of ['a', 'b']) await designer.locator('[aria-label="Designer controls"] label').filter({ hasText: `${name} · Border` }).getByRole('checkbox').check();
  await designer.getByRole('combobox', { name: 'Arrange', exact: true }).selectOption('SameHeight');
  await designer.getByRole('button', { name: 'Preview arrangement', exact: true }).click();
  await expect(designer.getByRole('button', { name: 'Apply source edits' })).toBeEnabled();
  await page.evaluate(value => monaco.editor.getModels().find(model => model.getLanguageId() === 'xml').setValue(value), canvas + '\n<!--external edit-->');
  await expect(designer.getByRole('button', { name: 'Apply source edits' })).toBeDisabled();
  await expect(call(page, 'xamlg_project_get')).rejects.toThrow();
});

test('browser group drag publishes one source transaction and one undo restores both controls', async ({ page }) => {
  await openStudio(page); await setSourceAndRun(page, canvas);
  const state = await call(page, 'xamlg_designer_state');
  await call(page, 'xamlg_designer_configure', { enabled: true, gridSize: 0, expectedDesignerRevision: state.designerRevision });
  const box = await page.locator('#avalonia-preview').boundingBox();
  await page.mouse.click(box.x + 50, box.y + 40);
  await page.keyboard.down('Control'); await page.mouse.click(box.x + 220, box.y + 115); await page.keyboard.up('Control');
  expect((await call(page, 'xamlg_designer_state')).selectedObjectIds).toHaveLength(2);
  const before = await call(page, 'xamlg_project_get');
  await page.mouse.move(box.x + 50, box.y + 40); await page.mouse.down();
  await page.mouse.move(box.x + 74, box.y + 56, { steps: 8 }); await page.mouse.up();
  await expect(page.locator('.statusbar')).toContainText('Designer edit committed');
  expect((await call(page, 'xamlg_project_get')).revision).toBe(before.revision + 1);
  expect(await source(page)).toContain('Canvas.Left="44" Canvas.Top="36"');
  expect(await source(page)).toContain('Canvas.Left="224" Canvas.Top="116"');
  await page.getByTitle('Undo source edit').click(); await expect.poll(() => source(page)).toBe(canvas);
});

test('HTTP MCP read-only policy allows designer plans and runtime reads while denying edits and input', async ({ page, request }) => {
  test.skip(!process.env.XAMLG_TEST_MCP_URL, 'Requires the real companion.');
  await openStudio(page); const client = await connectMcp(page, request); const invoke = client.call;
  try {
    const edit = await writeDocument(invoke, 'View.axaml', canvas);
    const tree = await invoke('xamlg_runtime_run', { expectedRevision: edit.revision });
    const target = (await invoke('xamlg_designer_targets', { objectIds: [named(tree)[0].id], expectedRuntimeRevision: tree.revision })).targets[0];
    const args = { items: [{ objectId: target.objectId, bounds: { ...target.bounds, x: target.bounds.x + 8 } }], expectedSourceRevision: edit.revision, expectedRuntimeRevision: tree.revision };
    await page.getByTestId('agent-access').click();
    await page.getByLabel('Permission profile').selectOption('ReadOnly');
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    expect((await invoke('xamlg_designer_geometry_plan', args)).documents).toHaveLength(1);
    expect((await invoke('xamlg_runtime_properties', { objectId: target.objectId })).properties.length).toBeGreaterThan(0);
    await expect(invoke('xamlg_designer_geometry_apply', args)).rejects.toThrow(/permission|permit|denied|policy/i);
    await expect(mutateRuntime(invoke, 'xamlg_runtime_input_pointer', { objectId: target.objectId, action: 'click' })).rejects.toThrow(/permission|permit|denied|policy/i);
    expect((await invoke('xamlg_document_read', { path: 'View.axaml' })).text).toBe(canvas);
    await page.getByTestId('agent-access').click();
    await page.getByRole('button', { name: 'Revoke & disconnect' }).click();
    await expect.poll(async () => (await client.rpc('tools/list')).tools.length).toBe(0);
    await expect(invoke('xamlg_runtime_tree')).rejects.toThrow();
  } finally { await client.close(); }
});
