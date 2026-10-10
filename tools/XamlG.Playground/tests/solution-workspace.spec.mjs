import { test, expect } from './studio-fixture.mjs';
import { openStudio } from './live-preview.mjs';

const savedWorkspace = page => page.evaluate(async () => (await window.xamlgBoot.importModule('solution-workspace.js')).loadWorkspace());
const api = (page, action, args = {}) => page.evaluate(async ({ action, args }) =>
  (await window.xamlgBoot.importModule('solution-workspace.js')).nativeRequest(action, args), { action, args });
const sourceText = (page, path) => page.evaluate(path => monaco.editor.getModels().find(model => model.uri.path.endsWith('/' + path))?.getValue(), path);

async function explorer(page) {
  await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
  await page.getByRole('button', { name: 'Open solution / folder…', exact: true }).click();
  await page.keyboard.press('Escape');
  const pane = page.getByRole('region', { name: 'Solution Explorer', exact: true });
  await expect(pane.getByRole('button', { name: 'New solution', exact: true })).toBeEnabled();
  return pane;
}
async function create(page, pane, format = 'slnx', add = false) {
  await pane.getByRole('button', { name: add ? 'Add project' : 'New solution', exact: true }).click();
  const wizard = page.getByRole('dialog', { name: add ? 'Add a new project' : 'Create a solution', exact: true });
  await wizard.locator('.ws-template').filter({ hasText: add ? 'Class Library' : 'Console App' }).click();
  await wizard.getByLabel('New workspace name').fill(add ? 'Library' : 'WorkspaceDemo');
  if (!add) await wizard.getByRole('combobox', { name: 'Solution format', exact: true }).selectOption(format);
  await wizard.getByTestId('create-workspace').click();
  await expect(wizard).toHaveCount(0);
  // Model, persisted entry and the live docked select must agree, even when the
  // new project's path sorts before the currently open solution.
  await expect.poll(async () => (await savedWorkspace(page)).entryPath).toBe(`WorkspaceDemo.${format}`);
  await expect(pane).toHaveAttribute('data-entry-path', `WorkspaceDemo.${format}`);
  await expect(pane.getByLabel('Workspace solution or project')).toHaveValue(`WorkspaceDemo.${format}`);
  await expect(pane.getByRole('button', { name: 'Add project', exact: true })).toBeEnabled();
  await expect(pane.locator('[title="Startup project"]')).toHaveCount(0);
}

for (const format of ['sln', 'slnx']) {
  test(`browser ${format} wizard adds projects, edits references, and restores document buffers`, async ({ page }) => {
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await openStudio(page, false);
    const pane = await explorer(page);
    await create(page, pane, format);
    const program = 'WorkspaceDemo/Program.cs';
    const text = '// Workspace Unicode round trip: Żółć 😀\r\nSystem.Console.WriteLine("Saved workspace");\r\n';
    await expect.poll(() => sourceText(page, program)).toContain('Console');
    await page.evaluate(({ program, text }) => monaco.editor.getModels().find(model => model.uri.path.endsWith('/' + program)).setValue(text), { program, text });
    const document = page.locator(`[data-workspace-document="${program}"]`);
    await expect(document.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
    await document.getByRole('button', { name: 'Save', exact: true }).click();
    await expect.poll(async () => (await savedWorkspace(page)).files.find(file => file.path === program)?.content).toBe(text);
    await create(page, pane, format, true);
    await expect.poll(() => sourceText(page, `WorkspaceDemo.${format}`)).toContain('Library.csproj');
    await expect(pane.locator('.ws-tree-row[title="Library/Library.csproj"]')).toBeVisible();
    await pane.locator('summary', { hasText: 'Files and project properties' }).click();
    const selectedProject = pane.getByRole('combobox', { name: 'Selected project', exact: true });
    await selectedProject.selectOption('WorkspaceDemo/WorkspaceDemo.csproj');
    await pane.getByRole('combobox', { name: 'Project reference', exact: true }).selectOption('Library/Library.csproj');
    await pane.getByRole('button', { name: 'Add project reference', exact: true }).click();
    const projectText = async () => (await savedWorkspace(page)).files.find(file => file.path === 'WorkspaceDemo/WorkspaceDemo.csproj').content;
    await expect.poll(projectText).toContain('ProjectReference');
    await expect(selectedProject).toHaveValue('WorkspaceDemo/WorkspaceDemo.csproj');
    await pane.getByRole('button', { name: 'Remove project reference', exact: true }).click();
    await expect.poll(projectText).not.toContain('ProjectReference');
    await pane.getByLabel('Package ID', { exact: true }).fill('Example.Library');
    await pane.getByLabel('Package version', { exact: true }).fill('1.2.3');
    await pane.getByRole('button', { name: 'Add / update package reference', exact: true }).click();
    await expect.poll(projectText).toContain('Example.Library');
    await pane.getByRole('button', { name: 'Remove package reference', exact: true }).click();
    await expect.poll(projectText).not.toContain('Example.Library');
    expect((await savedWorkspace(page)).openDocuments).toContain(program);
    await page.reload();
    await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
    expect((await savedWorkspace(page)).entryPath).toBe(`WorkspaceDemo.${format}`);
    expect((await savedWorkspace(page)).files.some(file => file.path === 'Library/Library.csproj')).toBe(true);
    await expect.poll(() => sourceText(page, program)).toBe(text);
    expect(errors).toEqual([]);
  });
}

test('rejected IndexedDB writes do not publish entry or startup selections and can be retried', async ({ page }) => {
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await openStudio(page, false);
  const pane = await explorer(page);
  await create(page, pane); await create(page, pane, 'slnx', true);
  const app = pane.locator('.ws-tree-row[title="WorkspaceDemo/WorkspaceDemo.csproj"]');
  await app.click();
  await pane.getByRole('button', { name: 'Set startup', exact: true }).click();
  await expect.poll(async () => (await savedWorkspace(page)).startupProject).toBe('WorkspaceDemo/WorkspaceDemo.csproj');
  await expect(pane.locator('[title="Startup project"]')).toHaveCount(1);
  await page.evaluate(() => {
    window.workspaceOriginalPut = IDBObjectStore.prototype.put;
    IDBObjectStore.prototype.put = function (...args) {
      if (this.transaction.db.name === 'xamlg.solution-workspace.v1') throw new DOMException('Workspace test quota failure', 'QuotaExceededError');
      return window.workspaceOriginalPut.apply(this, args);
    };
  });
  try {
    await pane.locator('.ws-tree-row[title="Library/Library.csproj"]').click();
    await pane.getByRole('button', { name: 'Set startup', exact: true }).click();
    await expect(pane.getByRole('alert')).toContainText('quota failure');
    await expect(app.locator('[title="Startup project"]')).toHaveCount(1);
    await pane.getByLabel('Workspace solution or project').selectOption('Library/Library.csproj');
    await expect(pane.getByLabel('Workspace solution or project')).toHaveValue('WorkspaceDemo.slnx');
    await expect(pane).toHaveAttribute('data-entry-path', 'WorkspaceDemo.slnx');
    expect((await savedWorkspace(page)).startupProject).toBe('WorkspaceDemo/WorkspaceDemo.csproj');
    expect((await savedWorkspace(page)).entryPath).toBe('WorkspaceDemo.slnx');
  } finally {
    await page.evaluate(() => { IDBObjectStore.prototype.put = window.workspaceOriginalPut; delete window.workspaceOriginalPut; });
  }
  await pane.getByLabel('Workspace solution or project').selectOption('Library/Library.csproj');
  await expect.poll(async () => (await savedWorkspace(page)).entryPath).toBe('Library/Library.csproj');
  await expect(pane).toHaveAttribute('data-entry-path', 'Library/Library.csproj');
  await expect(pane.locator('[title="Startup project"]')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('real IndexedDB transactions reject stale competing workspace snapshots', async ({ page }) => {
  await openStudio(page, false);
  const result = await page.evaluate(async () => {
    const module = await window.xamlgBoot.importModule('solution-workspace.js');
    const state = { format: 1, identity: 'transaction-contract', entryPath: null, startupProject: null, files: [], openDocuments: [] };
    const revision = (await module.loadWorkspace())?.revision ?? 0;
    const results = await Promise.allSettled([
      module.saveWorkspace({ ...state, identity: 'first' }, revision),
      module.saveWorkspace({ ...state, identity: 'second' }, revision)
    ]);
    return { statuses: results.map(result => result.status), messages: results.filter(result => result.status === 'rejected').map(result => result.reason.message), saved: await module.loadWorkspace(), revision };
  });
  expect(result.statuses.sort()).toEqual(['fulfilled', 'rejected']);
  expect(result.messages[0]).toContain('Another tab changed');
  expect(result.saved.revision).toBe(result.revision + 1);
  expect(['first', 'second']).toContain(result.saved.identity);
});

test('paired owner creates an actual SDK solution and builds and evaluates the selected project', async ({ page }) => {
  test.setTimeout(240000);
  if (!process.env.XAMLG_TEST_OWNER_TOKEN) throw new Error('Run with scripts/test-browser-studio.py.');
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await openStudio(page);
  await page.getByTestId('agent-access').click();
  const access = page.locator('.agent-access-panel');
  await access.getByLabel('Companion WebSocket').fill(process.env.XAMLG_TEST_MCP_URL.replace('http:', 'ws:') + '/bridge');
  await access.getByLabel('Owner token').fill(process.env.XAMLG_TEST_OWNER_TOKEN);
  await access.getByRole('button', { name: 'Connect companion', exact: true }).click();
  await expect(access.getByRole('status')).toContainText('Connected');
  await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
  expect((await api(page, 'workspace_status')).enabled).toBe(true);
  await expect(api(page, 'workspace_create', { name: 'Untrusted', template: 'classlib', trust: false })).rejects.toThrow(/trust/i);
  await expect(api(page, 'workspace_read', { path: '../outside.csproj' })).rejects.toThrow();
  const created = await api(page, 'workspace_create', { name: 'NativeWorkspace', template: 'classlib', createSolution: true, solutionFormat: 'slnx', trust: true });
  expect(created.command.exitCode, created.command.standardError).toBe(0);
  const pane = await explorer(page);
  await pane.locator('summary', { hasText: 'Open workspace' }).click();
  await pane.getByRole('button', { name: 'Open local workspace', exact: true }).click();
  await pane.getByLabel('Workspace solution or project').selectOption(created.entryPath);
  const trust = pane.getByLabel('I trust this workspace', { exact: false });
  await expect(trust).not.toBeChecked();
  await expect(pane.getByRole('button', { name: 'build', exact: true })).toBeDisabled();
  await trust.check();
  await pane.getByRole('combobox', { name: 'Build target', exact: true }).selectOption('project');
  const output = page.getByRole('region', { name: 'Workspace output', exact: true });
  for (const operation of ['restore', 'build']) {
    await pane.getByRole('button', { name: operation, exact: true }).click();
    await expect(pane.getByRole('tree')).toHaveAttribute('aria-busy', 'true');
    await expect(pane.getByRole('tree')).toHaveAttribute('aria-busy', 'false');
    await expect(output.getByRole('log')).toContainText('Exit code: 0');
  }
  await pane.getByRole('button', { name: 'evaluate', exact: true }).click();
  await expect(output.getByRole('heading', { name: 'Evaluated Roslyn solution', exact: true })).toBeVisible();
  const raw = output.locator('details').filter({ has: page.locator('summary', { hasText: /^Raw evaluated snapshot$/ }) });
  await raw.locator('summary').click();
  const graph = JSON.parse(await raw.locator('pre').innerText());
  expect(graph.evaluated).toBe(true);
  expect(graph.entryPath.endsWith('.csproj')).toBe(true);
  expect(graph.projects).toHaveLength(1);
  expect(graph.projects[0].documents.some(document => document.kind === 'Compile' && document.path.endsWith('/Class1.cs'))).toBe(true);
  expect(graph.projects[0].metadataReferences.length).toBeGreaterThan(0);
  expect(graph.compilerDiagnostics.filter(diagnostic => diagnostic.severity === 'Error')).toEqual([]);
  expect(errors).toEqual([]);
});

test('workspace tab close supports cancel, save, discard and persistent tab removal', async ({ page }) => {
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await openStudio(page, false);
  const pane = await explorer(page); await create(page, pane);
  const program = 'WorkspaceDemo/Program.cs';
  const tab = page.getByRole('tab').filter({ hasText: program });
  const document = page.locator(`[data-workspace-document="${program}"]`);
  const persisted = async () => (await savedWorkspace(page)).files.find(file => file.path === program).content;
  await expect.poll(() => sourceText(page, program)).toContain('Console');
  const original = await persisted();
  const savedText = '// saved on close\nSystem.Console.WriteLine("Close test");\n';
  const edit = async text => {
    await page.evaluate(({ program, text }) => monaco.editor.getModels().find(model => model.uri.path.endsWith('/' + program)).setValue(text), { program, text });
    await expect(document.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
  };
  const choices = [];
  page.on('dialog', async dialog => {
    const choice = choices.shift();
    if (choice === 'accept') await dialog.accept();
    else await dialog.dismiss();
  });
  await edit(savedText);
  choices.push('dismiss', 'dismiss');
  await tab.getByRole('button', { name: 'Close tab', exact: true }).click();
  await expect.poll(() => choices.length).toBe(0);
  await expect(document).not.toHaveAttribute('inert', '');
  await expect(tab).toHaveCount(1);
  expect(await persisted()).toBe(original);
  expect(await sourceText(page, program)).toBe(savedText);
  choices.push('accept');
  await tab.getByRole('button', { name: 'Close tab', exact: true }).click();
  await expect(tab).toHaveCount(0);
  await expect.poll(persisted).toBe(savedText);
  expect((await savedWorkspace(page)).openDocuments).not.toContain(program);
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(tab).toHaveCount(0);
  await pane.locator(`.ws-tree-row[title="${program}"]`).dblclick();
  await expect.poll(() => sourceText(page, program)).toBe(savedText);
  await edit('// discard this buffer\n');
  choices.push('dismiss', 'accept');
  await tab.getByRole('button', { name: 'Close tab', exact: true }).click();
  await expect(tab).toHaveCount(0);
  expect(await persisted()).toBe(savedText);
  expect((await savedWorkspace(page)).openDocuments).not.toContain(program);
  expect(errors).toEqual([]);
});

test('failed close persistence keeps the dirty buffer and tab available for recovery', async ({ page }) => {
  await openStudio(page, false);
  const pane = await explorer(page); await create(page, pane);
  const program = 'WorkspaceDemo/Program.cs';
  const tab = page.getByRole('tab').filter({ hasText: program });
  const document = page.locator(`[data-workspace-document="${program}"]`);
  const text = '// keep this buffer when closing cannot persist\n';
  await expect.poll(() => sourceText(page, program)).toContain('Console');
  await page.evaluate(({ program, text }) => monaco.editor.getModels().find(model => model.uri.path.endsWith('/' + program)).setValue(text), { program, text });
  await expect(document.getByRole('button', { name: 'Save', exact: true })).toBeEnabled();
  await page.evaluate(() => {
    window.workspaceOriginalPut = IDBObjectStore.prototype.put;
    IDBObjectStore.prototype.put = function (...args) {
      if (this.transaction.db.name === 'xamlg.solution-workspace.v1') throw new DOMException('Close storage failure', 'QuotaExceededError');
      return window.workspaceOriginalPut.apply(this, args);
    };
  });
  let dialogs = 0;
  page.on('dialog', async dialog => { if (dialogs++ === 0) await dialog.dismiss(); else await dialog.accept(); });
  try {
    await tab.getByRole('button', { name: 'Close tab', exact: true }).click();
    await expect(document.getByRole('alert')).toContainText('Close storage failure');
    await expect(tab).toHaveCount(1);
    await expect(document).not.toHaveAttribute('inert', '');
    expect(await sourceText(page, program)).toBe(text);
    expect((await savedWorkspace(page)).openDocuments).toContain(program);
  } finally {
    await page.evaluate(() => { IDBObjectStore.prototype.put = window.workspaceOriginalPut; delete window.workspaceOriginalPut; });
  }
  await document.getByRole('button', { name: 'Save', exact: true }).click();
  await expect.poll(async () => (await savedWorkspace(page)).files.find(file => file.path === program).content).toBe(text);
});
