import { test, expect } from './studio-fixture.mjs';
import { openStudio, call, writeDocument, openPreviewDocument } from './live-preview.mjs';

test('agent and inspector inputs changed during an in-flight save retain the latest value', async ({ page }) => {
  await openStudio(page, false);
  await page.getByTestId('agent-workbench').click();
  const agent = page.getByRole('region', { name: 'Coding agent workbench' });
  await agent.getByRole('navigation', { name: 'Agent sections' }).getByRole('button', { name: 'Connection', exact: true }).click();
  const moduleState = key => page.evaluate(async key => (await xamlgBoot.importModule('studio.js')).loadStudioState(key), key);
  for (const panel of ['agent', 'inspector']) {
    const key = panel === 'agent' ? 'agent-ui' : 'runtime-ui:Properties';
    if (panel === 'inspector') {
      await page.getByTestId('run-preview').click();
      await page.locator('[data-tab-id="runtime"]').click();
    }
    const input = panel === 'agent' ? agent.getByLabel('API key', { exact: true }) : page.getByRole('region', { name: 'Avalonia runtime properties' }).getByLabel('Find property');
    const value = state => panel === 'agent' ? state?.connections?.['direct:openai']?.apiKey : state?.propertyFilter;
    await input.fill('synthetic-initial-value');
    await expect.poll(async () => value(await moduleState(key))).toBe('synthetic-initial-value');
    await page.evaluate(key => {
      const encrypt = crypto.subtle.encrypt.bind(crypto.subtle);
      let first = true;
      globalThis.delayedStudioSave = { held: false };
      crypto.subtle.encrypt = async (...args) => {
        if (first && new TextDecoder().decode(args[0].additionalData) === key) {
          first = false; delayedStudioSave.held = true;
          await new Promise(resolve => delayedStudioSave.release = resolve);
        }
        return encrypt(...args);
      };
      delayedStudioSave.restore = () => { crypto.subtle.encrypt = encrypt; };
    }, key);
    try {
      await input.fill('synthetic-first-save');
      await expect.poll(() => page.evaluate(() => delayedStudioSave.held)).toBe(true);
      await input.fill('synthetic-latest-value');
      await page.evaluate(() => delayedStudioSave.release());
      await expect.poll(async () => value(await moduleState(key)), { timeout: 10000 }).toBe('synthetic-latest-value');
    } finally { await page.evaluate(() => { delayedStudioSave.release?.(); delayedStudioSave.restore(); }); }
  }
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  expect((await moduleState('agent-ui')).connections['direct:openai'].apiKey).toBe('synthetic-latest-value');
  expect((await moduleState('runtime-ui:Properties')).propertyFilter).toBe('synthetic-latest-value');
});

test('companion credentials are remembered before pairing and Forget removes retained versions', async ({ page }) => {
  await openStudio(page, false);
  await page.getByTestId('agent-access').click();
  const address = 'ws://127.0.0.1:4899/bridge', token = 'synthetic-unconnected-owner-token'.repeat(2);
  await page.getByLabel('Companion WebSocket', { exact: true }).fill(address);
  await page.getByLabel('Owner token', { exact: true }).fill(token);
  await expect.poll(() => page.evaluate(async () => (await (await xamlgBoot.importModule('studio.js')).loadStudioState('companion-connection'))?.token)).toBe(token);
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await page.getByTestId('agent-access').click();
  await expect(page.getByLabel('Companion WebSocket', { exact: true })).toHaveValue(address);
  await expect(page.getByLabel('Owner token', { exact: true })).toHaveValue(token);
  await expect(page.locator('.agent-access-panel').getByRole('status')).toContainText('Disconnected');
  await page.getByRole('button', { name: 'Forget connection', exact: true }).click();
  await expect(page.getByLabel('Owner token', { exact: true })).toHaveValue('');
  expect(await page.evaluate(async () => {
    const module = await xamlgBoot.importModule('studio.js');
    return [await module.loadStudioState('companion-connection'), await module.loadStudioState('companion-connection:previous')];
  })).not.toContainEqual(expect.objectContaining({ token }));
});

test('source documents, document tabs and undo history survive browser restart', async ({ page }) => {
  await openStudio(page);
  const invoke = (name, args) => call(page, name, args);
  await writeDocument(invoke, 'Saved.cs', 'namespace Playground; public class Saved { public const int Value = 1; }');
  await openPreviewDocument(page, 'Saved.cs');
  await expect(page.locator('.dock-source[data-document-path="Saved.cs"]')).toBeVisible();
  await writeDocument(invoke, 'Saved.cs', 'namespace Playground; public class Saved { public const int Value = 2; }');
  await expect.poll(() => page.evaluate(async () => (await (await xamlgBoot.importModule('studio.js')).loadStudioState('project'))?.codeFiles?.['Saved.cs'])).toContain('Value = 2');
  await page.reload();
  await expect(page.locator('.studio')).toHaveAttribute('data-ready', 'true');
  await expect(page.locator('.statusbar')).toContainText('Compilation succeeded');
  await expect(page.locator('.dock-source[data-document-path="Saved.cs"]')).toBeVisible();
  await page.getByTestId('agent-access').click();
  await expect(page.getByLabel('Enable access to this live project')).not.toBeChecked();
  await page.getByLabel('Enable access to this live project').check();
  await page.getByLabel('Permission profile').selectOption('FullAccess');
  let state = await invoke('xamlg_project_get');
  expect(state.canUndo).toBe(true);
  await invoke('xamlg_project_undo', { expectedRevision: state.revision });
  expect((await invoke('xamlg_document_read', { path: 'Saved.cs' })).text).toContain('Value = 1');
  state = await invoke('xamlg_project_get'); expect(state.canRedo).toBe(true);
  await invoke('xamlg_project_redo', { expectedRevision: state.revision });
  expect((await invoke('xamlg_document_read', { path: 'Saved.cs' })).text).toContain('Value = 2');
});

test('private storage compresses, recovers damaged writes, reports quota and prevents stale-tab overwrites', async ({ page }) => {
  await openStudio(page, false);
  const result = await page.evaluate(async () => {
    const a = await xamlgBoot.importModule('studio-storage.js');
    const b = await import(new URL('studio-storage.js?second-tab', document.baseURI).href);
    const key = 'storage-contract-test', secret = 'synthetic-credential-only';
    await a.loadStudioState(key); await b.loadStudioState(key);
    await a.saveStudioState(key, { text: secret.repeat(1000), version: 1 });
    let conflict = false;
    try { await b.saveStudioState(key, { version: 9 }); } catch (error) { conflict = error.message.includes('Another Studio tab'); }
    await a.saveStudioState(key, { text: secret.repeat(1000), version: 2 });
    const db = await new Promise((resolve, reject) => { const r = indexedDB.open('xamlg.studio.state.v1'); r.onsuccess = () => resolve(r.result); r.onerror = () => reject(r.error); });
    const original = await new Promise(resolve => { const r = db.transaction('records').objectStore('records').get(key); r.onsuccess = () => resolve(r.result); });
    await new Promise((resolve, reject) => {
      const tx = db.transaction('records', 'readwrite'); tx.oncomplete = resolve; tx.onerror = () => reject(tx.error);
      tx.objectStore('records').put({ ...original, data: new Uint8Array([1, 2, 3]) }, key);
    });
    const fresh = await import(new URL('studio-storage.js?restart', document.baseURI).href);
    const recovered = await fresh.loadStudioState(key);
    const recoveryError = (await fresh.studioStorageStatus()).error;
    await fresh.forgetStudioState(key);
    const put = IDBObjectStore.prototype.put;
    IDBObjectStore.prototype.put = function(value, name) { if (name === key) throw new DOMException('Full', 'QuotaExceededError'); return put.call(this, value, name); };
    let quota = false;
    try { await fresh.saveStudioState(key, { version: 3 }); } catch (error) { quota = error.message.includes('storage is full'); }
    finally { IDBObjectStore.prototype.put = put; }
    const quotaError = (await fresh.studioStorageStatus()).error;
    await fresh.forgetStudioState(key);
    const previous = await new Promise(resolve => { const r = db.transaction('records').objectStore('records').get(key + ':previous'); r.onsuccess = () => resolve(r.result); });
    db.close();
    return { conflict, recoveredVersion: recovered.version, recoveryError, quota, quotaError, compression: original.compression,
      compressedBytes: original.data.byteLength, previousRemoved: previous === undefined };
  });
  expect(result.conflict).toBe(true); expect(result.recoveredVersion).toBe(1); expect(result.recoveryError).toContain('recovered');
  expect(result.quota).toBe(true); expect(result.quotaError).toContain('storage is full');
  expect(result.compression).toBe('gzip'); expect(result.compressedBytes).toBeLessThan(1000); expect(result.previousRemoved).toBe(true);
});
