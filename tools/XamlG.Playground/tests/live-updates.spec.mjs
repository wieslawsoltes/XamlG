import { test, expect as baseExpect } from './studio-fixture.mjs';
const expect = baseExpect.configure({ timeout: 20000 });
import { openStudio, call, writeDocument, openPreviewDocument } from './live-preview.mjs';

test.use({ liveUpdates: true });
const ns = 'xmlns="https://github.com/avaloniaui"';
const view = text => `<TextBlock ${ns} Name="liveText" Text="${text}"/>`;
const edit = async (page, path, text) => { await page.evaluate(({ path, text }) => {
  const model = monaco.editor.getModels().find(model => model.uri.path.endsWith('/' + path));
  if (!model) throw new Error('Open the source document before editing: ' + path);
  model.setValue(text);
}, { path, text }); await expect(page.locator('.statusbar')).toContainText(/Source changed|C# source changed|Compiling/); };
const runMenu = page => page.locator('.studio-menu > summary').filter({ hasText: /^Run$/ }).click();
async function textInPreview(page) {
  const tree = await call(page, 'xamlg_runtime_tree');
  const node = tree.nodes.find(node => node.name === 'liveText');
  return (await call(page, 'xamlg_runtime_object_read', { objectId: node.id, path: ['Text'] })).value.value;
}
async function updated(page) {
  await expect(page.locator('.statusbar')).toContainText('preview updated automatically');
  await expect(page.getByTestId('run-preview')).toBeEnabled();
}

test('automatic compile and preview default on, debounce source edits and retain the last valid preview', async ({ page }) => {
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await openStudio(page);
  await updated(page);
  await page.locator('[data-tab-id="preview"]').click();
  await expect(page.locator('#avalonia-preview canvas').first()).toBeVisible();
  await runMenu(page);
  await expect(page.getByLabel('Auto compile', { exact: true })).toBeChecked();
  await expect(page.getByLabel('Auto preview', { exact: true })).toBeChecked();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Coding agent', exact: true }).click();
  const agentTab = page.locator('[data-tab-id="agent"]');
  await expect(agentTab).toHaveAttribute('aria-selected', 'true');
  await page.evaluate(text => {
    const model = monaco.editor.getModels().find(model => model.getLanguageId() === 'xml');
    const editor = monaco.editor.getEditors().find(editor => editor.getModel() === model);
    editor.focus();
    for (let i = 0; i < 12; i++) model.setValue(text.replace('Final edit', 'Interim ' + i));
    model.setValue(text);
  }, view('Final edit'));
  await expect(page.locator('.statusbar')).toContainText('Source changed');
  await updated(page);
  expect(await textInPreview(page)).toBe('Final edit');
  await expect(agentTab).toHaveAttribute('aria-selected', 'true');
  expect(await page.evaluate(() => monaco.editor.getEditors().some(editor => editor.hasTextFocus()))).toBe(true);
  await edit(page, 'View.axaml', `<TextBlock ${ns} UnknownMember="invalid" />`);
  await expect(page.locator('.statusbar')).toContainText('Compilation has errors');
  expect(await textInPreview(page)).toBe('Final edit');
  await edit(page, 'View.axaml', view('Recovered edit'));
  await updated(page);
  expect(await textInPreview(page)).toBe('Recovered edit');
  expect(errors).toEqual([]);
});

test('automatic settings persist, compilation can run without preview, and external edits cannot schedule execution', async ({ page }) => {
  await openStudio(page); await updated(page);
  await edit(page, 'View.axaml', view('Visible baseline')); await updated(page);
  await runMenu(page); await page.getByLabel('Auto preview', { exact: true }).uncheck(); await page.keyboard.press('Escape');
  await edit(page, 'View.axaml', view('Compiled only'));
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  expect(await textInPreview(page)).toBe('Visible baseline');
  await runMenu(page); await page.getByLabel('Auto compile', { exact: true }).uncheck(); await page.keyboard.press('Escape');
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await runMenu(page);
  await expect(page.getByLabel('Auto compile', { exact: true })).not.toBeChecked();
  await expect(page.getByLabel('Auto preview', { exact: true })).not.toBeChecked();
  await page.getByLabel('Auto compile', { exact: true }).check();
  await page.getByLabel('Auto preview', { exact: true }).check();
  await page.keyboard.press('Escape'); await updated(page);
  await page.getByTestId('agent-access').click();
  await page.getByLabel('Enable access to this live project').check();
  await page.getByLabel('Permission profile', { exact: true }).selectOption('FullAccess');
  await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
  await edit(page, 'View.axaml', view('Owner baseline')); await updated(page);
  await edit(page, 'View.axaml', view('Pending owner edit'));
  await writeDocument((name, args) => call(page, name, args), 'View.axaml', view('External source edit'));
  // Wait beyond the debounce window: the queued owner revision must not execute
  // source replaced by an external operation, even with automatic preview on.
  await page.waitForTimeout(1400);
  expect(await textInPreview(page)).toBe('Owner baseline');
  await page.getByTestId('run-preview').click();
  await expect(page.locator('.statusbar')).toContainText('Preview running');
  expect(await textInPreview(page)).toBe('External source edit');
});

test('automatic C# edits report errors and refresh in the selected isolated preview mode', async ({ page }) => {
  await openStudio(page, false); await updated(page);
  await runMenu(page); await page.getByTestId('run-isolated').click();
  await expect(page.locator('.statusbar')).toContainText('Isolated preview running');
  await openPreviewDocument(page, 'Code.cs');
  await edit(page, 'Code.cs', 'public class Broken {');
  await expect(page.locator('.statusbar')).toContainText('Compilation has errors');
  const iframe = page.locator('iframe[title="XamlG isolated preview"]');
  await expect(iframe).toBeVisible();
  await page.getByRole('button', { name: 'Coding agent', exact: true }).click();
  await edit(page, 'Code.cs', 'public class Repaired { public const int Value = 42; }');
  await expect(page.locator('.statusbar')).toContainText('isolated preview updated automatically');
  await expect(page.locator('[data-tab-id="agent"]')).toHaveAttribute('aria-selected', 'true');
  await page.locator('[data-tab-id="preview"]').click();
  await expect(iframe).toBeVisible();
  await expect(iframe).toHaveAttribute('sandbox', 'allow-scripts');
  await expect(page.locator('#avalonia-preview')).not.toBeVisible();
});
