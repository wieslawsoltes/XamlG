import { test, expect } from './studio-fixture.mjs';
import { openStudio, connectMcp } from './live-preview.mjs';
import { mountNativeUi, clickNativeTopButton } from './intelligent-ui-host.mjs';

function topButtonCounter(request, id) {
  return { ...request, id, xaml: `<StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui" Spacing="8">
    <Button ui:Key="increment" ui:Action="increment" Width="300" Height="40" HorizontalAlignment="Left" Content="Increment"/>
    <TextBlock Text="{ui:Expr &quot;Count: &quot; + state.count}"/>
    <TextBlock Text="{ui:Expr &quot;Previous: &quot; + state.previous}"/>
  </StackPanel>` };
}

test('native MCP buttons execute declared local state actions without external review or inference', async ({ page, request, context }) => {
  test.setTimeout(240000); await openStudio(page); const mcp = await connectMcp(page, request); let app;
  try {
    const catalog = await mcp.call('xamlg_ui_catalog', {});
    expect(catalog.composites.map(component => component.name)).toContain('ui:BarChart');
    const marker = await mcp.call('xamlg_ui_native_present', topButtonCounter(catalog.localActions, 'native-counter'));
    app = await mountNativeUi(context, mcp, marker);
    await expect(app.guest.locator('details')).toContainText('Count: 0');
    for (const count of [1, 2]) {
      await clickNativeTopButton(app.guest);
      await expect(app.guest.getByRole('status')).toContainText('state ' + count);
      await expect(app.guest.locator('details')).toContainText('Count: ' + count);
    }
    const snapshot = await mcp.call('xamlg_ui_read', { id: marker.id });
    expect(snapshot.state).toEqual({ count: 2, previous: 1 });
    const calls = await app.host.evaluate(() => window.nativeRequests.filter(call => call.name !== 'xamlg_ui_read'));
    expect(calls.map(call => call.name)).toEqual(['xamlg_ui_state_action', 'xamlg_ui_state_action']);
    expect(calls.map(call => call.arguments.expectedStateRevision)).toEqual([0, 1]);
    await expect(app.guest.getByRole('region', { name: 'Review UI action', exact: true })).toHaveCount(0);
    await expect(app.guest.getByRole('alert')).toHaveCount(0); expect(app.errors).toEqual([]);
  } finally { if (app) await app.host.close(); await mcp.close(); }
});

test('rich dashboard renders real Avalonia, reacts to data state and retires when the host selects another surface', async ({ page, request, context }, info) => {
  test.setTimeout(240000); await openStudio(page); const mcp = await connectMcp(page, request); let app;
  try {
    const catalog = await mcp.call('xamlg_ui_catalog', {});
    expect(catalog.composites).toHaveLength(14);
    const marker = await mcp.call('xamlg_ui_native_present', { ...catalog.dashboard, id: 'native-dashboard' });
    app = await mountNativeUi(context, mcp, marker);
    await expect(app.guest.locator('details')).toContainText('Alpha: 10');
    const initial = await mcp.call('xamlg_ui_read', { id: marker.id });
    const next = await mcp.call('xamlg_ui_state', { id: marker.id, expectedRevision: initial.revision, expectedStateRevision: initial.stateRevision, key: 'scale', value: 2 });
    await app.host.evaluate(id => window.updateNative(id), marker.id);
    await expect(app.guest.getByRole('status')).toContainText('state ' + next.stateRevision);
    await expect(app.guest.locator('details')).toContainText('Alpha: 20');
    await expect(app.guest.locator('details')).toContainText('100');
    await expect(app.guest.getByRole('alert')).toHaveCount(0);
    await app.host.screenshot({ path: info.outputPath('rich-native-dashboard.png'), fullPage: true });
    const replacement = await mcp.call('xamlg_ui_native_present', topButtonCounter(catalog.localActions, 'replacement-counter'));
    await app.host.evaluate(marker => window.publishNative(marker), replacement);
    await expect(app.guest.locator('details')).toContainText('Count: 0');
    await expect(app.guest.locator('details')).not.toContainText('Alpha: 20');
    await clickNativeTopButton(app.guest);
    await expect(app.guest.locator('details')).toContainText('Count: 1');
    expect((await mcp.call('xamlg_ui_read', { id: marker.id })).state.scale).toBe(2);
    expect((await mcp.call('xamlg_ui_read', { id: replacement.id })).state.count).toBe(1);
    expect(app.errors).toEqual([]);
  } finally { if (app) await app.host.close(); await mcp.close(); }
});

test('approved full-C# local actions are displayed in exact-source review and execute only inside the opaque frame', async ({ page, request }) => {
  test.setTimeout(240000); await openStudio(page); const mcp = await connectMcp(page, request);
  try {
    const panel = page.getByRole('complementary', { name: 'Intelligent UI workspace', exact: true });
    await panel.getByRole('button', { name: 'Intelligent UI workspace', exact: true }).click();
    await panel.locator('.ui-full-csharp > summary').click();
    const proposal = await mcp.call('xamlg_ui_csharp_propose', {
      id: 'approved-local-counter', expectedRevision: 0, sequence: 1, initialState: { n: 3 }, data: {},
      xaml: `<StackPanel xmlns="https://github.com/avaloniaui" xmlns:ui="urn:xamlg:intelligent-ui">
        <Button ui:Key="next" ui:Action="next" Width="300" Height="40" HorizontalAlignment="Left" Content="Next"/>
        <TextBlock Text="{ui:Expr &quot;Sum: &quot; + Enumerable.Range(1, checked((int)state.GetProperty(&quot;n&quot;).GetDecimal())).Sum()}"/>
      </StackPanel>`,
      actions: [{ id: 'next', kind: 'state', arguments: { n: '{ui:Expr checked(state.GetProperty("n").GetDecimal() + 1)}' } }]
    });
    expect(proposal.status).toBe('pending');
    await panel.getByRole('button', { name: new RegExp('Review proposal ' + proposal.id.slice(0, 8)) }).click();
    await expect(panel.getByLabel('Full C# local state actions')).toHaveValue(/GetDecimal/);
    await panel.getByRole('button', { name: 'Review full C#', exact: true }).click();
    const review = panel.getByRole('region', { name: 'Review full C# execution' });
    await expect(review).toContainText('Local state actions'); await expect(review).toContainText('GetDecimal');
    await expect(page.locator('iframe[title="Approved full C# preview"]')).toHaveCount(0);
    await panel.getByRole('button', { name: 'Approve and run full C#', exact: true }).click();
    await expect.poll(async () => {
      const status = await mcp.call('xamlg_ui_csharp_status', { id: proposal.id });
      if (status.status === 'failed') throw new Error(status.error);
      return status.status;
    }).toBe('completed');
    const frame = page.frameLocator('iframe[title="Approved full C# preview"]');
    await expect(frame.locator('canvas.avalonia-canvas')).toBeVisible();
    await frame.locator('details > summary').click(); await expect(frame.locator('details')).toContainText('Sum: 6');
    await clickNativeTopButton(frame); await expect(frame.getByRole('status')).toContainText('state 1');
    await expect(frame.locator('details')).toContainText('Sum: 10');
    await expect(frame.getByRole('alert')).toHaveCount(0);
    expect(await frame.locator('body').evaluate(() => {
      let parentBlocked = false, storageBlocked = false;
      try { void parent.document.body; } catch { parentBlocked = true; }
      try { void localStorage.length; } catch { storageBlocked = true; }
      return { parentBlocked, storageBlocked };
    })).toEqual({ parentBlocked: true, storageBlocked: true });
    await panel.getByRole('button', { name: 'Reset execution frame', exact: true }).click();
    await expect(page.locator('iframe[title="Approved full C# preview"]')).toHaveCount(0);
  } finally { await mcp.close(); }
});
