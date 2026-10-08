import { Readable } from 'node:stream';
import { finished } from 'node:stream/promises';
import { createInterface } from 'node:readline';
import { expect } from './studio-fixture.mjs';
import { connectMcp } from './live-preview.mjs';

// Real authenticated HTTP and browser WebSocket traffic. The legacy connection
// also establishes pairing; modern requests exercise the stateless SDK route.
export async function connectModernMcp(page, request) {
  const legacy = await connectMcp(page, request);
  const pending = new Set();
  let sequence = 0;
  async function send(method, params, id, signal, options = {}) {
    const name = params.name ?? params.uri ?? params.taskId ?? params.ref?.uri;
    const response = await fetch(process.env.XAMLG_TEST_MCP_URL + '/mcp', {
      method: 'POST', signal,
      headers: { Authorization: `Bearer ${process.env.XAMLG_TEST_MCP_TOKEN}`, Accept: 'application/json, text/event-stream',
        'Content-Type': 'application/json', 'MCP-Protocol-Version': '2026-07-28', 'Mcp-Method': method,
        ...(name ? { 'Mcp-Name': name } : {}) },
      body: JSON.stringify({ jsonrpc: '2.0', id, method, params: { ...params, _meta: {
        'io.modelcontextprotocol/protocolVersion': '2026-07-28',
        'io.modelcontextprotocol/clientInfo': { name: options.clientName ?? 'XamlG integrated acceptance', version: '1' },
        'io.modelcontextprotocol/clientCapabilities': options.tasks === false ? {} : { extensions: { 'io.modelcontextprotocol/tasks': {} } }
      } } })
    });
    if (!response.ok) throw new Error(`${method}: HTTP ${response.status}: ${(await response.text()).slice(0, 2000)}`);
    expect(response.headers.get('Mcp-Session-Id')).toBeNull();
    return response;
  }
  async function rpc(method, params = {}, options) {
    const id = ++sequence;
    const response = await send(method, params, id, AbortSignal.timeout(45000), options);
    const body = await response.text();
    const messages = body.startsWith('{') ? [JSON.parse(body)] : body.split('\n').filter(line => line.startsWith('data:')).map(line => JSON.parse(line.slice(5)));
    const message = messages.find(item => item.id === id);
    expect(message, method).toBeTruthy();
    if (message.error) throw new Error(JSON.stringify(message.error));
    return message.result;
  }
  return {
    rpc, legacy,
    async call(name, args = {}, options) {
      const result = await rpc('tools/call', { name, arguments: args }, options);
      if (result.isError) throw new Error(result.content.filter(item => item.type === 'text').map(item => item.text).join('\n'));
      expect(result.resultType).not.toBe('task');
      return result.structuredContent;
    },
    async subscribe(notifications) {
      const abort = new AbortController();
      const id = `acceptance-stream-${++sequence}`;
      const response = await send('subscriptions/listen', { notifications }, id, AbortSignal.any([abort.signal, AbortSignal.timeout(60000)]));
      const stream = Readable.fromWeb(response.body);
      const closed = finished(stream).catch(() => {});
      const lines = createInterface({ input: stream });
      const iterator = lines[Symbol.asyncIterator]();
      const messages = [];
      const subscription = {
        messages,
        async next() {
          for (;;) {
            let timer;
            const { value, done } = await Promise.race([iterator.next(), new Promise((_, reject) => {
              timer = setTimeout(() => reject(new Error('No expected MCP notification; received: ' + JSON.stringify(messages.map(item => ({ method: item.method, uri: item.params?.uri, status: item.params?.status }))))), 15000);
            })]).finally(() => clearTimeout(timer));
            expect(done, 'The MCP subscription closed before its expected notification').toBe(false);
            if (!value.startsWith('data:')) continue;
            const message = JSON.parse(value.slice(5));
            if (message.error) throw new Error(JSON.stringify(message.error));
            expect(message.params._meta['io.modelcontextprotocol/subscriptionId']).toBe(id);
            messages.push(message); return message;
          }
        },
        async until(predicate) {
          for (;;) {
            const known = messages.find(predicate);
            if (known) return known;
            await subscription.next();
          }
        },
        async close() { pending.delete(subscription); lines.close(); stream.destroy(); abort.abort(); await closed; }
      };
      pending.add(subscription);
      const ack = await subscription.next();
      expect(ack.method).toBe('notifications/subscriptions/acknowledged');
      subscription.ack = ack.params.notifications;
      return subscription;
    },
    async close() {
      for (const stream of [...pending]) await stream.close();
      if (!page.isClosed()) await legacy.close();
    }
  };
}
