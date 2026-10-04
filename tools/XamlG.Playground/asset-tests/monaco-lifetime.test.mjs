import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { Delayer } from '../node_modules/monaco-editor/esm/vs/base/common/async.js';
import { errorHandler, onUnexpectedError } from '../node_modules/monaco-editor/esm/vs/base/common/errors.js';
import { highlighterSchedules, patchMonacoLifetime } from '../build/monaco-lifetime-patch.mjs';

const original = await readFile(new URL('../node_modules/monaco-editor/min/vs/editor/editor.main.js', import.meta.url), 'utf8');
const patched = patchMonacoLifetime(original);

function schedule(expression, delay, callback) {
  // Execute the exact expression inserted in the shipped AMD bundle against the
  // pinned upstream Delayer/error handler, not mocks of cancellation semantics.
  const guarded = expression + '.catch(y.onUnexpectedError)';
  assert.equal(patched.split(guarded).length - 1, 1);
  const run = new Function('y', 'Y', 'return ' + guarded + ';');
  return run.call({ runDelayer: delay, _onPositionChanged: callback, _run: callback }, { onUnexpectedError }, {});
}

test('asset preparation only accepts the exact reviewed bundle and changes two call sites', () => {
  assert.equal(patched.length - original.length, 2 * '.catch(y.onUnexpectedError)'.length);
  assert.throws(() => patchMonacoLifetime(original + '\n'), /identity changed/);
  assert.throws(() => patchMonacoLifetime(patched), /identity changed/);
  assert.throws(() => patchMonacoLifetime(null), TypeError);
});

test('both real WordHighlighter schedules observe cancellation before editor retirement', async () => {
  for (const expression of highlighterSchedules) {
    const delay = new Delayer(60_000);
    let calls = 0;
    const completion = schedule(expression, delay, () => calls++);
    assert.equal(delay.isTriggered(), true);
    delay.dispose();
    assert.equal(await completion, undefined);
    assert.equal(calls, 0);
  }
});

test('normal highlight work is retained and unexpected failures remain visible', async () => {
  const before = errorHandler.unexpectedErrorHandler;
  const reported = [];
  errorHandler.unexpectedErrorHandler = error => reported.push(error);
  try {
    for (const expression of highlighterSchedules) {
      const delay = new Delayer(0);
      let calls = 0;
      await schedule(expression, delay, () => calls++);
      assert.equal(calls, 1);
      const failure = new Error('Unexpected highlight failure');
      await schedule(expression, delay, () => { throw failure; });
      assert.equal(reported.at(-1), failure);
      delay.dispose();
    }
    assert.equal(reported.length, 2);
  } finally {
    errorHandler.unexpectedErrorHandler = before;
  }
});
