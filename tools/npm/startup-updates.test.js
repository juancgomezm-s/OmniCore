'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { runManaged, backgroundEnvironment, eligible, waitForOperation } = require('./startup-updates');

function harness(responses, exits) {
  const calls = []; const children = []; const messages = [];
  const parent = new EventEmitter();
  parent.env = { PATH: 'fixture', OMNICORE_DATA_DIR: '/isolated', OPENAI_API_KEY: 'private',
    GH_TOKEN: 'private', OMNICORE_TRUSTED_ACTION_BOOTSTRAP: 'private' };
  parent.cwd = () => '/original';
  const options = { process: parent, stderr: { write: value => messages.push(value) },
    launchId: 'a'.repeat(32),
    rpc: async args => { calls.push(args); return responses.shift(); },
    launchChild: async (env, cwd) => { children.push({ env, cwd }); return exits.shift(); },
    spawn: (...args) => { calls.push(args); return { once() {}, unref() {} }; },
    sleep: async () => {}, readOperation: () => ({ schemaVersion: 1, operationId: 'b'.repeat(32), status: 'Succeeded' }) };
  return { options, calls, children, messages };
}

test('ordinary launch downloads in background without credentials or changing foreground arguments', async () => {
  const h = harness([{ action: 'continue', prepareInBackground: true }], [7]);
  assert.equal(await runManaged('/package/omni.dll', ['act', 'ñ a b'], h.options), 7);
  assert.equal(h.children[0].cwd, '/original');
  assert.equal(h.children[0].env.OMNICORE_STARTUP_MANAGED, '1');
  assert.equal(h.children[0].env.OPENAI_API_KEY, 'private');
  const preparation = h.calls[1];
  assert.deepEqual(preparation[1], ['/package/omni.dll', 'update', 'startup', 'prepare']);
  assert.deepEqual(preparation[2], { detached: true, windowsHide: true, stdio: 'ignore',
    env: { PATH: 'fixture', OMNICORE_DATA_DIR: '/isolated' } });
});

test('restart waits for exact existing operation and restores conversation in the same terminal path', async () => {
  const h = harness([{ action: 'continue' }, { action: 'wait', operationId: 'b'.repeat(32), statePath: '/state' },
    { action: 'continue', workspace: '/workspace', sessionId: 'session' }], [74, 0]);
  assert.equal(await runManaged('/package/omni.dll', [], h.options), 0);
  assert.equal(h.children.length, 2);
  assert.equal(h.children[1].cwd, '/workspace');
  assert.equal(h.children[1].env.OMNICORE_RESTART_SESSION, 'session');
  assert.deepEqual(h.calls[1], ['launch', 'a'.repeat(32), 'restart']);
  assert.deepEqual(h.calls[2], h.calls[1]);
});

test('deferred activation completes before first foreground child without replacing invocation workspace', async () => {
  const h = harness([{ action: 'wait', operationId: 'b'.repeat(32), statePath: '/state' },
    { action: 'continue', workspace: '/old-workspace', sessionId: 'old-session' }], [0]);
  assert.equal(await runManaged('/package/omni.dll', ['act'], h.options), 0);
  assert.equal(h.children.length, 1);
  assert.equal(h.children[0].cwd, '/original');
  assert.equal(h.children[0].env.OMNICORE_RESTART_SESSION, undefined);
});

test('unknown or mismatched effects never spawn a foreground or retry apply', async () => {
  for (const status of ['Unknown', 'Failed', 'Running']) {
    const h = harness([{ action: 'wait', operationId: 'b'.repeat(32), statePath: '/state' }], []);
    h.options.readOperation = () => ({ schemaVersion: 1, operationId: status === 'Running' ? 'c'.repeat(32) : 'b'.repeat(32), status });
    assert.equal(await runManaged('/package/omni.dll', [], h.options), 75);
    assert.equal(h.calls.length, 1); assert.equal(h.children.length, 0);
  }
});

test('offline bootstrap falls through once and blocked launch is bounded without prompts', async () => {
  const h = harness([], [0]); h.options.rpc = async () => { throw Error('offline'); };
  assert.equal(await runManaged('/package/omni.dll', ['act'], h.options), 0);
  assert.equal(h.children.length, 1);
  const blocked = harness([{ action: 'blocked' }], []);
  assert.equal(await runManaged('/package/omni.dll', [], blocked.options), 75);
  assert.equal(blocked.children.length, 0);
});

test('uncertain activation prints its saved recovery command without executing it or accepting control text', async () => {
  const h = harness([{ action: 'blocked', recoveryCommand: "node 'private recovery.js' --mode recover" }], []);
  assert.equal(await runManaged('/package/omni.dll', [], h.options), 75);
  assert.match(h.messages.join(''), /private recovery.js/);
  assert.equal(h.calls.length, 1); assert.equal(h.children.length, 0);
  const invalid = harness([{ action: 'blocked', recoveryCommand: '\u001b[31mcontrol' }], []);
  await runManaged('/package/omni.dll', [], invalid.options);
  assert.doesNotMatch(invalid.messages.join(''), /control/);
});

test('help and recovery commands do not invoke automatic updates', () => {
  for (const args of [['update', 'rollback'], ['help'], ['--help'], ['status'], ['host', '--help']])
    assert.equal(eligible(args), false);
  assert.equal(eligible(['host', '--stdio']), true);
  assert.equal(eligible([]), true);
});

test('background allowlist preserves Windows runtime variables with case insensitive names', () => {
  assert.deepEqual(backgroundEnvironment({ SystemRoot: 'windows', Path: 'node', AzureKey: 'private',
    OPENROUTER_API_KEY: 'private', OMNICORE_TRUSTED_ACTION_BOOTSTRAP: 'private' }),
  { SystemRoot: 'windows', Path: 'node' });
});

test('operation timeout observes only and never retries a helper', async () => {
  let now = 0; let reads = 0;
  await assert.rejects(waitForOperation({ operationId: 'b'.repeat(32), statePath: '/state' }, {
    now: () => now, sleep: async () => { now += 20 * 60 * 1000; },
    readOperation: () => { reads++; return { schemaVersion: 1, operationId: 'b'.repeat(32), status: 'Running' }; }
  }), /timeout/);
  assert.equal(reads, 1);
});

test('real child processes exercise bootstrap JSON, exact-operation wait, restart, cwd and argv', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'omni-startup-process-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }));
  fs.mkdirSync(path.join(root, 'updates'));
  const fixture = path.join(root, 'runtime.cjs');
  fs.writeFileSync(fixture, `
    const fs = require('node:fs'); const path = require('node:path');
    const root = path.dirname(process.argv[2]); const args = process.argv.slice(3);
    const receipt = path.join(root, 'child.json'); const state = path.join(root, 'updates', 'state.json');
    if (args[0] === 'update' && args[1] === 'startup') {
      let action = 'continue';
      if (args[4] === 'restart' && !fs.existsSync(state)) {
        fs.writeFileSync(state, JSON.stringify({schemaVersion:1, operationId:'b'.repeat(32), status:'Succeeded'}));
        action = 'wait';
      }
      console.log(JSON.stringify({action, dataDirectory:root, statePath:state, operationId:'b'.repeat(32),
        workspace:root, sessionId:'s', prepareInBackground:false}));
    } else if (!fs.existsSync(receipt)) {
      fs.writeFileSync(receipt, '{}'); process.exitCode = 74;
    } else {
      fs.writeFileSync(receipt, JSON.stringify({args, cwd:process.cwd(),
        session:process.env.OMNICORE_RESTART_SESSION, managed:process.env.OMNICORE_STARTUP_MANAGED}));
    }
  `);
  const parent = new EventEmitter(); parent.env = backgroundEnvironment(process.env); parent.cwd = () => root;
  const code = await runManaged(path.join(root, 'omni.dll'), ['act', 'ñ a b', '--json'], {
    process: parent, spawn: (_command, args, options) => spawn(process.execPath, [fixture, ...args], options)
  });
  assert.equal(code, 0);
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(root, 'child.json'), 'utf8')),
    { args: ['act', 'ñ a b', '--json'], cwd: root, session: 's', managed: '1' });
});
