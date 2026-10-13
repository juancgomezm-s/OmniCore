// Text-only normalization for captured Chat Completions, Messages and Responses.
// Never treat protocol wrappers or non-text attachments as injected instructions.
function contentText(content) {
  if (typeof content === 'string') return content;
  if (content == null) return '';
  if (Array.isArray(content) && content.every(block => ['text', 'input_text', 'output_text'].includes(block.type)
      && typeof block.text === 'string')) return content.map(block => block.text).join('\n');
  throw new Error('Unsupported non-text content in initial context fixture.');
}
function normalizePayload(body) {
  const messages = body.messages || body.input || [];
  if (!Array.isArray(messages)) throw new Error('Expected a text-message array.');
  const system = [contentText(body.system), contentText(body.instructions),
    ...messages.filter(message => ['system', 'developer'].includes(message.role)).map(message => contentText(message.content))]
    .filter(Boolean).join('\n');
  const user = messages.filter(message => message.role === 'user').map(message => contentText(message.content)).join('\n');
  // Recent Codex Responses requests attach namespace catalogs as input items.
  // Count them too; absent body.tools must never turn a real catalog into zero.
  const tools = [...(body.tools || []), ...messages.filter(message => message.type === 'additional_tools')
    .flatMap(message => message.tools || [])];
  function toolNames(items, prefix = '') {
    return items.flatMap(tool => tool.type === 'namespace' ? toolNames(tool.tools || [], prefix + tool.name + '.')
      : [prefix + (tool.function?.name || tool.name || tool.type)]);
  }
  return { system, user, tools, messages, toolIds: toolNames(tools) };
}
module.exports = { contentText, normalizePayload };
