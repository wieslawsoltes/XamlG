#!/usr/bin/env node
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';

const require = createRequire(new URL('../tools/XamlG.Playground/package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const base = process.argv[2] ?? 'http://127.0.0.1:8943';
const output = path.resolve(process.argv[3] ?? 'artifacts/controlcatalog-validation/browser');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
const messages = [];
const errors = [];
let completed = 0;
page.on('console', message => {
    const text = message.text();
    messages.push(`${message.type()}: ${text}`);
    if (text.includes('XAMLG CATALOG ') && (++completed % 100 === 0 || text.includes(' FAIL ')))
        console.log(text);
});
const startupError = new Promise((_, reject) => page.once('pageerror', error => reject(error)));
page.on('pageerror', error => errors.push(error.stack || error.message || String(error)));
page.setDefaultTimeout(180_000);
try {
    await page.goto(`${base}/?xamlg-validate=1`, { waitUntil: 'domcontentloaded' });
    await Promise.race([page.waitForFunction(() => typeof globalThis.xamlgValidateCatalog === 'function'), startupError]);
    await page.locator('canvas').first().waitFor({ state: 'visible' });
    await page.screenshot({ path: path.join(output, 'startup.png') });
    const results = JSON.parse(await Promise.race([
        page.evaluate(() => globalThis.xamlgValidateCatalog()), startupError
    ]));
    await writeFile(path.join(output, 'results.json'), JSON.stringify(results, null, 2));
    await page.screenshot({ path: path.join(output, 'completed.png') });
    assert.equal(results.length, 1188, 'Every registered page, section and gallery demo must run in all six theme configurations.');
    const groups = new Map();
    for (const result of results) {
        const key = `${result.Theme}/${result.Variant}/${result.Density}`;
        if (!groups.has(key)) groups.set(key, []);
        groups.get(key).push(result);
    }
    assert.equal(groups.size, 6);
    for (const [name, cases] of groups) {
        assert.equal(cases.filter(item => item.Kind === 'page').length, 76, name);
        assert.equal(cases.filter(item => item.Kind === 'section').length, 11, name);
        assert.equal(cases.filter(item => item.Kind === 'sample').length, 9, name);
        assert.equal(cases.filter(item => item.Kind === 'gallery').length, 102, name);
        assert.equal(new Set(cases.map(item => item.Name)).size, 198, name);
    }
    assert.deepEqual(results.filter(result => result.Error != null), [], 'Catalog pages failed in the published browser app.');
    assert.deepEqual(errors, [], 'Browser runtime errors');
    console.log(`PASS: ${results.length} published WebAssembly page/theme runs.`);
} catch (error) {
    await page.screenshot({ path: path.join(output, 'failed.png') }).catch(() => {});
    throw error;
} finally {
    await writeFile(path.join(output, 'console.log'), messages.join('\n'));
    await writeFile(path.join(output, 'errors.json'), JSON.stringify(errors, null, 2));
    await browser.close();
}
