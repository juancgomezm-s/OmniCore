'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');
const { verifyArchive } = require('../../bin/update-helper.js');

const helper = path.resolve(__dirname, '../../bin/update-helper.js');
const operationId = '1234567890abcdef1234567890abcdef';

function tempDirectory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'omnicore-update-helper-'));
}

function stateFile(directory, patch = {}) {
  const statePath = path.join(directory, 'state.json');
  fs.writeFileSync(statePath, JSON.stringify({
    schemaVersion: 1,
    operation: 'Apply',
    status: 'Pending',
    operationId,
    helperPid: null,
    npmPrefix: path.join(directory, 'prefix'),
    assetPath: path.join(directory, 'update.tgz'),
    expectedSha256: null,
    expectedSize: null,
    updatedAtUtc: new Date().toISOString(),
    ...patch,
  }));
  return statePath;
}

test('helper rechecks the staged archive digest and size after waiting, before npm activation', () => {
  const directory = tempDirectory();
  try {
    const data = Buffer.from('verified release archive');
    const archive = path.join(directory, 'update.tgz');
    fs.writeFileSync(archive, data);
    const digest = crypto.createHash('sha256').update(data).digest('hex');
    assert.equal(verifyArchive(archive, digest, data.length), true);
    fs.writeFileSync(archive, 'changed after verification');
    assert.throws(() => verifyArchive(archive, digest, data.length), /archive_file_invalid|archive_digest_mismatch/);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper refuses a symlink in place of the staged archive', t => {
  const directory = tempDirectory();
  try {
    const archive = path.join(directory, 'real.tgz');
    const link = path.join(directory, 'link.tgz');
    const data = Buffer.from('release');
    fs.writeFileSync(archive, data);
    try {
      fs.symlinkSync(archive, link, 'file');
    } catch (error) {
      if (['EPERM', 'EACCES', 'ENOTSUP'].includes(error.code)) return t.skip('symlink creation is unavailable');
      throw error;
    }
    const digest = crypto.createHash('sha256').update(data).digest('hex');
    assert.throws(() => verifyArchive(link, digest, data.length), /archive_file_invalid/);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper activates through fake npm with the inspected prefix and matching operation state', () => {
  const directory = tempDirectory();
  try {
    const data = Buffer.from('fixture archive payload');
    const archive = path.join(directory, 'update.tgz');
    fs.writeFileSync(archive, data);
    const digest = crypto.createHash('sha256').update(data).digest('hex');
    const prefix = path.join(directory, process.platform === 'win32' ? '%global & prefix%' : 'prefix');
    const capture = path.join(directory, 'npm-args.json');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      expectedSha256: digest,
      expectedSize: data.length,
    });
    let npmPath;
    if (process.platform === 'win32') {
      npmPath = path.join(directory, 'fake-npm.cmd');
      fs.writeFileSync(npmPath, '@echo off\r\nexit /b 0\r\n');
      const cli = path.join(directory, 'node_modules', 'npm', 'bin', 'npm-cli.js');
      fs.mkdirSync(path.dirname(cli), { recursive: true });
      fs.writeFileSync(cli, `require('node:fs').writeFileSync(${JSON.stringify(capture)}, JSON.stringify(process.argv.slice(2)));\n`);
    } else {
      npmPath = path.join(directory, 'fake-npm');
      fs.writeFileSync(npmPath, `#!/usr/bin/env node\nrequire('node:fs').writeFileSync(${JSON.stringify(capture)}, JSON.stringify(process.argv.slice(2)));\n`);
      fs.chmodSync(npmPath, 0o755);
    }
    const result = spawnSync(process.execPath, [helper,
      '--mode', 'apply', '--parent-pid', '2147483647', '--state', statePath,
      '--package-path', archive, '--npm', npmPath, '--package', 'omnicore-cli',
      '--timeout', '2', '--operation-id', operationId, '--sha256', digest,
      '--size', String(data.length), '--npm-prefix', prefix, '--node', process.execPath,
    ], { encoding: 'utf8', timeout: 10_000, windowsHide: true });
    assert.equal(result.status, 0, JSON.stringify({ stderr: result.stderr, stdout: result.stdout,
      state: fs.readFileSync(statePath, 'utf8') }));
    const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    assert.equal(state.status, 'Succeeded');
    assert.equal(state.operationId, operationId);
    const captured = fs.readFileSync(capture, 'utf8');
    const args = JSON.parse(captured);
    assert.deepEqual(args.slice(0, 4), ['install', '-g', '--prefix', prefix]);
    assert.ok(args.includes(archive));
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper rejects stale durable operation identity without invoking npm', () => {
  const directory = tempDirectory();
  try {
    const data = Buffer.from('release');
    const archive = path.join(directory, 'update.tgz');
    fs.writeFileSync(archive, data);
    const digest = crypto.createHash('sha256').update(data).digest('hex');
    const statePath = stateFile(directory, { operationId: 'ffffffffffffffffffffffffffffffff' });
    const capture = path.join(directory, 'called');
    const npmPath = path.join(directory, 'fake-npm.js');
    fs.writeFileSync(npmPath, `require('node:fs').writeFileSync(${JSON.stringify(capture)}, 'called');\n`);
    const result = spawnSync(process.execPath, [helper,
      '--mode', 'apply', '--parent-pid', '2147483647', '--state', statePath,
      '--package-path', archive, '--npm', npmPath, '--operation-id', operationId,
      '--sha256', digest, '--size', String(data.length), '--npm-prefix', path.join(directory, 'prefix'),
      '--node', process.execPath,
    ], { encoding: 'utf8', timeout: 10_000, windowsHide: true });
    assert.notEqual(result.status, 0);
    assert.equal(fs.existsSync(capture), false);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).operationId,
      'ffffffffffffffffffffffffffffffff');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper stops before activation if the staged bytes changed after download verification', () => {
  const directory = tempDirectory();
  try {
    const original = Buffer.from('release bytes verified by the downloader');
    const archive = path.join(directory, 'update.tgz');
    fs.writeFileSync(archive, Buffer.from('replaced staged bytes')); 
    const digest = crypto.createHash('sha256').update(original).digest('hex');
    const capture = path.join(directory, 'called');
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      expectedSha256: digest,
      expectedSize: original.length,
    });
    let npmPath;
    if (process.platform === 'win32') {
      npmPath = path.join(directory, 'fake-npm.cmd');
      fs.writeFileSync(npmPath, '@echo off\r\nexit /b 0\r\n');
      const cli = path.join(directory, 'node_modules', 'npm', 'bin', 'npm-cli.js');
      fs.mkdirSync(path.dirname(cli), { recursive: true });
      fs.writeFileSync(cli, `require('node:fs').writeFileSync(${JSON.stringify(capture)}, 'called');\n`);
    } else {
      npmPath = path.join(directory, 'fake-npm');
      fs.writeFileSync(npmPath, `#!/usr/bin/env node\nrequire('node:fs').writeFileSync(${JSON.stringify(capture)}, 'called');\n`);
      fs.chmodSync(npmPath, 0o755);
    }
    const result = spawnSync(process.execPath, [helper,
      '--mode', 'apply', '--parent-pid', '2147483647', '--state', statePath,
      '--package-path', archive, '--npm', npmPath, '--package', 'omnicore-cli',
      '--timeout', '2', '--operation-id', operationId, '--sha256', digest,
      '--size', String(original.length), '--npm-prefix', prefix, '--node', process.execPath,
    ], { encoding: 'utf8', timeout: 10_000, windowsHide: true });
    assert.notEqual(result.status, 0);
    assert.equal(fs.existsSync(capture), false);
    const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    assert.equal(state.status, 'Failed');
    assert.equal(state.operationId, operationId);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
