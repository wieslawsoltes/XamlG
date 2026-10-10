import { test, expect } from './studio-fixture.mjs';
import { openStudio, call } from './live-preview.mjs';

const tool = (page, kind) => page.locator(`.git-tool[aria-label="Git ${kind}"]`).filter({ visible: true });
const documentView = page => page.locator('.git-document').filter({ visible: true });
const idle = async page => {
  await expect(page.locator('.git-tool[aria-busy="true"]')).toHaveCount(0);
  await expect(page.locator('.git-error:visible')).toHaveCount(0);
};
async function showTool(page, name, kind) {
  await page.locator('.studio-menu > summary').filter({ hasText: /^Tools$/ }).click();
  await page.locator('.studio-menu-items').getByRole('button', { name, exact: true }).click();
  await expect(tool(page, kind)).toBeVisible();
  return tool(page, kind);
}
async function setGitText(page, value) {
  const view = documentView(page);
  await expect(view.locator('.monaco-editor')).toBeVisible();
  await view.evaluate((host, value) => {
    const editor = monaco.editor.getEditors().find(editor => host.contains(editor.getDomNode()));
    if (!editor) throw new Error('The Git document has no live Monaco editor.');
    editor.getModel().setValue(value);
  }, value);
}
async function gitText(page) {
  return documentView(page).evaluate(host => {
    const editor = monaco.editor.getEditors().find(editor => host.contains(editor.getDomNode()));
    return editor?.getModel()?.getValue();
  });
}
async function persisted(page, path, layer = 'worktree') {
  return page.evaluate(async ({ path, layer }) => {
    const { WorkspaceStore } = await import(new URL('git/storage.mjs', document.baseURI).href);
    const store = new WorkspaceStore();
    try {
      const state = (await store.list())[0], entry = state?.[layer]?.get(path);
      const bytes = entry && await store.blob(entry.oid);
      return bytes ? new TextDecoder('utf-8', { ignoreBOM: true }).decode(bytes) : null;
    } finally { await store.close(); }
  }, { path, layer });
}
async function createRepository(page, name = 'Git acceptance') {
  await page.getByTestId('git-workbench').click();
  const repositories = tool(page, 'repositories');
  await expect(repositories.getByLabel('Workspace name', { exact: true })).toBeVisible();
  await repositories.getByLabel('Workspace name', { exact: true }).fill(name);
  await repositories.getByRole('button', { name: 'New virtual', exact: true }).click();
  await expect(repositories.getByRole('status')).toHaveText(name);
  await idle(page);
  return repositories;
}
async function createFile(page, repositories, path) {
  await repositories.getByLabel('New file path', { exact: true }).fill(path);
  await repositories.getByRole('button', { name: 'Create file', exact: true }).click();
  await expect(documentView(page).locator('.git-path')).toHaveText(path);
  await expect(documentView(page).locator('.monaco-editor')).toBeVisible();
  await idle(page);
}

// Exercise the real published Blazor host, startup allowlist, Dockyard Razor
// templates, Monaco models and IndexedDB. No substitute document host is installed.
test('docked Git tools and Monaco documents preserve index, worktree, commits and unsaved buffers', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await openStudio(page);
  let repositories = await createRepository(page);
  await createFile(page, repositories, 'notes.txt');
  await setGitText(page, 'staged\n');
  await documentView(page).getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => persisted(page, 'notes.txt')).toBe('staged\n');
  await documentView(page).getByRole('button', { name: 'Stage saved file', exact: true }).click();
  await expect.poll(() => persisted(page, 'notes.txt', 'index')).toBe('staged\n');
  await setGitText(page, 'working\n');
  await documentView(page).getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => persisted(page, 'notes.txt')).toBe('working\n');
  const changes = await showTool(page, 'Git changes', 'changes');
  await changes.getByLabel('Author name', { exact: true }).fill('Studio Test');
  await changes.getByLabel('Author email', { exact: true }).fill('studio@example.test');
  await changes.getByLabel('Commit message', { exact: true }).fill('Commit staged snapshot');
  await changes.getByRole('button', { name: 'Commit staged', exact: true }).click();
  await expect.poll(() => persisted(page, 'notes.txt', 'head')).toBe('staged\n');
  expect(await persisted(page, 'notes.txt')).toBe('working\n');
  await idle(page);

  await setGitText(page, 'unsaved\n');
  const id = await documentView(page).getAttribute('data-git-document');
  await page.locator(`[data-tab-id="${id}"]`).getByRole('button', { name: 'Close tab', exact: true }).click();
  await expect(page.locator(`[data-tab-id="${id}"]`)).toHaveCount(0);
  repositories = await showTool(page, 'Git repositories', 'repositories');
  await repositories.getByRole('button', { name: 'notes.txt', exact: true }).click();
  await expect.poll(() => gitText(page)).toBe('unsaved\n');
  expect(await persisted(page, 'notes.txt')).toBe('working\n');
  await documentView(page).getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => persisted(page, 'notes.txt')).toBe('unsaved\n');

  const layout = await call(page, 'xamlg_layout_get');
  await call(page, 'xamlg_layout_content', { contentId: id, operation: 'floatInPage' });
  await expect.poll(() => gitText(page)).toBe('unsaved\n');
  await call(page, 'xamlg_layout_set', { layout: layout.layout });
  await expect.poll(() => gitText(page)).toBe('unsaved\n');
  await showTool(page, 'Git branches and stashes', 'branches');
  const history = await showTool(page, 'Git history', 'history');
  await history.getByRole('button', { name: /Commit staged snapshot/ }).click();
  await expect(documentView(page).locator('pre')).toContainText('Commit staged snapshot');
  await showTool(page, 'GitHub', 'github');
  await expect(tool(page, 'github').getByLabel('GitHub personal access token', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);

  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByTestId('git-workbench').click();
  await expect(tool(page, 'repositories').getByRole('button', { name: 'notes.txt', exact: true })).toBeVisible();
  expect(await persisted(page, 'notes.txt', 'head')).toBe('staged\n');
  expect(await persisted(page, 'notes.txt')).toBe('unsaved\n');
  await idle(page);
});

test('real Monaco diff documents stage only selected hunks', async ({ page }) => {
  await openStudio(page, false);
  const repositories = await createRepository(page);
  await createFile(page, repositories, 'hunks.txt');
  const before = Array.from({ length: 25 }, (_, i) => `line ${i}\n`).join('');
  const after = before.replace('line 1\n', 'first\n').replace('line 23\n', 'last\n');
  await setGitText(page, before);
  await documentView(page).getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => persisted(page, 'hunks.txt')).toBe(before);
  await documentView(page).getByRole('button', { name: 'Stage saved file', exact: true }).click();
  await expect.poll(() => persisted(page, 'hunks.txt', 'index')).toBe(before);
  await setGitText(page, after);
  await documentView(page).getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(() => persisted(page, 'hunks.txt')).toBe(after);
  await documentView(page).getByRole('button', { name: 'Diff', exact: true }).click();
  await expect(documentView(page).locator('.monaco-diff-editor')).toBeVisible();
  await expect(documentView(page).getByRole('checkbox')).toHaveCount(2);
  await documentView(page).getByLabel('Stage hunk 1', { exact: true }).check();
  await documentView(page).getByRole('button', { name: 'Stage selected hunks', exact: true }).click();
  await expect.poll(() => persisted(page, 'hunks.txt', 'index')).toBe(before.replace('line 1\n', 'first\n'));
  expect(await persisted(page, 'hunks.txt')).toBe(after);
  await idle(page);
});

test('Git source import uses the real Studio host and leaves automatic execution disabled', async ({ page }) => {
  await openStudio(page);
  const repositories = await createRepository(page);
  await createFile(page, repositories, 'Imported.axaml');
  const source = '<TextBlock xmlns="https://github.com/avaloniaui" Text="Imported without execution" />';
  await setGitText(page, source);
  page.once('dialog', dialog => dialog.accept('Imported.axaml'));
  await documentView(page).getByRole('button', { name: 'Copy into Studio', exact: true }).click();
  await expect(page.locator('[data-tab-id="document:Imported.axaml"]')).toBeVisible();
  expect((await call(page, 'xamlg_document_read', { path: 'Imported.axaml' })).text).toBe(source);
  const updates = await page.evaluate(() => JSON.parse(localStorage.getItem('xamlg.live-updates')));
  expect(updates).toEqual({ compile: false, preview: false });
  expect(await persisted(page, 'Imported.axaml')).toBe('');
  await idle(page);
});
