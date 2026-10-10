import test from 'node:test';
import assert from 'node:assert/strict';
import { createWorkbenchInterop, mountTool, mountDocument } from '../../tools/XamlG.Playground/wwwroot/git/workbench.mjs';
test('Razor components receive independent JS interop facades', () => {
    const first = createWorkbenchInterop(), second = createWorkbenchInterop();
    assert.notEqual(first, second); assert.equal(first.mountTool, mountTool); assert.equal(second.mountDocument, mountDocument);
    delete first.mountDocument; assert.equal(second.mountDocument, mountDocument);
});
