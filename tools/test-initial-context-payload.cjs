const assert = require('node:assert/strict');
const { normalizePayload } = require('./initial-context-payload.cjs');
const chat = normalizePayload({ messages: [{ role: 'system', content: 'system' },
  { role: 'user', content: [{ type: 'text', text: 'question' }] }], tools: [{ type: 'function', function: { name: 'read' } }] });
assert.equal(chat.system, 'system'); assert.equal(chat.user, 'question'); assert.deepEqual(chat.toolIds, ['read']);
const messages = normalizePayload({ system: [{ type: 'text', text: 'native' }],
  messages: [{ role: 'user', content: 'question' }], tools: [{ name: 'Read', input_schema: {} }] });
assert.equal(messages.system, 'native'); assert.deepEqual(messages.toolIds, ['Read']);
const responses = normalizePayload({ instructions: 'base', input: [
  { type: 'additional_tools', role: 'developer', tools: [{ type: 'namespace', name: 'functions',
    tools: [{ type: 'function', name: 'exec' }, { type: 'custom', name: 'apply_patch' }] }] },
  { type: 'message', role: 'developer', content: [{ type: 'input_text', text: 'permissions' }] },
  { type: 'message', role: 'user', content: [{ type: 'input_text', text: 'question' }] }
] });
assert.equal(responses.system, 'base\npermissions'); assert.equal(responses.user, 'question');
assert.deepEqual(responses.toolIds, ['functions.exec', 'functions.apply_patch']);
assert.equal(responses.tools.length, 1);
assert.throws(() => normalizePayload({ messages: [{ role: 'user', content: [{ type: 'input_image', image_url: 'fixture' }] }] }));
console.log('PASS: Chat, Messages, Responses instructions, inline namespace catalogs and non-text rejection.');
