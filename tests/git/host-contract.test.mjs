import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, access, readdir } from 'node:fs/promises';

const root = new URL('../../', import.meta.url);
const read = path => readFile(new URL(path, root), 'utf8');

test('every Git Razor module import is registered with the bounded startup loader', async () => {
    const startup = await read('tools/XamlG.Playground/wwwroot/startup.js');
    const registry = /const moduleNames = new Set\(\[([^\]]+)\]\)/.exec(startup);
    assert.ok(registry, 'The startup module registry must remain explicit.');
    const allowed = new Set([...registry[1].matchAll(/'([^']+)'/g)].map(m => m[1]));
    let calls = 0;
    for (const path of ['App.Git.cs', 'Components/GitTool.razor', 'Components/GitDocument.razor']) {
        const source = await read('tools/XamlG.Playground/' + path);
        for (const [, module] of source.matchAll(/"xamlgBoot\.importModule",\s*"([^"]+)"/g)) {
            assert.ok(allowed.has(module), `${path} imports unregistered module ${module}`);
            await access(new URL('tools/XamlG.Playground/wwwroot/' + module, root));
            calls++;
        }
    }
    assert.equal(calls, 3);
    assert.ok(!allowed.has('https://example.test/remote.mjs'));
    assert.ok(!allowed.has('../external.mjs'));
});

test('all relative Git module dependencies are present in the checkout', async () => {
    const directory = new URL('tools/XamlG.Playground/wwwroot/git/', root);
    for (const name of await readdir(directory)) {
        if (!name.endsWith('.mjs')) continue;
        const url = new URL(name, directory), source = await readFile(url, 'utf8');
        for (const [, dependency] of source.matchAll(/(?:from\s*|import\s*\()\s*['"](\.[^'"]+)['"]/g)) {
            await access(new URL(dependency, url));
        }
    }
});

test('Git validation is part of the repository workflow inventory', async () => {
    const inventory = JSON.parse(await read('eng/validation-workflows.json'));
    assert.ok(inventory.pullRequest.includes('git-workspaces.yml'));
    const workflow = await read('.github/workflows/git-workspaces.yml');
    assert.match(workflow, /node --test tests\/git\/\*\.test\.mjs/);
    assert.match(workflow, /git-workspaces\.spec\.mjs/);
    assert.match(workflow, /dotnet publish/);
});
