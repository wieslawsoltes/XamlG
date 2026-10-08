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
</StackPanel>`;
const code = `using Avalonia.Controls; using Avalonia.Interactivity; namespace Playground;
public partial class ComputerView : StackPanel {
  public ComputerView() => InitializeComponent();
  private void Submit(object? sender, RoutedEventArgs args) => result.Text = first.Text + " / " + second.Text;
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
    expect(observed.isError).not.toBe(true);
    const image = observed.content.find(part => part.type === 'image');
    expect(image.mimeType).toBe('image/png');
    const png = Buffer.from(image.data, 'base64');
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
  } finally { await mcp.close(); }
});
