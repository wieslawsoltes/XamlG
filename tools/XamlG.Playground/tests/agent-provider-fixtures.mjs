export function providerEvents(provider, output, round) {
  if (provider === 'openai') return [{ type: 'response.completed', sequence_number: round,
    response: { id: `resp_${round}`, object: 'response', created_at: 123, model: 'test-model', status: 'completed', output, usage: { input_tokens: 10, output_tokens: 5, total_tokens: 15 } } }];
  if (provider === 'gemini') return [{ candidates: [{ index: 0, content: { role: 'model', parts: output.map(item => item.type === 'function_call' ?
    { functionCall: { id: item.call_id, name: item.name, args: JSON.parse(item.arguments) } } : { text: item.content[0].text }) }, finishReason: 'STOP' }],
    usageMetadata: { promptTokenCount: 10, candidatesTokenCount: 5, totalTokenCount: 15 } }];
  const events = [{ type: 'message_start', message: { id: `msg_${round}`, type: 'message', role: 'assistant', model: 'test-model', content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 10, output_tokens: 0 } } }];
  output.forEach((item, index) => {
    events.push({ type: 'content_block_start', index, content_block: item.type === 'function_call' ? { type: 'tool_use', id: item.call_id, name: item.name, input: {} } : { type: 'text', text: '' } });
    events.push({ type: 'content_block_delta', index, delta: item.type === 'function_call' ? { type: 'input_json_delta', partial_json: item.arguments } : { type: 'text_delta', text: item.content[0].text } });
    events.push({ type: 'content_block_stop', index });
  });
  events.push({ type: 'message_delta', delta: { stop_reason: output.some(item => item.type === 'function_call') ? 'tool_use' : 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } });
  events.push({ type: 'message_stop' });
  return events;
}

export const modelCatalog = provider => provider === 'openai' ? { object: 'list', data: [{ id: 'test-model', object: 'model', created: 1, owned_by: 'fixture' }] } :
  provider === 'anthropic' ? { data: [{ id: 'test-model', display_name: 'Test model', type: 'model', created_at: '2026-01-01T00:00:00Z' }], has_more: false, first_id: 'test-model', last_id: 'test-model' } :
  { models: [{ name: 'models/test-model', displayName: 'Test model', supportedGenerationMethods: ['generateContent'] }] };
