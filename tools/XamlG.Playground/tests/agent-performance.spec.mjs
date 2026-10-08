import { performance } from 'node:perf_hooks';
import { test, expect } from './studio-fixture.mjs';
import { withAgentWorkbench, sendAgentEvent, agentReply, createAgentTask, reviewAgentRun, completedTask, agentSection } from './agent-fixture.mjs';

test('focused catalog and unchanged-state cursors reduce real SDK and companion payloads', async ({ page, request, baseURL }) => {
  await withAgentWorkbench({ page, request, baseURL }, ({ response, sequence }) => {
    sendAgentEvent(response, agentReply('Measured local fixture. '.repeat(700), sequence)); response.end();
  }, async ({ page, pane, api, requests }) => {
    const first = await createAgentTask(pane, 'Focused');
    await reviewAgentRun(page, pane, 'Inspect this project.'); await completedTask(api, first);
    await createAgentTask(pane, 'Full catalog');
    await agentSection(pane, 'Permissions');
    await pane.getByLabel('Send every tool schema with every request').check();
    const second = await pane.getByLabel('Task', { exact: true }).inputValue();
    await reviewAgentRun(page, pane, 'Inspect this project.'); await completedTask(api, second);
    const full = await api('state');
    const cursor = { sessionId: full.sessionId, revision: full.revision };
    const unchanged = await api('state', cursor);
    expect(unchanged.unchanged).toBe(true);
    const bytes = value => Buffer.byteLength(JSON.stringify(value));
    const durations = async args => {
      const times = [];
      for (let i = 0; i < 10; i++) { const start = performance.now(); await api('state', args); times.push(performance.now() - start); }
      return times.sort((a, b) => a - b)[5];
    };
    const metrics = {
      focusedTools: requests[0].tools.length, fullTools: requests[1].tools.length,
      focusedRequestBytes: bytes(requests[0]), fullRequestBytes: bytes(requests[1]),
      stateBytes: bytes(full), unchangedStateBytes: bytes(unchanged),
      fullStateMedianMs: await durations({}), unchangedStateMedianMs: await durations(cursor)
    };
    await test.info().attach('agent-performance.json', { body: JSON.stringify(metrics, null, 2), contentType: 'application/json' });
    expect(metrics.focusedTools).toBeLessThan(metrics.fullTools / 3);
    expect(metrics.focusedRequestBytes).toBeLessThan(metrics.fullRequestBytes * 0.35);
    expect(metrics.unchangedStateBytes).toBeLessThan(metrics.stateBytes * 0.01);
    // The synthetic endpoint measures request construction and transport, not paid model latency.
  });
});
