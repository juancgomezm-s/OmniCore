#!/usr/bin/env node
'use strict';

// Detached helper: waits for the calling omni process to exit, then installs the
// already-verified package. It never downloads or decides trust on its own.

const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const crypto = require('node:crypto');
let activationStarted = false;

function fail(message) {
  process.stderr.write(String(message) + '\n');
  process.exit(1);
}

function parseArgs(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key || !key.startsWith('--') || value === undefined) fail('invalid_arguments');
    const name = key.slice(2);
    if (Object.prototype.hasOwnProperty.call(values, name)) fail('duplicate_argument');
    values[name] = value;
  }
  return values;
}

function isAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error && error.code === 'EPERM';
  }
}

async function waitForExit(pid, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (isAlive(pid) && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  return !isAlive(pid);
}

function readState(statePath) {
  return JSON.parse(fs.readFileSync(statePath, 'utf8'));
}

function writeState(statePath, operationId, expectedStatus, patch) {
  const current = readState(statePath);
  if (current.operationId !== operationId || current.status !== expectedStatus) throw new Error('stale_operation');
  const next = { ...current, ...patch, updatedAtUtc: new Date().toISOString() };
  const temporary = statePath + '.' + process.pid + '.tmp';
  fs.writeFileSync(temporary, JSON.stringify(next, null, 2), 'utf8');
  fs.renameSync(temporary, statePath);
}

function verifyArchive(filePath, expectedSha256, expectedSize) {
  if (!path.isAbsolute(filePath) || !/^[a-f0-9]{64}$/.test(expectedSha256)
      || !Number.isSafeInteger(expectedSize) || expectedSize < 1) throw new Error('invalid_archive_expectation');
  const stat = fs.lstatSync(filePath);
  if (stat.isSymbolicLink() || !stat.isFile() || stat.size !== expectedSize) throw new Error('archive_file_invalid');
  const actual = crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
  if (actual !== expectedSha256) throw new Error('archive_digest_mismatch');
  return true;
}

function assertOperation(statePath, operationId, expectedStatus) {
  const state = readState(statePath);
  if (state.operationId !== operationId || state.status !== expectedStatus) throw new Error('stale_operation');
  return state;
}

function samePath(left, right) {
  const comparison = process.platform === 'win32' ? (value) => value.toLowerCase() : (value) => value;
  return comparison(path.resolve(left)) === comparison(path.resolve(right));
}

function npmCliPath(npmPath) {
  if (path.extname(npmPath).toLowerCase() === '.js') return npmPath;
  const candidate = path.join(path.dirname(npmPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
  return fs.existsSync(candidate) ? candidate : null;
}

function runNpm(npmPath, args, cwd, timeoutMs, nodePath) {
  if (process.platform === 'win32') {
    const cli = npmCliPath(npmPath);
    if (!cli) return { status: 1, error: new Error('npm_cli_unavailable'), stdout: '', stderr: '' };
    return spawnSync(nodePath, [cli, ...args], {
      cwd,
      encoding: 'utf8',
      timeout: timeoutMs,
      windowsHide: true,
      maxBuffer: 16 * 1024 * 1024,
    });
  }
  return spawnSync(npmPath, args, {
    cwd,
    encoding: 'utf8',
    timeout: timeoutMs,
    windowsHide: true,
    maxBuffer: 16 * 1024 * 1024,
  });
}

function detail(result) {
  const parts = ['npm_exit_' + result.status];
  if (result.error) parts.push(String(result.error.message || result.error));
  const stderr = (result.stderr || '').trim();
  const stdout = (result.stdout || '').trim();
  if (stderr) parts.push(stderr.slice(-2000));
  else if (stdout) parts.push(stdout.slice(-2000));
  return parts.join(': ');
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const mode = args.mode;
  const parentPid = Number(args['parent-pid']);
  const statePath = args.state;
  const npmPath = args.npm;
  const nodePath = args.node;
  const operationId = args['operation-id'];
  const npmPrefix = args['npm-prefix'];
  const packageName = args.package || 'omnicore-cli';
  const timeoutMs = Math.max(1, Number(args.timeout || 900)) * 1000;
  if (mode !== 'apply' && mode !== 'rollback') fail('invalid_mode');
  if (!Number.isInteger(parentPid) || parentPid <= 0) fail('invalid_parent');
  if (!statePath || !fs.existsSync(statePath)) fail('missing_state');
  if (!npmPath || !fs.existsSync(npmPath)) fail('missing_npm');
  if (!nodePath || !fs.existsSync(nodePath)) fail('missing_node');
  const npmExecutable = process.platform === 'win32' ? npmCliPath(npmPath) : npmPath;
  if (!npmExecutable) throw new Error('npm_cli_unavailable');
  if (!/^[a-z0-9@/_-]+$/.test(packageName)) fail('invalid_package');
  if (!operationId || !/^[a-f0-9]{32}$/.test(operationId)) fail('invalid_operation_id');
  if (!npmPrefix || !path.isAbsolute(npmPrefix)) fail('invalid_npm_prefix');
  const target = mode === 'apply' ? args['package-path'] : args['backup-path'];
  if (!target || !path.isAbsolute(target) || !fs.existsSync(target)) fail('missing_target');

  const lockPath = statePath + '.helper.lock';
  let lock;
  try {
    lock = fs.openSync(lockPath, 'wx');
  } catch (error) {
    fail(error && error.code === 'EEXIST' ? 'operation_in_progress' : 'lock_failed');
  }

  try {
    if (!(await waitForExit(parentPid, timeoutMs))) {
      writeState(statePath, operationId, 'Pending', { status: 'Failed', detail: 'parent_still_running' });
      return 1;
    }
    const pending = assertOperation(statePath, operationId, 'Pending');
    if (!samePath(pending.npmPrefix, npmPrefix)) throw new Error('npm_prefix_mismatch');
    if (mode === 'apply' && (!samePath(pending.assetPath, target)
        || pending.expectedSha256 !== args.sha256 || pending.expectedSize !== Number(args.size)))
      throw new Error('archive_expectation_mismatch');
    if (mode === 'rollback' && !samePath(pending.backupPath, target)) throw new Error('backup_path_mismatch');
    writeState(statePath, operationId, 'Pending', { status: 'Running', helperPid: process.pid, detail: 'installing' });

    let packagePath = target;
    const operation = assertOperation(statePath, operationId, 'Running');
    if (mode === 'apply') verifyArchive(target, args.sha256, Number(args.size));
    if (mode === 'rollback') {
      const packDirectory = path.join(path.dirname(statePath), 'staging', 'rollback-' + Date.now());
      fs.mkdirSync(packDirectory, { recursive: true });
      const packed = runNpm(npmExecutable,
        ['pack', target, '--ignore-scripts', '--pack-destination', packDirectory], packDirectory, timeoutMs, nodePath);
      if (packed.status !== 0) {
        writeState(statePath, operationId, 'Running', { status: 'Failed', detail: detail(packed) });
        return 1;
      }
      const lines = String(packed.stdout || '').split(/\r?\n/).map(line => line.trim()).filter(Boolean);
      const name = lines.length > 0 ? lines[lines.length - 1] : '';
      packagePath = path.join(packDirectory, name);
      if (!name.endsWith('.tgz') || !fs.existsSync(packagePath)) {
        writeState(statePath, operationId, 'Running', { status: 'Failed', detail: 'rollback_pack_missing' });
        return 1;
      }
    }

    const current = assertOperation(statePath, operationId, 'Running');
    if (current.npmPrefix !== npmPrefix || operation.npmPrefix !== npmPrefix) throw new Error('npm_prefix_mismatch');
    if (mode === 'apply' && (!samePath(current.assetPath, target)
        || current.expectedSha256 !== args.sha256 || current.expectedSize !== Number(args.size)))
      throw new Error('archive_expectation_mismatch');
    if (mode === 'apply') verifyArchive(target, args.sha256, Number(args.size));
    activationStarted = true;
    const installed = runNpm(npmExecutable,
      ['install', '-g', '--prefix', npmPrefix, '--ignore-scripts', '--no-audit', '--no-fund', packagePath],
      path.dirname(statePath), timeoutMs, nodePath);
    if (installed.status !== 0) {
      writeState(statePath, operationId, 'Running', { status: 'Unknown', detail: 'npm_outcome_unknown: ' + detail(installed) });
      return 1;
    }
    writeState(statePath, operationId, 'Running', { status: 'Succeeded', detail: mode === 'apply' ? 'update_applied' : 'rollback_applied' });
    return 0;
  } finally {
    try { fs.closeSync(lock); } catch { }
    try { fs.unlinkSync(lockPath); } catch { }
  }
}

module.exports = { verifyArchive, parseArgs, runNpm };

if (require.main === module) {
  main().then(code => process.exit(code)).catch(error => {
    try {
      const args = parseArgs(process.argv.slice(2));
      if (args.state && args['operation-id']) {
        const state = readState(args.state);
        if (state.status === 'Pending' || state.status === 'Running')
          writeState(args.state, args['operation-id'], state.status,
            { status: activationStarted ? 'Unknown' : 'Failed',
              detail: String(error && error.message || error) });
      }
    } catch { }
    try { process.stderr.write(String(error && error.stack || error) + '\n'); } catch { }
    process.exit(1);
  });
}
