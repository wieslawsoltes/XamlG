import { test, expect } from './studio-fixture.mjs';
import { openStudio, setSourceAndRun, writeDocument, connectMcp, call } from './live-preview.mjs';

const xaml = `<StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Playground.ComputerView" Spacing="8" Margin="16">
  <TextBox x:Name="first" Width="230" HorizontalAlignment="Left" />
  <TextBox x:Name="second" Width="230" HorizontalAlignment="Left" />
  <Button x:Name="submit" Content="Submit" Click="Submit" />
  <TextBlock x:Name="result" Text="Waiting" />
  <ComboBox x:Name="choices" Width="230" HorizontalAlignment="Left"><ComboBoxItem Content="Alpha" /><ComboBoxItem Content="Beta" /></ComboBox>
  <Slider x:Name="slider" Width="230" HorizontalAlignment="Left" Minimum="0" Maximum="100" />
  <ScrollViewer x:Name="scroll" Height="70"><StackPanel><TextBlock Text="Top" /><Border Height="240" /><TextBlock Text="Bottom" /></StackPanel></ScrollViewer>
  <Button x:Name="startClock" Content="Start clock" Click="StartClock" />
  <TextBlock x:Name="clock" Text="Clock stopped" />
</StackPanel>`;
const code = `using System; using Avalonia.Controls; using Avalonia.Interactivity; using Avalonia.Threading; namespace Playground;
public partial class ComputerView : StackPanel {
  public ComputerView() => InitializeComponent();
  private void Submit(object? sender, RoutedEventArgs args) => result.Text = first.Text + " / " + second.Text;
  private void StartClock(object? sender, RoutedEventArgs args) {
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; var ticks = 0;
    timer.Tick += (_, _) => { clock.Text = "Clock tick " + ++ticks; if (ticks == 100) timer.Stop(); }; timer.Start();
  }
}`;

for (const isolated of [false, true]) test(`MCP computer workflow returns real images and operates the ${isolated ? 'isolated' : 'trusted'} app`, async ({ page, request }) => {
  test.setTimeout(150000);
  await openStudio(page);
  await writeDocument((name, args) => call(page, name, args), 'Code.cs', code);
  await setSourceAndRun(page, xaml);
  if (isolated) {
    await page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
    await page.getByTestId('run-isolated').click();
    await expect(page.locator('.statusbar')).toContainText('Isolated preview running');
  }
  const mcp = await connectMcp(page, request);
  try {
    await mcp.call('xamlg_computer_viewport', { width: 600, height: 480 });
    const observed = await mcp.rpc('tools/call', { name: 'xamlg_computer_observe', arguments: { maximumWidth: 300, maximumHeight: 240 } });
    expect(observed.isError, JSON.stringify(observed.structuredContent)).not.toBe(true);
    const image = observed.content.find(part => part.type === 'image');
    expect(image.mimeType).toBe('image/png');
    const png = Buffer.from(image.data, 'base64');
    expect(png.length).toBeGreaterThan(1000);
    await test.info().attach('mcp-observation.png', { body: png, contentType: 'image/png' });
    expect([...png.subarray(0, 8)]).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
    expect(png.readUInt32BE(16)).toBe(300); expect(png.readUInt32BE(20)).toBe(240);
    expect(JSON.stringify(observed.structuredContent)).not.toContain(image.data);
    let frame = observed.structuredContent.observation;
    expect(frame.width).toBe(600); expect(frame.height).toBe(480);
    const first = frame.elements.find(item => item.name === 'first');
    const actions = await mcp.call('xamlg_computer_actions', { frameId: frame.frameId, expectedRevision: frame.revision, actions: [
      { kind: 'click', x: (first.x + first.width / 2) * frame.imageScale, y: (first.y + first.height / 2) * frame.imageScale },
      { kind: 'text', text: 'One' }, { kind: 'key', key: 'Tab' }, { kind: 'text', text: 'Two' },
      { kind: 'click', target: { name: 'submit' } }, { kind: 'assert', target: { name: 'result' }, text: 'One / Two' }
    ] });
    expect(actions.error).toBeNull(); expect(actions.completed).toHaveLength(6);
    const old = await mcp.rpc('tools/call', { name: 'xamlg_computer_actions', arguments: { frameId: frame.frameId, expectedRevision: frame.revision, actions: [{ kind: 'click', target: { name: 'submit' } }] } });
    expect(old.isError).toBe(true);
    frame = actions.observation;
    const popup = await mcp.call('xamlg_computer_actions', { frameId: frame.frameId, expectedRevision: frame.revision, actions: [
      { kind: 'click', target: { name: 'choices' } }, { kind: 'wait', milliseconds: 60 }
    ] });
    expect(popup.error).toBeNull();
    expect(popup.observation.elements.some(item => item.text === 'Beta' && item.visible)).toBe(true);
    frame = popup.observation;
    const pick = await mcp.call('xamlg_computer_actions', { frameId: frame.frameId, expectedRevision: frame.revision, actions: [
      { kind: 'key', key: 'Down' }, { kind: 'key', key: 'Enter' }, { kind: 'scroll', target: { name: 'scroll' }, deltaY: -3 }, { kind: 'reset' }
    ] });
    expect(pick.error).toBeNull(); expect(pick.completed).toHaveLength(4);
    frame = pick.observation;
    const partial = await mcp.call('xamlg_computer_actions', { frameId: frame.frameId, expectedRevision: frame.revision, screenshot: false, actions: [
      { kind: 'focus', target: { name: 'first' } }, { kind: 'assert', target: { name: 'result' }, text: 'incorrect' }, { kind: 'text', text: 'MUST NOT RUN' }
    ] });
    expect(partial.failedIndex).toBe(1); expect(partial.completed).toHaveLength(1);
    expect(partial.observation.elements.find(item => item.name === 'first').text).toBe('One');

    frame = (await mcp.call('xamlg_computer_observe', { screenshot: false, count: 200 })).observation;
    const ticking = await mcp.call('xamlg_computer_actions', { frameId: frame.frameId, expectedRevision: frame.revision, screenshot: false,
      actions: [{ kind: 'click', target: { name: 'startClock' } }] });
    expect(ticking.error).toBeNull();
    frame = ticking.observation;
    // Give the app's real dispatcher clock multiple ticks between observation and input.
    await page.waitForTimeout(500);
    const expired = await mcp.rpc('tools/call', { name: 'xamlg_computer_actions', arguments: {
      frameId: frame.frameId, expectedRevision: frame.revision, screenshot: false,
      actions: [{ kind: 'click', target: { name: 'submit' } }]
    } });
    expect(expired.isError).toBe(true);
    const refreshed = await mcp.call('xamlg_computer_actions', {
      frameId: frame.frameId, expectedRevision: frame.revision, screenshot: false, refreshTargets: true, actions: [
        { kind: 'key', target: { name: 'first' }, key: 'End' },
        { kind: 'text', target: { name: 'first' }, text: ' live' },
        { kind: 'click', target: { name: 'submit' } },
        { kind: 'assert', target: { name: 'result' }, text: 'One live / Two' }
      ]
    });
    expect(refreshed.error).toBeNull(); expect(refreshed.completed).toHaveLength(4);
    const changedTarget = await mcp.rpc('tools/call', { name: 'xamlg_computer_actions', arguments: {
      frameId: frame.frameId, expectedRevision: frame.revision, screenshot: false, refreshTargets: true,
      actions: [{ kind: 'text', target: { name: 'first' }, text: 'MUST NOT RUN' }]
    } });
    expect(changedTarget.isError).toBe(true);
    const current = (await mcp.call('xamlg_computer_observe', { screenshot: false, count: 200 })).observation;
    expect(current.elements.find(item => item.name === 'first').text).toBe('One live');
    expect(current.elements.find(item => item.name === 'clock').text).toMatch(/^Clock tick [1-9]/);
  } finally { await mcp.close(); }
});
