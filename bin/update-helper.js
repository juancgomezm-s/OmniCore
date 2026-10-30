#!/usr/bin/env node
'use strict';

// Detached helper for npm's global Windows installation. It accepts only the
// operation already recorded by the host and never makes a trust decision.

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');

const MAX_OUTPUT_BYTES = 16 * 1024 * 1024;
const DEFAULT_TIMEOUT_SECONDS = 900;
const SMOKE_TIMEOUT_MS = 30_000;
const semverPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

function fail(message) {
  process.stderr.write(String(message) + '\n');
  process.exitCode = 1;
  throw new Error(String(message));
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

function operationName(mode) {
  return mode === 'apply' ? 'Apply' : 'Rollback';
}

function pidStatus(pid) {
  if (!Number.isSafeInteger(pid) || pid <= 0) return 'unknown';
  try {
    process.kill(pid, 0);
    return 'alive';
  } catch (error) {
    // Only ESRCH proves a process is absent. EPERM, EACCES, platform-specific
    // errors, and reused PIDs all fail closed as alive/unknown.
    return error && error.code === 'ESRCH' ? 'dead' : 'unknown';
  }
}

async function waitForExit(pid, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  while (pidStatus(pid) !== 'dead' && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  return pidStatus(pid) === 'dead';
}

function readState(statePath) {
  return JSON.parse(fs.readFileSync(statePath, 'utf8'));
}

function samePath(left, right) {
  const normalize = value => path.resolve(value);
  const a = normalize(left);
  const b = normalize(right);
  return process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b;
}

function writeState(statePath, operation, operationId, expectedStatus, patch) {
  const current = readState(statePath);
  if (current.operation !== operation || current.operationId !== operationId
      || current.status !== expectedStatus) throw new Error('stale_operation');
  const next = { ...current, ...patch, updatedAtUtc: new Date().toISOString() };
  const temporary = statePath + '.' + process.pid + '.' + crypto.randomBytes(8).toString('hex') + '.tmp';
  const descriptor = fs.openSync(temporary, 'wx', 0o600);
  try {
    fs.writeFileSync(descriptor, JSON.stringify(next, null, 2), 'utf8');
    fs.fsyncSync(descriptor);
  } finally {
    fs.closeSync(descriptor);
  }
  fs.renameSync(temporary, statePath);
  return next;
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

function collectBackupFiles(directory, files) {
  const root = path.resolve(directory);
  const rootStat = fs.lstatSync(root);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) throw new Error('backup_reparse_point');
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const absolute = path.join(root, entry.name);
    const info = fs.lstatSync(absolute);
    if (info.isSymbolicLink()) throw new Error('backup_reparse_point');
    if (info.isDirectory()) collectBackupFiles(absolute, files);
    else if (info.isFile()) files.push(absolute);
    else throw new Error('backup_entry_type_invalid');
  }
}

function appendFileContentHash(treeHash, relativePath, absolutePath) {
  const pathBytes = Buffer.from(relativePath, 'utf8');
  if (pathBytes.length > 0x7fffffff) throw new Error('backup_path_too_long');
  const length = Buffer.allocUnsafe(4);
  length.writeInt32LE(pathBytes.length, 0);
  treeHash.update(length);
  treeHash.update(pathBytes);

  const flags = fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW || 0);
  const descriptor = fs.openSync(absolutePath, flags);
  try {
    const before = fs.fstatSync(descriptor, { bigint: true });
    if (!before.isFile()) throw new Error('backup_entry_type_invalid');
    const contentHash = crypto.createHash('sha256');
    const buffer = Buffer.allocUnsafe(64 * 1024);
    let count;
    while ((count = fs.readSync(descriptor, buffer, 0, buffer.length, null)) > 0)
      contentHash.update(buffer.subarray(0, count));
    const after = fs.fstatSync(descriptor, { bigint: true });
    if (before.size !== after.size || before.mtimeNs !== after.mtimeNs)
      throw new Error('backup_changed_during_hash');
    treeHash.update(contentHash.digest());
  } finally {
    fs.closeSync(descriptor);
  }
}

function fingerprintDirectory(directory) {
  const root = path.resolve(directory);
  const files = [];
  collectBackupFiles(root, files);
  files.sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
  const treeHash = crypto.createHash('sha256');
  for (const file of files) {
    const relative = path.relative(root, file).replace(/\\/g, '/');
    appendFileContentHash(treeHash, relative, file);
  }
  return treeHash.digest('hex');
}

function assertOperation(statePath, operation, operationId, expectedStatus) {
  const state = readState(statePath);
  if (state.operation !== operation || state.operationId !== operationId || state.status !== expectedStatus)
    throw new Error('stale_operation');
  return state;
}

function createLock(lockPath, owner) {
  const descriptor = fs.openSync(lockPath, 'wx', 0o600);
  try {
    fs.writeFileSync(descriptor, JSON.stringify(owner), 'utf8');
    fs.fsyncSync(descriptor);
  } finally {
    fs.closeSync(descriptor);
  }
}

function readLock(lockPath) {
  let contents;
  try { contents = fs.readFileSync(lockPath, 'utf8'); }
  catch (error) {
    if (error && error.code === 'ENOENT') return { missing: true };
    throw error;
  }
  if (contents.length === 0) return { legacy: true };
  let value;
  try { value = JSON.parse(contents); }
  catch { return { invalid: true }; }
  if (!value || typeof value !== 'object' || Array.isArray(value)
      || !Number.isSafeInteger(value.pid) || value.pid <= 0
      || !['Apply', 'Rollback'].includes(value.operation)
      || typeof value.operationId !== 'string' || !/^[a-f0-9]{32}$/.test(value.operationId)
      || typeof value.statePath !== 'string' || !path.isAbsolute(value.statePath)) return { invalid: true };
  return { owner: value };
}

function releaseLock(lockPath, owner) {
  const current = readLock(lockPath);
  if (current.owner && current.owner.pid === owner.pid && current.owner.operation === owner.operation
      && current.owner.operationId === owner.operationId && samePath(current.owner.statePath, owner.statePath)) {
    fs.unlinkSync(lockPath);
  }
}

function matchingState(statePath, operation, operationId) {
  const state = readState(statePath);
  if (state.operation !== operation || state.operationId !== operationId
      || !['Pending', 'Running'].includes(state.status)) throw new Error('lock_operation_mismatch');
  return state;
}

function reclaimOrphanLock(lockPath, statePath, operation, operationId) {
  const lock = readLock(lockPath);
  if (lock.missing) return 'retry';
  if (lock.invalid) throw new Error('lock_owner_invalid');
  const state = matchingState(statePath, operation, operationId);
  if (lock.owner) {
    const owner = lock.owner;
    if (owner.operation !== operation || owner.operationId !== operationId
        || !samePath(owner.statePath, statePath)) throw new Error('operation_in_progress');
    if (pidStatus(owner.pid) !== 'dead') throw new Error('operation_in_progress');
    if (state.status === 'Running' && state.helperPid !== owner.pid
        || state.status === 'Pending' && state.helperPid !== null && state.helperPid !== undefined
          && state.helperPid !== owner.pid)
      throw new Error('lock_owner_mismatch');
  } else {
    // Before owner metadata was added, the lock was empty. Do not infer a PID
    // from arbitrary legacy bytes: the durable state must identify a dead helper.
    if (state.helperPid === null || state.helperPid === undefined || pidStatus(state.helperPid) !== 'dead'
        || state.parentPid === null || state.parentPid === undefined || pidStatus(state.parentPid) !== 'dead')
      throw new Error('legacy_lock_unverifiable');
  }

  if (state.npmPid !== null && state.npmPid !== undefined && pidStatus(state.npmPid) !== 'dead')
    throw new Error('operation_in_progress');

  if (state.status === 'Running') {
    writeState(statePath, operation, operationId, 'Running', {
      status: 'Unknown',
      detail: 'helper_interrupted_during_installation',
    });
    fs.unlinkSync(lockPath);
    return 'unknown';
  }
  if (state.parentPid !== null && state.parentPid !== undefined && pidStatus(state.parentPid) !== 'dead')
    throw new Error('operation_in_progress');

  fs.unlinkSync(lockPath);
  return 'retry';
}

function acquireLock(lockPath, statePath, operation, operationId) {
  const owner = {
    pid: process.pid,
    operation,
    operationId,
    statePath: path.resolve(statePath),
  };
  try {
    createLock(lockPath, owner);
    return { owner, recovered: false };
  } catch (error) {
    if (!error || error.code !== 'EEXIST') throw new Error('lock_failed');
  }

  const recovery = reclaimOrphanLock(lockPath, statePath, operation, operationId);
  if (recovery === 'unknown') return { owner: null, unknown: true };
  try {
    createLock(lockPath, owner);
    return { owner, recovered: true };
  } catch (error) {
    throw new Error(error && error.code === 'EEXIST' ? 'operation_in_progress' : 'lock_failed');
  }
}

function npmCliPath(npmPath) {
  if (path.extname(npmPath).toLowerCase() === '.js') return npmPath;
  const candidate = path.join(path.dirname(npmPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
  return fs.existsSync(candidate) ? candidate : null;
}

function runNpmDirect(npmPath, args, cwd, timeoutMs, nodePath) {
  if (process.platform === 'win32') {
    const cli = npmCliPath(npmPath);
    if (!cli) return { status: 1, error: new Error('npm_cli_unavailable'), stdout: '', stderr: '' };
    return spawnSync(nodePath, [cli, ...args], {
      cwd,
      encoding: 'utf8',
      timeout: timeoutMs,
      windowsHide: true,
      maxBuffer: MAX_OUTPUT_BYTES,
    });
  }
  return spawnSync(npmPath, args, {
    cwd,
    encoding: 'utf8',
    timeout: timeoutMs,
    windowsHide: true,
    maxBuffer: MAX_OUTPUT_BYTES,
  });
}

async function npmChildMain(serialized) {
  let request;
  try { request = JSON.parse(serialized); }
  catch { process.exitCode = 78; return; }
  if (!request || !Array.isArray(request.args) || typeof request.npmPath !== 'string'
      || typeof request.cwd !== 'string' || !Number.isSafeInteger(request.timeoutMs)
      || typeof request.nodePath !== 'string') {
    process.exitCode = 78;
    return;
  }

  let input = '';
  for await (const chunk of process.stdin) input += chunk.toString('utf8');
  if (input.trim() !== 'continue') {
    // The owner did not durably register this PID. In particular, never start
    // npm when the parent helper exits before sending its authorization byte.
    process.exitCode = 78;
    return;
  }

  const result = runNpmDirect(request.npmPath, request.args, request.cwd, request.timeoutMs, request.nodePath);
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  if (result.error) process.stderr.write(String(result.error.message || result.error) + '\n');
  process.exitCode = Number.isInteger(result.status) ? result.status : 1;
}

async function runNpm(npmPath, args, cwd, timeoutMs, nodePath, statePath, operation, operationId,
  expectedStateStatus, statePatch = {}) {
  const request = JSON.stringify({ npmPath, args, cwd, timeoutMs, nodePath });
  const child = spawn(process.execPath, [__filename, '--npm-child', request], {
    cwd: path.dirname(statePath),
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  let stdout = '';
  let stderr = '';
  let overflow = false;
  const appendOutput = (target, chunk) => {
    if (overflow) return;
    const next = target === 'stdout' ? stdout + chunk.toString('utf8') : stderr + chunk.toString('utf8');
    if (Buffer.byteLength(next, 'utf8') > MAX_OUTPUT_BYTES) {
      overflow = true;
      child.kill();
      return;
    }
    if (target === 'stdout') stdout = next;
    else stderr = next;
  };
  child.stdout.on('data', chunk => appendOutput('stdout', chunk));
  child.stderr.on('data', chunk => appendOutput('stderr', chunk));

  await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('spawn', resolve);
  });

  try {
    const current = assertOperation(statePath, operation, operationId, expectedStateStatus);
    writeState(statePath, operation, operationId, expectedStateStatus, {
      ...statePatch,
      npmPid: child.pid,
    });
    child.stdin.end('continue\n');
    const exit = await new Promise(resolve => child.once('close', (code, signal) => resolve({ code, signal })));

    const after = readState(statePath);
    if (after.operation === operation && after.operationId === operationId
        && after.npmPid === child.pid && ['Pending', 'Running'].includes(after.status)) {
      writeState(statePath, operation, operationId, after.status, { npmPid: null });
    } else if (after.operation !== current.operation || after.operationId !== current.operationId) {
      throw new Error('stale_operation');
    }

    if (overflow) return { status: null, error: new Error('npm_output_exceeded_limit'), stdout, stderr };
    return {
      status: exit.code,
      error: exit.signal ? new Error('npm_wrapper_signal_' + exit.signal) : null,
      stdout,
      stderr,
    };
  } catch (error) {
    // EOF is a deny-by-default signal to a wrapper that has not yet started npm.
    child.stdin.end();
    child.kill();
    throw error;
  }
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

function regularFile(filePath) {
  try {
    const info = fs.lstatSync(filePath);
    return info.isFile() && !info.isSymbolicLink();
  } catch { return false; }
}

function verifyInstalledPackage(packageDirectory, packageName, expectedVersion) {
  const directory = path.resolve(packageDirectory);
  const directoryInfo = fs.lstatSync(directory);
  if (!directoryInfo.isDirectory() || directoryInfo.isSymbolicLink()) throw new Error('installed_package_directory_invalid');
  const packageJsonPath = path.join(directory, 'package.json');
  if (!regularFile(packageJsonPath)) throw new Error('installed_package_manifest_missing');
  const manifest = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
  if (manifest.name !== packageName || manifest.version !== expectedVersion)
    throw new Error('installed_package_identity_mismatch');
  const launcher = path.join(directory, 'bin', 'omni.js');
  const assembly = path.join(directory, 'dist', 'npm', 'omni.dll');
  if (!regularFile(launcher) || !regularFile(assembly)) throw new Error('installed_cli_files_missing');
  return launcher;
}

function smokeInstalledCli(launcher, nodePath, statePath, operationId, timeoutMs) {
  const smokeRoot = path.join(path.dirname(statePath), 'staging', 'smoke-' + operationId);
  const dataDirectory = path.join(smokeRoot, 'data');
  const configDirectory = path.join(smokeRoot, 'config');
  fs.mkdirSync(dataDirectory, { recursive: true });
  fs.mkdirSync(configDirectory, { recursive: true });
  try {
    const result = spawnSync(nodePath, [launcher, '--help'], {
      cwd: smokeRoot,
      env: {
        ...process.env,
        OMNICORE_DATA_DIR: dataDirectory,
        OMNICORE_CONFIG_DIR: configDirectory,
      },
      encoding: 'utf8',
      timeout: timeoutMs,
      windowsHide: true,
      maxBuffer: MAX_OUTPUT_BYTES,
    });
    if (result.error || result.status !== 0 || !String(result.stdout || '').trim())
      throw new Error('installed_cli_smoke_failed');
    return true;
  } finally {
    try { fs.rmSync(smokeRoot, { recursive: true, force: true }); } catch { }
  }
}

function ensureNoReparsePoint(filePath, expectedKind) {
  const stat = fs.lstatSync(filePath);
  if (stat.isSymbolicLink() || expectedKind === 'file' && !stat.isFile()
      || expectedKind === 'directory' && !stat.isDirectory()) throw new Error('update_target_invalid');
}

function preflightWindowsPackageDlls(packageDirectory) {
  if (process.platform !== 'win32' || !fs.existsSync(packageDirectory)) return;
  const root = path.resolve(packageDirectory);
  const rootInfo = fs.lstatSync(root);
  if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) throw new Error('installed_package_directory_invalid');

  function visit(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const filePath = path.join(directory, entry.name);
      const info = fs.lstatSync(filePath);
      if (info.isSymbolicLink()) continue;
      if (info.isDirectory()) {
        visit(filePath);
        continue;
      }
      if (!info.isFile() || path.extname(entry.name).toLowerCase() !== '.dll') continue;

      let descriptor;
      try {
        // Opening for read/write requests the Windows access mode npm will need,
        // but no bytes are written. Sharing violations identify a live process
        // that still has the installed assembly open.
        descriptor = fs.openSync(filePath, 'r+');
      } catch (error) {
        if (error && ['EACCES', 'EPERM', 'EBUSY', 'ETXTBSY'].includes(error.code))
          throw new Error('active_binary_in_use');
        throw error;
      } finally {
        if (descriptor !== undefined) fs.closeSync(descriptor);
      }
    }
  }

  visit(root);
}

async function main() {
  if (process.argv[2] === '--npm-child') {
    await npmChildMain(process.argv[3] || '');
    return process.exitCode || 0;
  }
  const args = parseArgs(process.argv.slice(2));
  const mode = args.mode;
  const operation = operationName(mode);
  const parentPid = Number(args['parent-pid']);
  const statePath = args.state;
  const npmPath = args.npm;
  const nodePath = args.node;
  const operationId = args['operation-id'];
  const npmPrefix = args['npm-prefix'];
  const packageDirectory = args['package-directory'];
  const packageName = args.package || 'omnicore-cli';
  const expectedVersion = args['expected-version'];
  const expectedBackupSha256 = args['backup-sha256'];
  const timeoutSeconds = Number(args.timeout || DEFAULT_TIMEOUT_SECONDS);
  const timeoutMs = timeoutSeconds * 1000;
  if (mode !== 'apply' && mode !== 'rollback') fail('invalid_mode');
  if (!Number.isSafeInteger(parentPid) || parentPid <= 0) fail('invalid_parent');
  if (!statePath || !path.isAbsolute(statePath) || !fs.existsSync(statePath)) fail('missing_state');
  if (!npmPath || !fs.existsSync(npmPath)) fail('missing_npm');
  if (!nodePath || !fs.existsSync(nodePath)) fail('missing_node');
  if (!packageDirectory || !path.isAbsolute(packageDirectory)) fail('invalid_package_directory');
  if (!expectedVersion || !semverPattern.test(expectedVersion)) fail('invalid_expected_version');
  if (mode === 'rollback' && (!expectedBackupSha256 || !/^[a-f0-9]{64}$/.test(expectedBackupSha256)))
    fail('invalid_backup_fingerprint');
  if (!Number.isSafeInteger(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 3600) fail('invalid_timeout');
  const npmExecutable = process.platform === 'win32' ? npmCliPath(npmPath) : npmPath;
  if (!npmExecutable) throw new Error('npm_cli_unavailable');
  if (!/^[a-z0-9@/_-]+$/.test(packageName)) fail('invalid_package');
  if (!operationId || !/^[a-f0-9]{32}$/.test(operationId)) fail('invalid_operation_id');
  if (!npmPrefix || !path.isAbsolute(npmPrefix)) fail('invalid_npm_prefix');
  const target = mode === 'apply' ? args['package-path'] : args['backup-path'];
  if (!target || !path.isAbsolute(target) || !fs.existsSync(target)) fail('missing_target');
  ensureNoReparsePoint(target, mode === 'apply' ? 'file' : 'directory');

  const lockPath = statePath + '.helper.lock';
  let lock;
  try {
    lock = acquireLock(lockPath, statePath, operation, operationId);
  } catch (error) {
    fail(error && error.message === 'operation_in_progress' ? 'operation_in_progress'
      : error && error.message === 'legacy_lock_unverifiable' ? 'legacy_lock_unverifiable'
        : error && error.message === 'lock_operation_mismatch' ? 'lock_operation_mismatch'
          : error && error.message === 'lock_owner_mismatch' ? 'lock_owner_mismatch' : 'lock_failed');
  }
  if (lock.unknown) return 1;

  let packDirectory;
  try {
    if (!(await waitForExit(parentPid, timeoutMs))) {
      writeState(statePath, operation, operationId, 'Pending', { status: 'Failed', detail: 'parent_still_running' });
      return 1;
    }
    const pending = assertOperation(statePath, operation, operationId, 'Pending');
    if (!samePath(pending.npmPrefix, npmPrefix)) throw new Error('npm_prefix_mismatch');
    if (mode === 'apply' && (!samePath(pending.assetPath, target)
        || pending.expectedSha256 !== args.sha256 || pending.expectedSize !== Number(args.size)))
      throw new Error('archive_expectation_mismatch');
    if (mode === 'rollback' && !samePath(pending.backupPath, target)) throw new Error('backup_path_mismatch');
    if (mode === 'apply') verifyArchive(target, args.sha256, Number(args.size));
    if (mode === 'rollback') {
      if (pending.backupSha256 !== expectedBackupSha256
          || fingerprintDirectory(target) !== expectedBackupSha256) throw new Error('backup_fingerprint_mismatch');
      verifyInstalledPackage(target, packageName, expectedVersion);
    }

    let packagePath = target;
    if (mode === 'rollback') {
      packDirectory = path.join(path.dirname(statePath), 'staging', 'rollback-' + operationId);
      fs.mkdirSync(packDirectory, { recursive: true });
      const packed = await runNpm(npmExecutable,
        ['pack', target, '--ignore-scripts', '--pack-destination', packDirectory], packDirectory, timeoutMs, nodePath,
        statePath, operation, operationId, 'Pending');
      if (packed.error || packed.status !== 0) {
        writeState(statePath, operation, operationId, 'Pending', { status: 'Failed', detail: 'rollback_pack_failed' });
        return 1;
      }
      const lines = String(packed.stdout || '').split(/\r?\n/).map(line => line.trim()).filter(Boolean);
      const name = lines.length > 0 ? lines[lines.length - 1] : '';
      packagePath = path.join(packDirectory, name);
      if (!name.endsWith('.tgz') || !regularFile(packagePath)) {
        writeState(statePath, operation, operationId, 'Pending', { status: 'Failed', detail: 'rollback_pack_missing' });
        return 1;
      }
      if (pending.backupSha256 !== expectedBackupSha256
          || fingerprintDirectory(target) !== expectedBackupSha256) {
        writeState(statePath, operation, operationId, 'Pending', {
          status: 'Failed', detail: 'backup_fingerprint_mismatch',
        });
        return 1;
      }
    }

    const current = assertOperation(statePath, operation, operationId, 'Pending');
    if (!samePath(current.npmPrefix, npmPrefix)) throw new Error('npm_prefix_mismatch');
    if (mode === 'apply' && (!samePath(current.assetPath, target)
        || current.expectedSha256 !== args.sha256 || current.expectedSize !== Number(args.size)))
      throw new Error('archive_expectation_mismatch');
    if (mode === 'rollback' && (!samePath(current.backupPath, target)
        || current.backupSha256 !== expectedBackupSha256)) throw new Error('backup_fingerprint_mismatch');
    if (mode === 'apply') verifyArchive(target, args.sha256, Number(args.size));

    // Check locked Windows assemblies immediately before the first activation
    // side effect. Linux keeps its existing npm failure/Unknown behavior.
    preflightWindowsPackageDlls(packageDirectory);

    // Durable intent precedes the first npm side effect. A crash from here on
    // is ambiguous until package identity and the CLI smoke both pass.
    const installed = await runNpm(npmExecutable,
      ['install', '-g', '--prefix', npmPrefix, '--ignore-scripts', '--no-audit', '--no-fund', packagePath],
      path.dirname(statePath), timeoutMs, nodePath, statePath, operation, operationId, 'Pending', {
        status: 'Running',
        helperPid: process.pid,
        detail: 'npm_install_started',
      });
    if (installed.error || installed.status !== 0) {
      writeState(statePath, operation, operationId, 'Running', {
        status: 'Unknown', detail: 'npm_outcome_unknown: ' + detail(installed),
      });
      return 1;
    }

    writeState(statePath, operation, operationId, 'Running', { detail: 'npm_succeeded_verifying_package' });
    let launcher;
    try {
      launcher = verifyInstalledPackage(packageDirectory, packageName, expectedVersion);
    } catch (error) {
      writeState(statePath, operation, operationId, 'Running', {
        status: 'Unknown', detail: [
          'installed_package_directory_invalid', 'installed_package_manifest_missing',
          'installed_package_identity_mismatch', 'installed_cli_files_missing',
        ].includes(error && error.message) ? error.message : 'installed_package_identity_mismatch',
      });
      return 1;
    }

    writeState(statePath, operation, operationId, 'Running', { detail: 'verifying_installed_cli' });
    try {
      smokeInstalledCli(launcher, nodePath, statePath, operationId, SMOKE_TIMEOUT_MS);
    } catch {
      writeState(statePath, operation, operationId, 'Running', {
        status: 'Unknown', detail: 'installed_cli_smoke_failed',
      });
      return 1;
    }

    writeState(statePath, operation, operationId, 'Running', {
      status: 'Succeeded', detail: mode === 'apply' ? 'update_applied' : 'rollback_applied',
    });
    return 0;
  } catch (error) {
    try {
      const current = readState(statePath);
      if (current.operation === operation && current.operationId === operationId
          && (current.status === 'Pending' || current.status === 'Running')) {
        writeState(statePath, operation, operationId, current.status, {
          status: current.status === 'Running' ? 'Unknown' : 'Failed',
          detail: String(error && error.message || error),
        });
      }
    } catch { }
    process.stderr.write(String(error && error.stack || error) + '\n');
    return 1;
  } finally {
    if (packDirectory) {
      try { fs.rmSync(packDirectory, { recursive: true, force: true }); } catch { }
    }
    try { releaseLock(lockPath, lock.owner); } catch { }
  }
}

module.exports = { verifyArchive, fingerprintDirectory, parseArgs, runNpm, verifyInstalledPackage, smokeInstalledCli };

if (require.main === module) {
  main().then(code => { process.exitCode = code; }).catch(error => {
    process.stderr.write(String(error && error.stack || error) + '\n');
    process.exitCode = 1;
  });
}
