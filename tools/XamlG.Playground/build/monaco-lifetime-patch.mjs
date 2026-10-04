import { createHash } from 'node:crypto';

// Monaco 0.52.2's WordHighlighter ignores two Delayer promises. Retirement rejects
// those promises with CancellationError, even though the buffer was captured correctly.
// Patch the producer, not window.onerror/unhandledrejection. The existing upstream
// handler ignores cancellation and still reports real errors. See docs/monaco-compatibility.md.
export const monacoBundleSha256 = '90b588bc0b624e24052a576e1bcab2eaffec7bc666895188862eebd9c9745782';
export const highlighterSchedules = Object.freeze([
  'this.runDelayer.trigger(()=>{this._onPositionChanged(Y)})',
  'this.runDelayer.trigger(()=>{this._run()})'
]);

export function patchMonacoLifetime(source) {
  if (typeof source !== 'string') throw new TypeError('The Monaco bundle must be UTF-8 text.');
  if (createHash('sha256').update(source, 'utf8').digest('hex') !== monacoBundleSha256)
    throw new Error('Monaco bundle identity changed. Review the lifetime patch against the pinned upstream source before publishing.');
  for (const expression of highlighterSchedules) {
    const first = source.indexOf(expression);
    if (first < 0 || source.indexOf(expression, first + expression.length) >= 0)
      throw new Error('The pinned WordHighlighter schedule is missing or ambiguous.');
    source = source.replace(expression, expression + '.catch(y.onUnexpectedError)');
  }
  return source;
}
