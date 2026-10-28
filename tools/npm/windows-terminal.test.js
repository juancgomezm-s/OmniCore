'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter } = require('node:events');
const { eligible, prepare, profileId, quote } = require('./windows-terminal');
const { run } = require('./launcher');

function fixture(t) {
  const local = fs.mkdtempSync(path.join(os.tmpdir(), 'omnicore-terminal-test-'));
  t.after(() => fs.rmSync(local, { recursive: true, force: true }));
  const terminal = path.join(local, 'Microsoft', 'WindowsApps', 'wt.exe');
  fs.mkdirSync(path.dirname(terminal), { recursive: true });
  fs.writeFileSync(terminal, 'fixture');
  const parent = Object.assign(new EventEmitter(), { platform: 'win32', stdin: { isTTY: true },
    stdout: { isTTY: true }, env: { LOCALAPPDATA: local }, execPath: 'C:\\Node JS\\node.exe', cwd: () => 'C:\\My Project' });
  return { local, terminal, parent, fragment: path.join(local, 'Microsoft', 'Windows Terminal', 'Fragments', 'OmniCore', 'omnicore.json') };
}

test('only interactive local TUI startup is eligible; explicit and remote launches stay put', t => {
  const { parent } = fixture(t);
  assert.equal(eligible([], parent), true);
  assert.equal(eligible(['tui'], parent), true);
  for (const args of [['ask', 'hello'], ['--help'], ['pet', '--json'], ['tui', '--json'], ['--current-terminal']])
    assert.equal(eligible(args, parent), false);
  for (const variable of ['SSH_CONNECTION', 'SSH_TTY', 'TMUX', 'OMNICORE_CURRENT_TERMINAL']) {
    parent.env[variable] = '1'; assert.equal(eligible([], parent), false); delete parent.env[variable];
  }
  parent.env.WT_PROFILE_ID = profileId.toUpperCase(); assert.equal(eligible([], parent), false);
  delete parent.env.WT_PROFILE_ID;
  parent.stdout.isTTY = false; assert.equal(eligible([], parent), false);
  parent.stdout.isTTY = true; parent.stdin.isTTY = false; assert.equal(eligible([], parent), false);
  parent.stdin.isTTY = true; parent.platform = 'linux'; assert.equal(eligible([], parent), false);
});

test('owns one profile, preserves cwd, avoids recursion, and never edits terminal settings', t => {
  const { parent, fragment, local } = fixture(t);
  const settings = path.join(local, 'settings.json'); fs.writeFileSync(settings, 'user settings');
  const launch = prepare('C:\\Omni Core', parent);
  assert.deepEqual(launch.args, ['-w', 'new', 'new-tab', '--profile', profileId, '--startingDirectory', parent.cwd()]);
  const profile = JSON.parse(fs.readFileSync(fragment, 'utf8')).profiles[0];
  assert.equal(profile.antialiasingMode, 'cleartype');
  assert.match(profile.commandline, /--current-terminal/);
  assert.equal(fs.readFileSync(settings, 'utf8'), 'user settings');
  assert.deepEqual(prepare('C:\\Omni Core', parent), launch);
  assert.equal(JSON.parse((fs.readFileSync(fragment, 'utf8'))).profiles.length, 1);
});

test('updates its own installation path but respects a user-edited profile', t => {
  const { parent, fragment } = fixture(t);
  prepare('old-path', parent); prepare('new-path', parent);
  assert.match(fs.readFileSync(fragment, 'utf8'), /new-path/);
  const custom = fs.readFileSync(fragment, 'utf8').replace('cleartype', 'grayscale');
  fs.writeFileSync(fragment, custom);
  assert.equal(prepare('third-path', parent), null);
  assert.equal(fs.readFileSync(fragment, 'utf8'), custom);
});

test('missing terminal and paths containing terminal command separators fall back', t => {
  const { parent, terminal } = fixture(t);
  parent.cwd = () => 'C:\\project;with-semicolon'; assert.equal(prepare('root', parent), null);
  parent.cwd = () => 'C:\\project'; fs.unlinkSync(terminal); assert.equal(prepare('root', parent), null);
  assert.equal(quote('C:\\with spaces\\'), '"C:\\with spaces\\\\"');
  assert.equal(quote('a"b'), '"a\\"b"');
});

test('failed Windows Terminal startup invokes dotnet once; marker bypasses redirection', t => {
  const { parent } = fixture(t);
  const calls = [], children = [];
  const options = { process: parent, packageRoot: 'package-root', existsSync: () => true,
    spawn: (...args) => { calls.push(args); const child = new EventEmitter(); child.exitCode = null; children.push(child); return child; } };
  run([], options);
  assert.match(calls[0][0], /wt\.exe$/);
  children[0].emit('error', new Error('terminal failed'));
  children[0].emit('exit', 1);
  assert.equal(calls.length, 2); assert.equal(calls[1][0], 'dotnet');
  children[1].emit('exit', 0, null);
  calls.length = 0;
  run(['--current-terminal', 'tui'], options);
  assert.equal(calls[0][0], 'dotnet'); assert.equal(calls[0][1].at(-1), 'tui');
  assert.equal(calls[0][1].includes('--current-terminal'), false);
});
