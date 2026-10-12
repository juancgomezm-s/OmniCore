'use strict';

const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const path = require('node:path');
const test = require('node:test');
const { run } = require('./launcher');

function makeHarness() {
  const child = new EventEmitter();
  child.exitCode = null;
  child.kill = signal => {
    child.lastSignal = signal;
    return true;
  };
  const parent = new EventEmitter();
  const messages = [];
  const calls = [];
  const options = {
    packageRoot: path.join('C:', 'Program Files', 'OmniCore'),
    spawn: (...args) => {
      calls.push(args);
      return child;
    },
    existsSync: () => true,
    process: parent,
    stderr: { write: message => messages.push(message) }
  };
  return { child, parent, messages, calls, options };
}

test('launches dotnet with the packaged DLL and forwards arguments unchanged', () => {
  const { calls, options } = makeHarness();
  const args = ['act', 'fix this file.cs', '--json', '--value=a b', 'ñ'];

  run(args, options);

  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], 'dotnet');
  assert.deepEqual(calls[0][1], [
    path.join('C:', 'Program Files', 'OmniCore', 'dist', 'npm', 'omni.dll'),
    ...args
  ]);
  assert.deepEqual(calls[0][2], { stdio: 'inherit', windowsHide: true });
});

test('forwards termination signals and maps child exit status', () => {
  const { child, parent, options } = makeHarness();
  run([], options);

  parent.emit('SIGINT');
  assert.equal(child.lastSignal, 'SIGINT');
  child.emit('exit', 23, null);
  assert.equal(parent.exitCode, 23);
  assert.equal(parent.listenerCount('SIGINT'), 0);
});

test('maps a child signal exit to the conventional shell status', () => {
  const { child, parent, options } = makeHarness();
  run([], options);
  child.emit('exit', null, 'SIGTERM');
  assert.equal(parent.exitCode, 143);
});

test('prints a focused runtime message when dotnet cannot be found', () => {
  const { child, parent, messages, options } = makeHarness();
  run([], options);
  const error = new Error('spawn dotnet ENOENT');
  error.code = 'ENOENT';
  child.emit('error', error);

  assert.equal(parent.exitCode, 1);
  assert.match(messages.join(''), /runtime de \.NET 10/);
  assert.equal(parent.listenerCount('SIGINT'), 0);
});

test('explains when the package was installed without its published CLI', () => {
  const { parent, messages, calls, options } = makeHarness();
  options.packageRoot = path.join(__dirname, 'missing-package-fixture');
  options.existsSync = () => false;

  const child = run([], options);

  assert.equal(child, null);
  assert.equal(calls.length, 0);
  assert.equal(parent.exitCode, 1);
  assert.match(messages.join(''), /sin --ignore-scripts/);
});
