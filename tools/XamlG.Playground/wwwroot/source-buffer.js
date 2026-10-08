// Monaco and textarea normalize line endings in their displayed buffers. Keep exact
// project text separately and translate positions at the editor/source boundary.
export class SourceBuffer {
  constructor(text, hideBom = false) { this.hideBom = hideBom; this.set(text); }
  set(text) {
    this.text = text;
    this.starts = [this.hideBom && text.startsWith('\uFEFF') ? 1 : 0];
    this.ends = [];
    for (const match of text.matchAll(/\r\n|\r|\n/g)) {
      this.ends.push(match.index); this.starts.push(match.index + match[0].length);
    }
    this.ends.push(text.length);
  }
  offsetAt(position) {
    const line = Math.max(0, Math.min(this.starts.length - 1, position.lineNumber - 1));
    return Math.min(this.ends[line], this.starts[line] + Math.max(0, position.column - 1));
  }
  positionAt(offset) {
    offset = Math.max(0, Math.min(this.text.length, offset));
    let first = 0, last = this.starts.length - 1;
    while (first < last) {
      const middle = Math.ceil((first + last) / 2);
      if (this.starts[middle] <= offset) first = middle; else last = middle - 1;
    }
    return { lineNumber: first + 1, column: Math.max(0, Math.min(offset, this.ends[first]) - this.starts[first]) + 1 };
  }
  applyChanges(changes) {
    const edits = changes.map(change => ({
      start: this.offsetAt({ lineNumber: change.range.startLineNumber, column: change.range.startColumn }),
      end: this.offsetAt({ lineNumber: change.range.endLineNumber, column: change.range.endColumn }), text: change.text
    })).sort((a, b) => b.start - a.start);
    let text = this.text;
    for (const edit of edits) text = text.slice(0, edit.start) + edit.text + text.slice(edit.end);
    this.set(text);
  }
  changeEol(eol) { this.set(this.text.replace(/\r\n|\r|\n/g, eol)); }
  // The fallback has no range-change event. Recover its single changed interval
  // against the previous displayed value, preserving all untouched source bytes.
  replaceDisplayed(before, after) {
    let start = 0, end = 0;
    while (start < before.length && start < after.length && before[start] === after[start]) start++;
    while (end < before.length - start && end < after.length - start && before[before.length - end - 1] === after[after.length - end - 1]) end++;
    const displayed = new SourceBuffer(before);
    const from = displayed.positionAt(start), to = displayed.positionAt(before.length - end);
    this.applyChanges([{ range: { startLineNumber: from.lineNumber, startColumn: from.column, endLineNumber: to.lineNumber, endColumn: to.column },
      text: after.slice(start, after.length - end) }]);
  }
  displayColumn(line, column) { return line === 1 && this.starts[0] === 1 ? Math.max(1, column - 1) : column; }
  sourceColumn(line, column) { return line === 1 && this.starts[0] === 1 ? column + 1 : column; }
}
