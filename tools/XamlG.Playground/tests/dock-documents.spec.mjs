import { test, expect } from './studio-fixture.mjs';
import { openStudio, call, writeDocument } from './live-preview.mjs';

const tab = (page, path) => page.locator(`[data-tab-id="document:${encodeURIComponent(path)}"]`);
const editor = (page, path) => page.locator(`.source-pane[data-document-path="${path}"] .code-editor`);
const menu = (page, title) => page.locator('.studio-menu > summary').filter({ hasText: new RegExp(`^${title}$`) }).click();

test('native document tabs retain immediate edits through close, reopen, floating and layout restore', async ({ page }) => {
  await openStudio(page);
  const invoke = (name, args) => call(page, name, args);
  await writeDocument(invoke, 'Models/One.cs', 'public class One { public int Value => 1; }');
  await writeDocument(invoke, 'Models/Two.cs', 'public class Two { public int Value => 2; }');
  for (const path of ['Models/One.cs', 'Models/Two.cs']) {
    await page.locator('.explorer').getByRole('button').filter({ has: page.locator('.file-path', { hasText: path }) }).click();
    await expect(editor(page, path)).toBeVisible();
  }
  await tab(page, 'Models/One.cs').click();
  const changed = 'public class One { public int Value => 987; }';
  await page.evaluate(text => {
    monaco.editor.getModels().find(model => model.uri.path.endsWith('/Models/One.cs')).setValue(text);
    document.querySelector('[data-tab-id="document:Models%2FOne.cs"] button[aria-label="Close tab"]').click();
  }, changed);
  await expect(tab(page, 'Models/One.cs')).toHaveCount(0);
  expect((await invoke('xamlg_document_read', { path: 'Models/One.cs' })).text).toBe(changed);
  await page.locator('.explorer').getByRole('button').filter({ has: page.locator('.file-path', { hasText: 'Models/One.cs' }) }).click();
  await expect(editor(page, 'Models/One.cs')).toBeVisible();
  const layout = await invoke('xamlg_layout_get');
  expect(layout.layout).not.toContain(changed);
  await invoke('xamlg_layout_content', { contentId: 'document:Models%2FOne.cs', operation: 'floatInPage' });
  await expect(editor(page, 'Models/One.cs')).toBeVisible();
  expect((await invoke('xamlg_document_read', { path: 'Models/One.cs' })).text).toBe(changed);
  await invoke('xamlg_layout_set', { layout: layout.layout });
  await expect(editor(page, 'Models/One.cs')).toBeVisible();
  expect((await invoke('xamlg_compiler_compile')).success).toBe(true);
  await invoke('xamlg_document_remove', { path: 'Models/One.cs', expectedRevision: (await invoke('xamlg_project_get')).revision });
  await expect(tab(page, 'Models/One.cs')).toHaveCount(0);
  await invoke('xamlg_project_undo', { expectedRevision: (await invoke('xamlg_project_get')).revision });
  await page.locator('.explorer').getByRole('button').filter({ has: page.locator('.file-path', { hasText: 'Models/One.cs' }) }).click();
  await expect(editor(page, 'Models/One.cs')).toBeVisible();
  expect((await invoke('xamlg_document_read', { path: 'Models/One.cs' })).text).toBe(changed);
});

test('shell menus, generated documents and individual runtime tool panels work at narrow widths', async ({ page }) => {
  await openStudio(page, false);
  await menu(page, 'Project'); await expect(page.getByLabel('Example', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape'); await expect(page.getByLabel('Example', { exact: true })).not.toBeVisible();
  await menu(page, 'View'); await page.getByRole('button', { name: 'Generated C#', exact: true }).click();
  const generated = page.locator('.generated-files button').first();
  const path = (await generated.textContent()).replace(/^#\s*/, '').trim(); await generated.click();
  await expect(page.locator(`[data-tab-id="generated:${encodeURIComponent(path)}"]`)).toHaveAttribute('aria-selected', 'true');
  expect(await page.evaluate(path => monaco.editor.getEditors().find(editor => editor.getModel()?.uri.path.endsWith('/' + path))?.getOption(monaco.editor.EditorOption.readOnly), path)).toBe(true);
  await menu(page, 'Edit');
  await expect(page.getByRole('button', { name: 'Format document', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape');
  for (const title of ['Runtime objects', 'Bindings', 'Styles', 'Runtime resources', 'Events', 'Input', 'Accessibility', 'Runtime tools']) {
    await menu(page, 'Tools'); await page.locator('.studio-menu-items').getByRole('button', { name: title, exact: true }).click();
    await expect(page.getByRole('region', { name: /^Avalonia runtime / }).filter({ visible: true })).toHaveCount(1);
  }
  await page.setViewportSize({ width: 740, height: 820 });
  await page.getByTestId('agent-workbench').click();
  const agent = page.getByRole('region', { name: 'Coding agent workbench' });
  await agent.getByRole('navigation').getByRole('button', { name: 'Tools', exact: true }).click();
  await expect(agent.getByLabel('Search tools', { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(740);
  await page.getByRole('button', { name: 'Toggle color theme', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});
