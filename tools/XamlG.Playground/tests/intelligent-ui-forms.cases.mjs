import { test, expect } from './studio-fixture.mjs';
import { openStudio, connectMcp } from './live-preview.mjs';
import { mountNativeUi, clickNativeTopButton } from './intelligent-ui-host.mjs';

// Imported by intelligent-ui-parity.spec.mjs so these cases run in the existing
// production Wasm/Skia acceptance job rather than a mocked DOM-only fixture.
test('native forms gate submission while retaining editable state and computed errors', async ({ page, request, context }) => {
  test.setTimeout(240000);
  await openStudio(page); const mcp = await connectMcp(page, request); let app;
  try {
    const catalog = await mcp.call('xamlg_ui_catalog', {});
    expect(catalog.composites.map(component => component.name)).toEqual(expect.arrayContaining([
      'ui:Form', 'ui:Field', 'ui:SubmitButton', 'ui:ValidationSummary'
    ]));
    const marker = await mcp.call('xamlg_ui_native_present', {
      id: 'native-validated-form', expectedRevision: 0, sequence: 1,
      initialState: { name: '', submitted: false }, data: {},
      xaml: `<ui:Form xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui">
        <ui:SubmitButton ui:Key="submit" ui:Action="submit" Width="300" Height="40" HorizontalAlignment="Left" Content="Save"/>
        <ui:Field Label="Name" IsRequired="True" ErrorText="Name is required.">
          <TextBox ui:Key="name" ui:Bind="name" MaxLength="80"/>
        </ui:Field>
        <ui:ValidationSummary/>
        <TextBlock Text="{ui:Expr state.submitted ? &quot;Saved&quot; : &quot;Draft&quot;}"/>
      </ui:Form>`,
      actions: [{ id: 'submit', kind: 'state', arguments: { submitted: true } }]
    });
    app = await mountNativeUi(context, mcp, marker);
    await expect(app.guest.locator('details')).toContainText('Name is required.');
    await clickNativeTopButton(app.guest);
    let snapshot = await mcp.call('xamlg_ui_read', { id: marker.id });
    expect(snapshot.state.submitted).toBe(false);
    expect(snapshot.stateRevision).toBe(0);
    snapshot = await mcp.call('xamlg_ui_state', {
      id: marker.id, expectedRevision: snapshot.revision,
      expectedStateRevision: snapshot.stateRevision, key: 'name', value: 'Browser project'
    });
    await app.host.evaluate(id => window.updateNative(id), marker.id);
    await expect(app.guest.getByRole('status')).toContainText('state ' + snapshot.stateRevision);
    await expect(app.guest.locator('details')).not.toContainText('Name is required.');
    await clickNativeTopButton(app.guest);
    await expect(app.guest.locator('details')).toContainText('Saved');
    await expect(app.guest.getByRole('status')).toContainText('state 2');
    snapshot = await mcp.call('xamlg_ui_read', { id: marker.id });
    expect(snapshot.state).toEqual({ name: 'Browser project', submitted: true });
    const calls = await app.host.evaluate(() => window.nativeRequests.filter(call => call.name !== 'xamlg_ui_read'));
    expect(calls.map(call => call.name)).toEqual(['xamlg_ui_state_action']);
    await expect(app.guest.getByRole('region', { name: 'Review UI action', exact: true })).toHaveCount(0);
    await expect(app.guest.getByRole('alert')).toHaveCount(0);
    expect(app.errors).toEqual([]);
  } finally { if (app) await app.host.close(); await mcp.close(); }
});

test('native repeated actions retain item identity after data reordering', async ({ page, request, context }) => {
  test.setTimeout(240000);
  await openStudio(page); const mcp = await connectMcp(page, request); let app;
  try {
    const rows = [{ id: 'a', label: 'Alpha' }, { id: 'b', label: 'Beta' }];
    const marker = await mcp.call('xamlg_ui_native_present', {
      id: 'native-contextual-actions', expectedRevision: 0, sequence: 1,
      initialState: { selected: '' }, data: { rows },
      xaml: `<StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" Spacing="8">
        <Button ui:Key="select" ui:Each="{ui:Expr data.rows}" ui:ItemKey="{ui:Expr item.id}" ui:Action="select"
                Width="300" Height="40" HorizontalAlignment="Left" Content="{ui:Expr item.label}"/>
        <TextBlock Text="{ui:Expr &quot;Selected: &quot; + state.selected}"/>
      </StackPanel>`,
      actions: [{ id: 'select', kind: 'state', arguments: { selected: '{ui:Expr item.id}' } }]
    });
    app = await mountNativeUi(context, mcp, marker);
    await clickNativeTopButton(app.guest);
    await expect(app.guest.locator('details')).toContainText('Selected: a');
    let snapshot = await mcp.call('xamlg_ui_read', { id: marker.id });
    const dataResult = await mcp.call('xamlg_ui_data', { id: marker.id, expectedRevision: snapshot.revision, data: { rows: [...rows].reverse() } });
    await app.host.evaluate(id => window.updateNative(id), marker.id);
    await expect(app.guest.getByRole('status')).toContainText('revision ' + dataResult.revision);
    await clickNativeTopButton(app.guest);
    await expect(app.guest.locator('details')).toContainText('Selected: b');
    snapshot = await mcp.call('xamlg_ui_read', { id: marker.id });
    expect(snapshot.state.selected).toBe('b'); expect(snapshot.stateRevision).toBe(2);
    const calls = await app.host.evaluate(() => window.nativeRequests.filter(call => call.name !== 'xamlg_ui_read'));
    expect(calls.map(call => call.name)).toEqual(['xamlg_ui_state_action', 'xamlg_ui_state_action']);
    expect(calls[0].arguments.nodeKey).not.toBe(calls[1].arguments.nodeKey);
    await expect(app.guest.getByRole('alert')).toHaveCount(0); expect(app.errors).toEqual([]);
  } finally { if (app) await app.host.close(); await mcp.close(); }
});
