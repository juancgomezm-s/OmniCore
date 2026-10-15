'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { install } = require('./install');

function harness(t, overrides = {}) {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'omnicore-installer-test-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const calls = [];
  const messages = [];
  const options = {
    temp, platform: 'linux', home: path.join(temp, 'home with spaces ñ'),
    env: { npm_execpath: __filename },
    stderr: { write: text => messages.push(text) },
    stdout: { write: text => messages.push(text) },
    spawnSync(executable, args, settings) {
      calls.push({ executable, args, settings });
      if (args[1] === 'pack') {
        fs.writeFileSync(path.join(args.at(-1), 'omnicore-cli-0.1.0.tgz'), 'fixture');
      }
      return { status: 0 };
    },
    ...overrides
  };
  return { calls, messages, options };
}

test('Linux stages and checks the CLI before installing with npm into the user prefix', t => {
  const { calls, options } = harness(t);
  assert.equal(install([], options), 0);
  assert.equal(calls.length, 4);
  assert.match(calls[0].args[0], /prepare\.js$/);
  assert.equal(calls[1].executable, 'dotnet');
  assert.equal(calls[1].args.at(-1), '--help');
  assert.deepEqual(calls[2].args.slice(1, 4), ['pack', '--ignore-scripts', '--pack-destination']);
  assert.deepEqual(calls[3].args.slice(1, 8), [
    'install', '--global', '--ignore-scripts', '--no-audit', '--no-fund', '--prefix',
    path.join(options.home, '.local')
  ]);
  assert.equal(calls[3].args.at(-1), path.join(calls[2].args.at(-1), 'omnicore-cli-0.1.0.tgz'));
  assert.ok(calls.every(call => call.settings.stdio === 'inherit' && !call.settings.shell));
});

test('Windows preserves the configured global npm prefix by default', t => {
  const { calls, options } = harness(t, { platform: 'win32' });
  assert.equal(install([], options), 0);
  assert.equal(calls[3].args.includes('--prefix'), false);
});

test('an explicit prefix with spaces is forwarded as one argument', t => {
  const { calls, options } = harness(t);
  const prefix = path.join(options.temp, 'installation with spaces ñ');
  assert.equal(install(['--prefix', prefix], options), 0);
  assert.equal(calls[3].args.at(-2), prefix);
});

for (const [label, failAt] of [['publish', 1], ['startup check', 2], ['pack', 3], ['npm install', 4]]) {
  test(`${label} failure is propagated and no subsequent operation runs`, t => {
    const { calls, options } = harness(t);
    const original = options.spawnSync;
    options.spawnSync = (...args) => {
      const result = original(...args);
      return calls.length === failAt ? { status: 23 } : result;
    };
    assert.equal(install([], options), 23);
    assert.equal(calls.length, failAt);
  });
}

test('invalid options cannot start a build or change an installation', t => {
  const { calls, options } = harness(t);
  assert.equal(install(['--prefix'], options), 2);
  assert.equal(install(['--unexpected'], options), 2);
  assert.equal(calls.length, 0);
});

test('a missing package archive cannot replace the installed application', t => {
  const { calls, options } = harness(t);
  options.spawnSync = (executable, args) => {
    calls.push({ executable, args });
    return { status: 0 };
  };
  assert.equal(install([], options), 1);
  assert.equal(calls.length, 3);
});

test('process launch errors and termination signals stop the installation', t => {
  const { options } = harness(t);
  options.spawnSync = () => ({ error: new Error('ENOENT') });
  assert.equal(install([], options), 1);
  options.spawnSync = () => ({ status: null, signal: 'SIGTERM' });
  assert.equal(install([], options), 1);
});

test('direct invocation explains that npm must select its own CLI', t => {
  const { calls, options } = harness(t, { env: {} });
  assert.equal(install([], options), 1);
  assert.equal(calls.length, 0);
});
