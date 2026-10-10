import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';

// A small evidence artifact, independent of browser traces and asset-request logs.
// This reporter never overrides Playwright's result or hides an unexpected test.
export default class WorkspaceReporter {
  suite;
  errors = [];
  onBegin(_config, suite) { this.suite = suite; }
  onError(error) { this.errors.push(this.message(error)); }
  message(error) {
    let text = String(error?.message ?? error?.value ?? error ?? '').replace(/\u001b\[[0-9;]*m/g, '');
    for (const name of ['XAMLG_TEST_OWNER_TOKEN', 'XAMLG_TEST_MCP_TOKEN', 'XAMLG_STUDIO_TOKEN', 'XAMLG_STUDIO_OWNER_TOKEN']) {
      const secret = process.env[name];
      if (secret) text = text.replaceAll(secret, '[test credential]');
    }
    return text.slice(0, 6000);
  }
  onEnd(result) {
    const tests = (this.suite?.allTests() ?? []).map(test => ({
      title: test.titlePath().filter(Boolean).join(' › '),
      outcome: test.outcome(), expectedStatus: test.expectedStatus,
      attempts: test.results.map(attempt => ({
        status: attempt.status, retry: attempt.retry, duration: attempt.duration,
        errors: attempt.errors.map(error => this.message(error)).slice(0, 4)
      }))
    }));
    const counts = { expected: 0, unexpected: 0, flaky: 0, skipped: 0 };
    for (const test of tests) counts[test.outcome]++;
    const path = resolve('test-results/workspace-browser-summary.json');
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, JSON.stringify({ format: 1, status: result.status, counts, errors: this.errors, tests }, null, 2));
  }
  printsToStdio() { return false; }
}
