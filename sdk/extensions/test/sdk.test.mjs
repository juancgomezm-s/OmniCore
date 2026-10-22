import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { readFile } from 'node:fs/promises';
import { protocolVersion, maximumFrameBytes } from '../dist/src/contracts.js';

test('compiled SDK negotiates identity, serves view/tool and cleans up', async () => {
  const child = spawn(process.execPath, ['dist/examples/minimal.js'], { stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout });
  const pending = new Map();
  lines.on('line', line => {
    const response = JSON.parse(line);
    if (response.method) child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: response.id, result: { kind: 'git', git: { branch: 'fixture' } } }) + '\n');
    else pending.get(response.id)?.(response);
  });
  let id = 0;
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const requestId = String(++id); const timer = setTimeout(() => reject(new Error('timeout')), 5000);
    pending.set(requestId, result => { clearTimeout(timer); pending.delete(requestId); resolve(result); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: requestId, method, params }) + '\n');
  });
  try {
    const init = { protocolVersion, extensionId: 'example.hello', capabilities: ['workspace.read'], sessionId: null };
    assert.deepEqual((await rpc('initialize', init)).result, init);
    const view = (await rpc('view.read', { extensionId: init.extensionId, viewId: 'hello' })).result;
    assert.equal(view.id, 'example.hello.hello'); assert.equal(view.items[1].data.git.branch, 'fixture');
    assert.equal((await rpc('tools/call', { name: 'hello', arguments: { name: 'prueba' } })).result.text, 'Hola, prueba');
    assert.ok((await rpc('execution.bypass', {})).error);
    assert.ok((await rpc('shutdown', {})).result);
  } finally { child.kill(); lines.close(); }
});

test('schema shares exact protocol and UTF-8 frame bound', async () => {
  const schema = JSON.parse(await readFile('protocol-v1.schema.json', 'utf8'));
  assert.equal(schema.$defs.initialize.properties.protocolVersion.const, protocolVersion);
  assert.equal(schema['x-maximumFrameBytes'], maximumFrameBytes);
});

test('distinct pre/post/observer hooks and cooperative cancellation', async () => {
  const moduleUrl = new URL('../dist/src/index.js', import.meta.url).href;
  const code = `import {Extension} from ${JSON.stringify(moduleUrl)}; const ext=new Extension();
    ext.before('BeforeTool',()=>({kind:'restrict',deniedToolIds:['filesystem.write']}));
    ext.after('AfterTool',()=>{}); ext.observe('RunCompleted',()=>{});
    ext.handle('slow',async (_,signal)=>{await new Promise(resolve=>signal.addEventListener('abort',resolve,{once:true}));return {cancelled:signal.aborted}});
    ext.listen();`;
  const child = spawn(process.execPath, ['--input-type=module', '-e', code], { stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout }); const pending = new Map(); let counter = 0;
  lines.on('line', line => { const response = JSON.parse(line); pending.get(response.id)?.(response); });
  function rpc(method, params) {
    const id = String(++counter);
    return { id, result: new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('timeout')), 5000);
      pending.set(id, value => { clearTimeout(timer); pending.delete(id); resolve(value); });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    }) };
  }
  try {
    await rpc('initialize', { protocolVersion, extensionId: 'test.hooks', capabilities: [], sessionId: null }).result;
    assert.equal((await rpc('hooks/execute', { point: 'BeforeTool' }).result).result.kind, 'restrict');
    assert.equal((await rpc('hooks/execute', { point: 'AfterTool' }).result).result.kind, 'continue');
    assert.equal((await rpc('hooks/execute', { point: 'RunCompleted' }).result).result.kind, 'continue');
    const slow = rpc('slow', {});
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: '$/cancelRequest', params: { id: slow.id } }) + '\n');
    assert.equal((await slow.result).result.cancelled, true);
    await rpc('shutdown', {}).result;
  } finally { child.kill(); lines.close(); }
});

test('activity pull iterator applies backpressure, advances cursor and cancels without another request', async () => {
  const moduleUrl = new URL('../dist/src/index.js', import.meta.url).href;
  const code = `import {Extension} from ${JSON.stringify(moduleUrl)}; const ext=new Extension();
    ext.handle('test/watch',async()=>{const stop=new AbortController();const iterator=ext.activity.watch({signal:stop.signal,intervalMs:500});
      const first=await iterator.next();const second=await iterator.next();stop.abort();const done=await iterator.next();
      return {first:first.value,second:second.value,done:done.done}});ext.listen();`;
  const child = spawn(process.execPath, ['--input-type=module', '-e', code], { stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout }); const requests = []; const pending = new Map(); let counter = 0;
  lines.on('line', line => {
    const response = JSON.parse(line);
    if (response.method) {
      requests.push(response.params.afterSequence);
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: response.id, result: {
        sessionId: 'session-test', nextSequence: response.params.afterSequence + 1, highWater: 2,
        earliestAvailableSequence: 1, gap: response.params.afterSequence === 0, items: [] } }) + '\n');
    } else pending.get(response.id)?.(response);
  });
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const id = String(++counter); const timer = setTimeout(() => reject(new Error('timeout')), 5000);
    pending.set(id, result => { clearTimeout(timer); pending.delete(id); resolve(result); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  try {
    await rpc('initialize', { protocolVersion, extensionId: 'test.activity', capabilities: ['history.read'], sessionId: 'session-test' });
    const result = (await rpc('test/watch', {})).result;
    assert.equal(result.first.gap, true); assert.equal(result.second.nextSequence, 2); assert.equal(result.done, true);
    assert.deepEqual(requests, [0, 1]); await rpc('shutdown', {});
  } finally { child.kill(); lines.close(); }
});
