// No UI dependencies: reads enabled views from the real OmniCore stdio Host.
// node headless.mjs <absolute-omni-executable> [extensionId viewId]
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { randomUUID } from 'node:crypto';
const [executable, extensionId, viewId] = process.argv.slice(2);
if (!executable) throw new Error('Usage: node headless.mjs <omni-executable> [extensionId viewId]');
const child = spawn(executable, ['host', '--stdio'], { stdio: ['pipe', 'pipe', 'inherit'] });
const lines = createInterface({ input: child.stdout });
const waiting = new Map();
lines.on('line', line => { const response = JSON.parse(line); waiting.get(response.messageId)?.(response); });
function request(operation, fields) {
  const messageId = randomUUID();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('host_timeout')), 30000);
    waiting.set(messageId, value => { clearTimeout(timer); waiting.delete(messageId); resolve(value); });
    child.stdin.write(JSON.stringify({ version: '1.0', messageId, messageType: operation === 'hello' ? 'hello' : 'command',
      payload: operation === 'hello' ? fields : { operation, ...fields } }) + '\n');
  });
}
try {
  console.log(JSON.stringify(await request('hello', { supportedVersions: ['1.0'], capabilities: ['query.extensionPlatform'] })));
  const name = extensionId && viewId ? 'extensions.view:' + encodeURIComponent(JSON.stringify({ extensionId, viewId })) : 'extensions.views';
  console.log(JSON.stringify(await request('query', { name })));
} finally { child.stdin.end(); lines.close(); child.kill(); }
