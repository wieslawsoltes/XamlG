import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const moduleSource = await readFile(new URL('../wwwroot/source-buffer.js', import.meta.url));
const { SourceBuffer } = await import('data:text/javascript;base64,' + moduleSource.toString('base64'));

test('source offsets map rendered Unicode positions across mixed newlines and a hidden BOM', () => {
  const source = new SourceBuffer('\uFEFFa😀\r\nb\nc\rd', true);
  for (const [offset, lineNumber, column] of [[1, 1, 1], [2, 1, 2], [4, 1, 4], [6, 2, 1], [7, 2, 2], [8, 3, 1], [10, 4, 1], [11, 4, 2]]) {
    assert.deepEqual(source.positionAt(offset), { lineNumber, column });
    assert.equal(source.offsetAt({ lineNumber, column }), offset);
  }
  assert.equal(source.displayColumn(1, 3), 2);
  assert.equal(source.displayColumn(2, 3), 3);
  assert.equal(source.sourceColumn(1, 2), 3);
  assert.equal(source.sourceColumn(2, 3), 3);
});

test('multiple Monaco edits preserve all untouched line endings and use the original ranges', () => {
  const source = new SourceBuffer('head 😀\r\nfirst\nseparator\r\nlast');
  source.applyChanges([
    { range: { startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 6 }, text: 'FIRST changed' },
    { range: { startLineNumber: 4, startColumn: 1, endLineNumber: 4, endColumn: 5 }, text: 'LAST' }
  ]);
  assert.equal(source.text, 'head 😀\r\nFIRST changed\nseparator\r\nLAST');
  assert.equal(source.offsetAt({ lineNumber: 4, column: 1 }), source.text.indexOf('LAST'));
});

test('inserting and removing lines changes only the requested newline span', () => {
  const source = new SourceBuffer('a\r\nb\nc');
  source.applyChanges([{ range: { startLineNumber: 2, startColumn: 2, endLineNumber: 2, endColumn: 2 }, text: '\r\ninserted' }]);
  assert.equal(source.text, 'a\r\nb\r\ninserted\nc');
  source.applyChanges([{ range: { startLineNumber: 2, startColumn: 2, endLineNumber: 3, endColumn: 9 }, text: '' }]);
  assert.equal(source.text, 'a\r\nb\nc');
});

test('only an explicit EOL conversion normalizes every existing line ending', () => {
  const source = new SourceBuffer('\uFEFFa\r\nb\nc\rd', true);
  source.changeEol('\n'); assert.equal(source.text, '\uFEFFa\nb\nc\nd');
  source.changeEol('\r\n'); assert.equal(source.text, '\uFEFFa\r\nb\r\nc\r\nd');
  assert.equal(source.offsetAt({ lineNumber: 4, column: 1 }), 10);
});

test('textarea edits and immediate captures retain untouched CRLF and no final newline', () => {
  const source = new SourceBuffer('first\r\nsecond\nlast');
  source.replaceDisplayed('first\nsecond\nlast', 'first\nSECOND\nlast');
  assert.equal(source.text, 'first\r\nSECOND\nlast');
  source.replaceDisplayed('first\nSECOND\nlast', 'first\nSECOND\nlast\n');
  assert.equal(source.text, 'first\r\nSECOND\nlast\n');
  source.replaceDisplayed('first\nSECOND\nlast\n', 'firstSECOND\nlast\n');
  assert.equal(source.text, 'firstSECOND\nlast\n');
});

test('authoritative replacement resets source mappings even if the displayed text is identical', () => {
  const source = new SourceBuffer('a\nb\r\nc');
  source.set('a\r\nb\nc');
  assert.equal(source.offsetAt({ lineNumber: 2, column: 1 }), 3);
  assert.deepEqual(source.positionAt(5), { lineNumber: 3, column: 1 });
  source.set(''); assert.deepEqual(source.positionAt(0), { lineNumber: 1, column: 1 });
});
