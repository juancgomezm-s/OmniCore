'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
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
  assert.deepEqual(launch.args, ['-w', 'new', 'new-tab', '--profile', profileId, '--startingDirectory', parent.cwd(),
    parent.execPath, path.join('C:\\Omni Core', 'bin', 'omni.js'), '--current-terminal', 'tui']);
  const profile = JSON.parse(fs.readFileSync(fragment, 'utf8')).profiles[0];
  assert.equal(profile.antialiasingMode, 'cleartype');
  assert.deepEqual(profile.font, { face: 'Consolas', size: 12, weight: 400 });
  assert.match(profile.commandline, /--current-terminal/);
  assert.equal(fs.readFileSync(settings, 'utf8'), 'user settings');
  assert.deepEqual(prepare('C:\\Omni Core', parent), launch);
  assert.equal(JSON.parse((fs.readFileSync(fragment, 'utf8'))).profiles.length, 1);
});

test('upgrades an owned fontless profile to the approved font without changing its launch command', t => {
  const { parent, fragment, local } = fixture(t);
  prepare('package-root', parent);
  const previous = JSON.parse(fs.readFileSync(fragment, 'utf8'));
  const commandline = previous.profiles[0].commandline;
  delete previous.profiles[0].font;
  const legacyContent = JSON.stringify(previous, null, 2) + '\n';
  fs.writeFileSync(fragment, legacyContent);
  fs.writeFileSync(path.join(local, 'OmniCore', 'terminal-profile.sha256'),
    crypto.createHash('sha256').update(legacyContent).digest('hex'));
  assert.ok(prepare('package-root', parent));
  const upgraded = JSON.parse(fs.readFileSync(fragment, 'utf8')).profiles[0];
  assert.deepEqual(upgraded.font, { face: 'Consolas', size: 12, weight: 400 });
  assert.equal(upgraded.commandline, commandline);
  assert.equal(upgraded.guid, profileId);
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

test('Windows App Execution Alias is available even when existsSync follows it unsuccessfully', t => {
  const { parent, terminal } = fixture(t);
  const io = { ...fs, existsSync: p => p === terminal ? false : fs.existsSync(p),
    lstatSync: p => p === terminal ? { isFile: () => false, isSymbolicLink: () => true } : fs.lstatSync(p) };
  assert.equal(prepare('root', parent, io).terminal, terminal);
});

test('missing terminal and paths containing terminal command separators fall back', t => {
  const { parent, terminal } = fixture(t);
  parent.cwd = () => 'C:\\project;with-semicolon'; assert.equal(prepare('root', parent), null);
  parent.cwd = () => 'C:\\project'; assert.equal(prepare('root;another-command', parent), null);
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

test('interactive Windows Terminal handoff creates a visible window and successful exit does not fall back', t => {
  const { parent } = fixture(t);
  const calls = [], children = [];
  run([], { process: parent, packageRoot: 'package-root', existsSync: () => true,
    spawn: (...args) => {
      calls.push(args);
      const child = new EventEmitter(); child.exitCode = null; children.push(child); return child;
    } });
  assert.match(calls[0][0], /wt\.exe$/);
  assert.deepEqual(calls[0][2], { stdio: 'inherit', windowsHide: false });
  children[0].emit('exit', 0);
  assert.equal(parent.exitCode, 0);
  assert.equal(calls.length, 1);
});
