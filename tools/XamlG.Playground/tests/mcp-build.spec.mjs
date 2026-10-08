import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { test, expect } from './studio-fixture.mjs';
import { call, openStudio, writeDocument } from './live-preview.mjs';
import { connectModernMcp } from './mcp-fixture.mjs';
import { agentRequest } from './agent-fixture.mjs';

test.setTimeout(120000);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const resource = async (mcp, uri) => JSON.parse((await mcp.rpc('resources/read', { uri })).contents[0].text);
async function bytesFor(mcp, artifact) {
  const chunks = []; let offset = 0;
  do {
    const part = await mcp.call('xamlg_build_read', { id: artifact.id, offset, count: 131072 });
    expect(part.artifact).toEqual(artifact); expect(part.offset).toBe(offset);
    const bytes = Buffer.from(part.base64, 'base64'); expect(bytes.length).toBe(part.count);
    expect(part.count).toBeLessThanOrEqual(131072); chunks.push(bytes); offset += bytes.length;
    expect(part.hasMore).toBe(offset < artifact.length);
  } while (offset < artifact.length);
  const bytes = Buffer.concat(chunks);
  expect(bytes.length).toBe(artifact.length); expect(hash(bytes)).toBe(artifact.sha256);
  return bytes;
}
const waitTask = (mcp, args) => mcp.rpc('tools/call', { name: 'xamlg_wait', arguments: args });
async function terminal(mcp, id, status = 'completed') {
  let result;
  await expect.poll(async () => (result = await mcp.rpc('tasks/get', { taskId: id })).status).toBe(status);
  return result;
}
async function details(dialog, title) {
  const node = dialog.locator('details').filter({ has: dialog.page().locator('summary', { hasText: title }) }).first();
  if (await node.getAttribute('open') === null) await node.locator('summary').first().click();
  return node;
}

test('MCP creates immutable JSON ZIP assembly and PDB artifacts with owned chunks and local downloads', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectModernMcp(page, request);
  try {
    const targets = await mcp.call('xamlg_build_targets');
    expect(targets.targets).toEqual(['project-json', 'source-zip', 'assembly', 'symbols']);
    const original = await mcp.call('xamlg_document_read', { path: 'Code.cs' });
    const code = original.text + '\r\n/* PRIVATE_SOURCE_SENTINEL ' + 'chunk-boundary '.repeat(22000) + '*/';
    await writeDocument(mcp.call, 'Code.cs', code);
    const resourcePath = 'Themes/雪 & data.axaml';
    const resourceText = '<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Color x:Key="Accent">#224466</Color></ResourceDictionary>';
    await writeDocument(mcp.call, resourcePath, resourceText);
    const project = await mcp.call('xamlg_project_get');
    const xaml = await mcp.call('xamlg_document_read', { path: 'View.axaml' });
    const artifacts = {};
    for (const target of targets.targets) {
      const artifact = await mcp.call('xamlg_build_create', { target, expectedRevision: project.revision });
      expect(artifact.revision).toBe(project.revision);
      expect(Date.parse(artifact.expiresAt) - Date.parse(artifact.createdAt)).toBe(300000);
      artifacts[target] = { artifact, bytes: await bytesFor(mcp, artifact) };
    }
    expect(artifacts['project-json'].bytes.length).toBeGreaterThan(262144);
    const saved = JSON.parse(artifacts['project-json'].bytes.toString('utf8'));
    expect(saved).toMatchObject({ format: 'xamlg-project', version: 4, code, xaml: xaml.text, resources: { [resourcePath]: resourceText } });
    expect(saved.compilerOptions).toBeTruthy();
    const archive = JSON.parse(execFileSync('python3', ['-c',
      'import io,json,sys,zipfile; z=zipfile.ZipFile(io.BytesIO(sys.stdin.buffer.read())); print(json.dumps({n:z.read(n).decode("utf-8") for n in z.namelist()}))'], { input: artifacts['source-zip'].bytes }).toString());
    expect(archive['source/Code.cs']).toBe(code); expect(archive['source/View.axaml']).toBe(xaml.text);
    expect(archive['source/' + resourcePath]).toBe(resourceText);
    expect(JSON.parse(archive['source/CompilerSettings.json'])).toBeTruthy();
    expect(Object.keys(archive).some(path => path.startsWith('generated/') && path.endsWith('.cs'))).toBe(true);
    expect(JSON.parse(archive['diagnostics.json'])).toBeInstanceOf(Array);
    expect(artifacts.assembly.bytes.subarray(0, 2).toString()).toBe('MZ');
    expect(artifacts.assembly.bytes.includes(Buffer.from('BSJB'))).toBe(true);
    expect(artifacts.symbols.bytes.subarray(0, 4).toString()).toBe('BSJB');
    await writeDocument(mcp.call, 'Code.cs', original.text + '\n// changed after export');
    expect(await bytesFor(mcp, artifacts['project-json'].artifact)).toEqual(artifacts['project-json'].bytes);
    await expect(mcp.call('xamlg_build_create', { target: 'assembly', expectedRevision: project.revision })).rejects.toThrow();
    const id = artifacts['project-json'].artifact.id;
    const first = await resource(mcp, 'xamlg://artifacts/' + id);
    expect(first.count).toBe(262144); expect(first.hasMore).toBe(true);
    expect((await mcp.call('xamlg_build_read', { id, count: 32 }, { clientName: 'studio-owner' })).count).toBe(32);
    await expect(call(page, 'xamlg_build_read', { id })).rejects.toThrow();
    await expect(mcp.call('xamlg_build_read', { id, count: 262145 })).rejects.toThrow();
    const ownerArtifact = await call(page, 'xamlg_build_create', { target: 'project-json', expectedRevision: (await call(page, 'xamlg_project_get')).revision });
    await expect(mcp.call('xamlg_build_read', { id: ownerArtifact.id })).rejects.toThrow();
    await expect(mcp.call('xamlg_build_release', { id: ownerArtifact.id })).rejects.toThrow();
    await expect(resource(mcp, 'xamlg://artifacts/' + ownerArtifact.id)).rejects.toThrow();
    await page.getByTestId('agent-access').click();
    const access = page.locator('.agent-access-panel');
    const inventory = await details(access, 'Build artifacts');
    const row = inventory.locator('div').filter({ has: page.locator('strong', { hasText: 'xamlg-source.zip' }) });
    const downloading = page.waitForEvent('download'); await row.getByRole('button', { name: 'Download', exact: true }).click();
    const download = await downloading; expect(download.suggestedFilename()).toBe('xamlg-source.zip');
    expect(await readFile(await download.path())).toEqual(artifacts['source-zip'].bytes);
    await row.getByRole('button', { name: 'Release', exact: true }).click();
    await expect(mcp.call('xamlg_build_read', { id: artifacts['source-zip'].artifact.id })).rejects.toThrow();
    const catalog = await details(access, 'Tool catalog');
    await catalog.getByLabel('Filter capabilities').fill('build_'); await catalog.getByRole('combobox', { name: 'Scope', exact: true }).selectOption('Build');
    await expect(catalog.locator('code')).toHaveText(['xamlg_build_targets', 'xamlg_build_create', 'xamlg_build_read', 'xamlg_build_release']);
    const activity = await details(access, 'Activity');
    await activity.getByLabel('Filter activity').fill('xamlg_build_read'); await activity.getByLabel('Errors only').check();
    await expect(activity.locator('p').first()).toContainText('failed');
    await activity.getByLabel('Follow new activity').uncheck();
    const frozen = await activity.locator('p').allTextContents();
    await expect(mcp.call('xamlg_build_read', { id: 'unknown-handle' })).rejects.toThrow();
    expect(await activity.locator('p').allTextContents()).toEqual(frozen);
    const exporting = page.waitForEvent('download'); await activity.getByRole('button', { name: 'Export filtered activity' }).click();
    const exported = await readFile(await (await exporting).path(), 'utf8');
    const entries = JSON.parse(exported); expect(entries.length).toBeGreaterThan(0);
    expect(entries.every(entry => entry.Name === 'xamlg_build_read' && entry.Status === 'failed')).toBe(true);
    for (const secret of ['PRIVATE_SOURCE_SENTINEL', code, process.env.XAMLG_TEST_OWNER_TOKEN, process.env.XAMLG_TEST_MCP_TOKEN]) expect(exported).not.toContain(secret);
    expect(Object.keys(entries[0]).sort()).toEqual(['Caller', 'ElapsedMilliseconds', 'ErrorCode', 'Method', 'Name', 'Sequence', 'Status', 'Time']);
    await activity.getByRole('button', { name: 'Clear activity' }).click(); await expect(activity.locator('p')).toHaveCount(0);
    await access.getByRole('button', { name: 'Release all artifacts' }).click();
    await expect(inventory.locator('div')).toHaveCount(0);
    await expect(mcp.call('xamlg_build_read', { id })).rejects.toThrow();
    await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
  } finally { await mcp.close(); }
});

test('MCP waits observe source updates without blocking edits and owner cancellation retires operations', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectModernMcp(page, request);
  try {
    const project = await mcp.call('xamlg_project_get');
    const wait = await waitTask(mcp, { milliseconds: 60000, afterRevision: project.revision, resources: ['xamlg://source/View.axaml'] });
    expect(wait.resultType).toBe('task'); expect(wait.status).toBe('working');
    const stream = await mcp.subscribe({ taskIds: [wait.taskId], resourceSubscriptions: ['xamlg://source/View.axaml'] });
    expect(stream.ack.taskIds).toEqual([wait.taskId]);
    const xaml = '<StackPanel xmlns="https://github.com/avaloniaui"><TextBlock Name="observed" Text="Changed through MCP"/></StackPanel>';
    const edited = await mcp.call('xamlg_document_write', { path: 'View.axaml', text: xaml, expectedRevision: project.revision });
    const update = await stream.until(message => message.method === 'notifications/resources/updated');
    expect(Object.keys(update.params).sort()).toEqual(['_meta', 'uri']); expect(update.params.uri).toBe('xamlg://source/View.axaml');
    const completed = await stream.until(message => message.method === 'notifications/tasks/status' && message.params.status === 'completed');
    expect(completed.params.result.structuredContent).toMatchObject({ reason: 'changed', changedResource: 'xamlg://source/View.axaml', revision: edited.revision });
    expect((await terminal(mcp, wait.taskId)).result.isError).not.toBe(true);
    expect((await resource(mcp, 'xamlg://source/View.axaml')).text).toBe(xaml);
    const completion = await mcp.rpc('completion/complete', { ref: { type: 'ref/resource', uri: 'xamlg://source/{path}' }, argument: { name: 'path', value: 'View' } });
    expect(completion.completion.values).toEqual(['View.axaml']);
    await stream.close();
    expect((await mcp.legacy.call('xamlg_wait', { milliseconds: 10, resources: [] })).reason).toBe('timeout');
    const timed = await waitTask(mcp, { milliseconds: 20, resources: [] });
    expect((await terminal(mcp, timed.taskId)).result.structuredContent.reason).toBe('timeout');
    const cancelled = await waitTask(mcp, { milliseconds: 60000, resources: [] });
    await mcp.rpc('tasks/cancel', { taskId: cancelled.taskId }); await terminal(mcp, cancelled.taskId, 'cancelled');
    const ownerCancel = await waitTask(mcp, { milliseconds: 60000, resources: [] });
    await page.getByTestId('agent-workbench').click();
    const pane = page.getByRole('region', { name: 'Coding agent workbench' });
    await pane.getByRole('button', { name: 'Refresh', exact: true }).click();
    const operations = pane.locator('details.agent-operations'); await operations.locator('summary').click();
    const operation = operations.locator('.agent-operation').filter({ hasText: ownerCancel.taskId });
    await expect(operation).toContainText('Working'); await operation.getByRole('button', { name: 'Cancel operation' }).click();
    await expect(operation).toContainText('Cancelled'); await terminal(mcp, ownerCancel.taskId, 'cancelled');
    await operations.getByRole('button', { name: 'Clear finished operations' }).click();
    await expect(operations.locator('.agent-operation')).toHaveCount(0);
    await expect(mcp.rpc('tasks/get', { taskId: ownerCancel.taskId })).rejects.toThrow();
    const obsolete = await waitTask(mcp, { milliseconds: 60000, resources: [] });
    const artifact = await mcp.call('xamlg_build_create', { target: 'project-json', expectedRevision: (await mcp.call('xamlg_project_get')).revision });
    await page.locator('.studio-menu > summary').filter({ hasText: /^Project$/ }).click();
  await page.getByLabel('Example', { exact: true }).selectOption('1');
  await page.keyboard.press('Escape');
    await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
    await expect.poll(async () => (await mcp.rpc('tools/list')).tools.length).toBe(0);
    await expect(mcp.rpc('tasks/get', { taskId: obsolete.taskId })).rejects.toThrow();
    await page.getByTestId('agent-access').click();
    const access = page.locator('.agent-access-panel');
    await expect(access.getByLabel('Enable access to this live project')).not.toBeChecked();
    await expect(access.locator('summary', { hasText: 'Build artifacts' })).toHaveText('Build artifacts (0)');
    await access.getByLabel('Enable access to this live project').check();
    await access.getByLabel('Owner token').fill(process.env.XAMLG_TEST_OWNER_TOKEN);
    await access.getByRole('button', { name: 'Connect companion', exact: true }).click();
    await expect(access.getByRole('status')).toContainText('Connected');
    await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
    await expect(mcp.call('xamlg_build_read', { id: artifact.id })).rejects.toThrow();
    await expect(mcp.rpc('tasks/get', { taskId: obsolete.taskId })).rejects.toThrow();
    expect((await agentRequest(page, 'state')).operations).toEqual([]);
  } finally { await mcp.close(); }
});

test('MCP resources follow generated and runtime changes while permissions and revocation protect the workspace', async ({ page, request }) => {
  await openStudio(page);
  const mcp = await connectModernMcp(page, request);
  try {
    await writeDocument(mcp.call, 'View.axaml', '<TextBlock xmlns="https://github.com/avaloniaui" Name="observed" Text="Before resource update"/>');
    const generated = await mcp.call('xamlg_generated_list');
    const generatedUri = 'xamlg://generated/' + encodeURIComponent(generated.files[0].path);
    const stream = await mcp.subscribe({ resourceSubscriptions: [generatedUri, 'xamlg://runtime'] });
    await writeDocument(mcp.call, 'View.axaml', '<TextBlock xmlns="https://github.com/avaloniaui" Name="observed" Text="After resource update"/>');
    await mcp.call('xamlg_compiler_compile');
    const currentGenerated = await mcp.call('xamlg_generated_list');
    await test.info().attach('generated-resource-paths', { body: JSON.stringify({ before: generated, after: currentGenerated }), contentType: 'application/json' });
    await stream.until(message => message.method === 'notifications/resources/updated' && message.params.uri === generatedUri);
    if (!currentGenerated.files.some(file => file.path === generated.files[0].path)) await expect(resource(mcp, generatedUri)).rejects.toThrow();
    expect((await resource(mcp, 'xamlg://generated/' + encodeURIComponent(currentGenerated.files[0].path))).text).toContain('After resource update');
    const runtime = await mcp.call('xamlg_runtime_run', { expectedRevision: (await mcp.call('xamlg_project_get')).revision });
    await stream.until(message => message.method === 'notifications/resources/updated' && message.params.uri === 'xamlg://runtime');
    const node = runtime.nodes.find(node => node.name === 'observed'); expect(node).toBeTruthy();
    const propertiesUri = `xamlg://runtime/${encodeURIComponent(node.id)}/properties`;
    const properties = await resource(mcp, propertiesUri);
    const text = properties.properties.find(property => property.name === 'Text');
    const runtimeUpdates = await mcp.subscribe({ resourceSubscriptions: [propertiesUri] });
    await mcp.call('xamlg_runtime_property_set', { objectId: node.id, property: text.key, value: 'Live resource value', expectedRevision: (await mcp.call('xamlg_runtime_tree')).revision });
    await runtimeUpdates.until(message => message.method === 'notifications/resources/updated');
    expect((await resource(mcp, propertiesUri)).properties.find(property => property.name === 'Text').value.value).toBe('Live resource value');
    await stream.close(); await runtimeUpdates.close();
    const project = await mcp.call('xamlg_project_get');
    await page.getByTestId('agent-access').click();
    const access = page.locator('.agent-access-panel');
    await access.getByLabel('Permission profile').selectOption('ReadOnly');
    await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
    const mutation = { path: 'View.axaml', text: '<Border xmlns="https://github.com/avaloniaui"/>', expectedRevision: project.revision };
    await expect(mcp.call('xamlg_document_write', mutation)).rejects.toThrow();
    await expect(mcp.call('xamlg_runtime_run', { expectedRevision: project.revision })).rejects.toThrow();
    const artifact = await mcp.call('xamlg_build_create', { target: 'assembly', expectedRevision: project.revision });
    expect((await bytesFor(mcp, artifact)).subarray(0, 2).toString()).toBe('MZ');
    expect((await resource(mcp, 'xamlg://source/View.axaml')).text).toContain('After resource update');
    const complete = () => mcp.rpc('completion/complete', { ref: { type: 'ref/resource', uri: 'xamlg://source/{path}' }, argument: { name: 'path', value: 'View' } });
    expect((await complete()).completion.values).toEqual(['View.axaml']);
    await page.getByTestId('agent-access').click();
    await access.getByLabel('Permission profile').selectOption('Ask');
    await page.locator('.ad-anchorable-pane[aria-label="Agent access"] > .ad-pane-title').getByRole('button', { name: 'Hide tool window', exact: true }).click();
    const denied = mcp.call('xamlg_document_write', mutation).then(value => ({ value }), error => ({ error: error.message }));
    const review = page.getByRole('dialog', { name: 'Review agent operation' });
    await expect(review).toContainText('xamlg_document_write'); await review.getByRole('button', { name: 'Deny', exact: true }).click();
    expect((await denied).error).toBeTruthy();
    expect((await resource(mcp, 'xamlg://source/View.axaml')).text).toContain('After resource update');
    const waiting = await waitTask(mcp, { milliseconds: 60000, resources: [] });
    await page.getByTestId('agent-access').click();
    await access.getByRole('button', { name: 'Revoke & disconnect' }).click();
    await expect(access.locator('summary', { hasText: 'Build artifacts' })).toHaveText('Build artifacts (0)');
    await expect(mcp.rpc('tasks/get', { taskId: waiting.taskId })).rejects.toThrow();
    await expect(mcp.call('xamlg_build_read', { id: artifact.id })).rejects.toThrow();
    await expect(resource(mcp, 'xamlg://source/View.axaml')).rejects.toThrow();
    expect((await complete()).completion.values ?? []).toEqual([]);
    await expect(page.evaluate(() => window.xamlgAutomation.resource('xamlg://source/View.axaml'))).rejects.toThrow();
  } finally { await mcp.close(); }
});
