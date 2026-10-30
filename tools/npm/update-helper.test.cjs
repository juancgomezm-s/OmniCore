'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
const test = require('node:test');
const { fingerprintDirectory, verifyArchive } = require('../../bin/update-helper.js');

const helper = path.resolve(__dirname, '../../bin/update-helper.js');
const operationId = '1234567890abcdef1234567890abcdef';
const expectedVersion = '1.2.3';
const deadPid = 2147483647;

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
    parentPid: deadPid,
    helperPid: null,
    npmPrefix: path.join(directory, 'prefix'),
    assetPath: path.join(directory, 'update.tgz'),
    backupPath: path.join(directory, 'backup'),
    backupSha256: null,
    expectedSha256: null,
    expectedSize: null,
    updatedAtUtc: new Date().toISOString(),
    ...patch,
  }));
  return statePath;
}

function installFixtureSource({ packageDirectory, version = expectedVersion, capture, npmStatus = 0,
  smokeStatus = 0, lockPath, statePath }) {
  const program = `
const fs = require('node:fs');
const path = require('node:path');
const packageDirectory = ${JSON.stringify(packageDirectory)};
fs.mkdirSync(path.join(packageDirectory, 'bin'), { recursive: true });
fs.mkdirSync(path.join(packageDirectory, 'dist', 'npm'), { recursive: true });
fs.writeFileSync(path.join(packageDirectory, 'package.json'), JSON.stringify({ name: 'omnicore-cli', version: ${JSON.stringify(version)} }));
fs.writeFileSync(path.join(packageDirectory, 'bin', 'omni.js'), ${JSON.stringify(`process.stdout.write('OmniCore help\\n'); process.exit(${smokeStatus});`)});
fs.writeFileSync(path.join(packageDirectory, 'dist', 'npm', 'omni.dll'), 'fixture assembly');
${capture ? `fs.writeFileSync(${JSON.stringify(capture)}, JSON.stringify({ args: process.argv.slice(2), lock: fs.readFileSync(${JSON.stringify(lockPath)}, 'utf8'), state: JSON.parse(fs.readFileSync(${JSON.stringify(statePath)}, 'utf8')) }));` : ''}
process.exit(${npmStatus});
`;
  return program;
}

function fakeNpm(directory, options = {}) {
  const packageDirectory = options.packageDirectory || path.join(directory, 'prefix', 'node_modules', 'omnicore-cli');
  const statePath = options.statePath || path.join(directory, 'state.json');
  const capture = options.capture || path.join(directory, 'npm-args.json');
  const lockPath = statePath + '.helper.lock';
  const source = installFixtureSource({
    packageDirectory,
    version: options.version,
    capture: options.captureEnabled === false ? null : capture,
    npmStatus: options.npmStatus,
    smokeStatus: options.smokeStatus,
    lockPath,
    statePath,
  });
  let npmPath;
  if (process.platform === 'win32') {
    npmPath = path.join(directory, 'fake-npm.cmd');
    fs.writeFileSync(npmPath, '@echo off\r\nexit /b 0\r\n');
    const cli = path.join(directory, 'node_modules', 'npm', 'bin', 'npm-cli.js');
    fs.mkdirSync(path.dirname(cli), { recursive: true });
    fs.writeFileSync(cli, source);
  } else {
    npmPath = path.join(directory, 'fake-npm');
    fs.writeFileSync(npmPath, `#!/usr/bin/env node\n${source}`);
    fs.chmodSync(npmPath, 0o755);
  }
  return { npmPath, packageDirectory, capture, lockPath };
}

function invokeHelper({ directory, statePath, archive, npmPath, packageDirectory, prefix, mode = 'apply',
  expectedVersion: version = expectedVersion, backupPath = path.join(directory, 'backup'), backupSha256,
  operation = mode === 'apply' ? 'Apply' : 'Rollback',
  operationId: id = operationId, env = process.env, timeout = 10_000, operationTimeoutSeconds = 2 }) {
  const targetArgs = mode === 'apply'
    ? ['--package-path', archive, '--sha256', crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
      '--size', String(fs.statSync(archive).size)]
    : ['--backup-path', backupPath, '--backup-sha256', backupSha256];
  const args = [helper, '--mode', mode, '--parent-pid', String(deadPid), '--state', statePath,
    ...targetArgs, '--npm', npmPath, '--package', 'omnicore-cli',
    '--expected-version', version, '--package-directory', packageDirectory,
    '--timeout', String(operationTimeoutSeconds), '--operation-id', id, '--npm-prefix', prefix, '--node', process.execPath];
  return spawnSync(process.execPath, args, { encoding: 'utf8', timeout, windowsHide: true, env });
}

function referenceFingerprint(directory) {
  const root = path.resolve(directory);
  const files = [];
  function visit(current) {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const absolute = path.join(current, entry.name);
      const stat = fs.lstatSync(absolute);
      if (stat.isSymbolicLink()) throw new Error('fixture has a symlink');
      if (stat.isDirectory()) visit(absolute);
      else if (stat.isFile()) files.push(absolute);
      else throw new Error('fixture has a special file');
    }
  }
  visit(root);
  files.sort();
  const hash = crypto.createHash('sha256');
  for (const file of files) {
    const name = Buffer.from(path.relative(root, file).replace(/\\/g, '/'), 'utf8');
    const size = Buffer.alloc(4);
    size.writeInt32LE(name.length, 0);
    hash.update(size);
    hash.update(name);
    hash.update(crypto.createHash('sha256').update(fs.readFileSync(file)).digest());
  }
  return hash.digest('hex');
}

function findNpm() {
  const name = process.platform === 'win32' ? 'npm.cmd' : 'npm';
  for (const directory of (process.env.PATH || '').split(path.delimiter)) {
    const candidate = path.join(directory, name);
    if (fs.existsSync(candidate)) return candidate;
  }
  throw new Error(`${name} is unavailable on PATH`);
}

function npmCliFor(npmPath) {
  if (process.platform !== 'win32') return npmPath;
  const cli = path.join(path.dirname(npmPath), 'node_modules', 'npm', 'bin', 'npm-cli.js');
  if (!fs.existsSync(cli)) throw new Error(`npm CLI is missing at ${cli}`);
  return cli;
}

function runRealNpm(npmPath, args, cwd, env) {
  const result = process.platform === 'win32'
    ? spawnSync(process.execPath, [npmCliFor(npmPath), ...args], { cwd, env, encoding: 'utf8', timeout: 120_000, windowsHide: true })
    : spawnSync(npmPath, args, { cwd, env, encoding: 'utf8', timeout: 120_000, windowsHide: true });
  assert.equal(result.status, 0, JSON.stringify({ args, status: result.status, stderr: result.stderr, stdout: result.stdout }));
  return result.stdout;
}

function createArchive(directory, content = 'fixture archive payload') {
  const archive = path.join(directory, 'update.tgz');
  fs.writeFileSync(archive, content);
  return archive;
}

function makeNpmPackage(directory, version, helpOutput = 'OmniCore fixture help') {
  fs.mkdirSync(path.join(directory, 'bin'), { recursive: true });
  fs.mkdirSync(path.join(directory, 'dist', 'npm'), { recursive: true });
  fs.writeFileSync(path.join(directory, 'package.json'), JSON.stringify({
    name: 'omnicore-cli', version, bin: { omni: 'bin/omni.js' }, files: ['bin/', 'dist/npm/'],
  }, null, 2));
  fs.writeFileSync(path.join(directory, 'bin', 'omni.js'),
    `process.stdout.write(${JSON.stringify(helpOutput)} + '\\n');\n`);
  fs.writeFileSync(path.join(directory, 'dist', 'npm', 'omni.dll'), `fixture-${version}`);
}

function createRealNpmArchive(npmPath, packageDirectory, outputDirectory, env) {
  const output = runRealNpm(npmPath,
    ['pack', packageDirectory, '--ignore-scripts', '--pack-destination', outputDirectory], outputDirectory, env);
  const filename = String(output).split(/\r?\n/).map(line => line.trim()).filter(line => line.endsWith('.tgz')).at(-1);
  assert.ok(filename, `npm pack did not report its archive: ${output}`);
  const archive = path.join(outputDirectory, filename);
  assert.equal(fs.existsSync(archive), true);
  return archive;
}

async function waitForFile(filePath, timeoutMs = 5_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (fs.existsSync(filePath)) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error('timed out waiting for fixture lock process');
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

test('backup fingerprint follows the updater tree digest framing and changes with file bytes', () => {
  const directory = tempDirectory();
  try {
    fs.mkdirSync(path.join(directory, 'sub'), { recursive: true });
    fs.writeFileSync(path.join(directory, 'package.json'), '{"name":"omnicore-cli"}');
    fs.writeFileSync(path.join(directory, 'sub', 'cli.bin'), Buffer.from([0, 1, 2, 255]));
    const expected = referenceFingerprint(directory);
    assert.equal(fingerprintDirectory(directory), expected);
    fs.writeFileSync(path.join(directory, 'sub', 'cli.bin'), Buffer.from([0, 1, 2, 254]));
    assert.notEqual(fingerprintDirectory(directory), expected);
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

test('helper activates with a matching operation, verifies the installed package, and smoke-starts the CLI', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, process.platform === 'win32' ? '%global & prefix%' : 'prefix');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
      expectedSize: fs.statSync(archive).size,
    });
    const fake = fakeNpm(directory, { statePath });
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.equal(result.status, 0, JSON.stringify({ stderr: result.stderr, stdout: result.stdout,
      state: fs.readFileSync(statePath, 'utf8') }));
    const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    assert.equal(state.status, 'Succeeded');
    assert.equal(state.operationId, operationId);
    const captured = JSON.parse(fs.readFileSync(fake.capture, 'utf8'));
    assert.equal(state.helperPid, captured.state.helperPid);
    assert.deepEqual(captured.args.slice(0, 4), ['install', '-g', '--prefix', prefix]);
    assert.ok(captured.args.includes(archive));
    const owner = JSON.parse(captured.lock);
    assert.equal(owner.pid, captured.state.helperPid);
    assert.equal(owner.operation, 'Apply');
    assert.equal(owner.operationId, operationId);
    assert.equal(path.resolve(owner.statePath), path.resolve(statePath));
    assert.equal(Number.isSafeInteger(captured.state.npmPid), true, 'npm wrapper PID must be durable before npm starts');
    assert.equal(fs.existsSync(fake.lockPath), false);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).npmPid, null);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('npm wrapper exits without invoking npm when its parent closes the gate without authorization', async () => {
  const directory = tempDirectory();
  try {
    const statePath = stateFile(directory);
    const fake = fakeNpm(directory, { statePath });
    const request = JSON.stringify({
      npmPath: fake.npmPath,
      args: ['install', '-g'],
      cwd: directory,
      timeoutMs: 10_000,
      nodePath: process.execPath,
    });
    const child = spawn(process.execPath, [helper, '--npm-child', request], {
      cwd: directory,
      windowsHide: true,
      stdio: ['pipe', 'ignore', 'ignore'],
    });
    child.stdin.end();
    const exitCode = await new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', resolve);
    });
    assert.equal(exitCode, 78);
    assert.equal(fs.existsSync(fake.capture), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper rejects stale durable operation identity without invoking npm', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory, 'release');
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, { operationId: 'ffffffffffffffffffffffffffffffff', assetPath: archive, npmPrefix: prefix });
    const fake = fakeNpm(directory, { statePath, captureEnabled: false });
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(result.status, 0);
    assert.equal(fs.existsSync(fake.capture), false);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).operationId,
      'ffffffffffffffffffffffffffffffff');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper stops before activation if staged bytes changed after download verification', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory, 'replaced staged bytes');
    const prefix = path.join(directory, 'prefix');
    const original = Buffer.from('release bytes verified by the downloader');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      expectedSha256: crypto.createHash('sha256').update(original).digest('hex'),
      expectedSize: original.length,
    });
    const fake = fakeNpm(directory, { statePath, captureEnabled: false });
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(result.status, 0);
    assert.equal(fs.existsSync(fake.capture), false);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Failed');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('npm failure after activation becomes Unknown and leaves the prior backup intact', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const backup = path.join(directory, 'backup');
    fs.mkdirSync(backup);
    fs.writeFileSync(path.join(backup, 'package.json'), JSON.stringify({ name: 'omnicore-cli', version: '1.0.0' }));
    const statePath = stateFile(directory, {
      assetPath: archive,
      backupPath: backup,
      npmPrefix: prefix,
      expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
      expectedSize: fs.statSync(archive).size,
    });
    const fake = fakeNpm(directory, { statePath, npmStatus: 1 });
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(result.status, 0);
    const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    assert.equal(state.status, 'Unknown');
    assert.match(state.detail, /npm_outcome_unknown/);
    assert.equal(fs.existsSync(path.join(backup, 'package.json')), true);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('Windows preflight fails before npm when an installed DLL is held open by another process',
  { skip: process.platform !== 'win32', timeout: 15_000 }, async () => {
    const directory = tempDirectory();
    let lockProcess;
    try {
      const archive = createArchive(directory);
      const prefix = path.join(directory, 'prefix');
      const packageDirectory = path.join(prefix, 'node_modules', 'omnicore-cli');
      const dllPath = path.join(packageDirectory, 'dist', 'npm', 'omni.dll');
      fs.mkdirSync(path.dirname(dllPath), { recursive: true });
      fs.writeFileSync(dllPath, 'currently loaded assembly');
      const readyPath = path.join(directory, 'dll-lock-ready');
      const statePath = stateFile(directory, {
        assetPath: archive,
        npmPrefix: prefix,
        expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
        expectedSize: fs.statSync(archive).size,
      });
      const fake = fakeNpm(directory, { statePath, packageDirectory });
      const psQuote = value => `'${String(value).replace(/'/g, "''")}'`;
      const script = `$stream = [System.IO.File]::Open(${psQuote(dllPath)}, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read); [System.IO.File]::WriteAllText(${psQuote(readyPath)}, 'ready'); Start-Sleep -Seconds 30; $stream.Dispose()`;
      const child = spawn('powershell.exe',
        ['-NoProfile', '-NonInteractive', '-Command', script], { windowsHide: true, stdio: 'ignore' });
      lockProcess = child;
      await waitForFile(readyPath);

      const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
        packageDirectory, prefix });
      assert.notEqual(result.status, 0);
      const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
      assert.equal(state.status, 'Failed');
      assert.equal(state.detail, 'active_binary_in_use');
      assert.equal(fs.existsSync(fake.capture), false, 'npm must not run before the preflight succeeds');
    } finally {
      if (lockProcess && typeof lockProcess.kill === 'function') {
        lockProcess.kill();
        await Promise.race([
          new Promise(resolve => lockProcess.once('close', resolve)),
          new Promise(resolve => setTimeout(resolve, 2_000)),
        ]);
      }
      fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    }
  });

test('npm exit zero with the wrong installed version never becomes Succeeded', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
      expectedSize: fs.statSync(archive).size,
    });
    const fake = fakeNpm(directory, { statePath, version: '9.9.9' });
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(result.status, 0);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Unknown');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('npm exit zero with a broken CLI smoke never becomes Succeeded', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
      expectedSize: fs.statSync(archive).size,
    });
    const fake = fakeNpm(directory, { statePath, smokeStatus: 3 });
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(result.status, 0);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Unknown');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('rollback refuses a backup fingerprint mismatch before calling npm', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const backup = path.join(directory, 'backup');
    makeNpmPackage(backup, '1.0.0');
    const actual = fingerprintDirectory(backup);
    const wrong = '0'.repeat(64);
    assert.notEqual(actual, wrong);
    const id = 'abcdefabcdefabcdefabcdefabcdefab';
    const statePath = stateFile(directory, {
      operation: 'Rollback',
      operationId: id,
      backupPath: backup,
      backupSha256: wrong,
      npmPrefix: prefix,
    });
    const fake = fakeNpm(directory, { statePath, captureEnabled: false });
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix, mode: 'rollback', expectedVersion: '1.0.0',
      backupSha256: wrong, operationId: id });
    assert.notEqual(result.status, 0);
    const state = JSON.parse(fs.readFileSync(statePath, 'utf8'));
    assert.equal(state.status, 'Failed');
    assert.equal(state.detail, 'backup_fingerprint_mismatch');
    assert.equal(fs.existsSync(fake.capture), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('real npm apply and rollback stay inside a temporary prefix and verify both package versions', { timeout: 180_000 }, () => {
  const directory = tempDirectory();
  try {
    const npmPath = findNpm();
    const npmConfig = path.join(directory, 'npmrc');
    const npmCache = path.join(directory, 'npm-cache');
    const env = {
      ...process.env,
      NPM_CONFIG_USERCONFIG: npmConfig,
      npm_config_userconfig: npmConfig,
      NPM_CONFIG_CACHE: npmCache,
      npm_config_cache: npmCache,
      NPM_CONFIG_AUDIT: 'false',
      npm_config_audit: 'false',
      NPM_CONFIG_FUND: 'false',
      npm_config_fund: 'false',
    };
    fs.writeFileSync(npmConfig, 'audit=false\nfund=false\nupdate-notifier=false\n');
    fs.mkdirSync(npmCache, { recursive: true });

    const baselinePackage = path.join(directory, 'source-0.1.0');
    const updatedPackage = path.join(directory, 'source-0.2.0');
    makeNpmPackage(baselinePackage, '0.1.0', 'OmniCore 0.1.0 help');
    makeNpmPackage(updatedPackage, '0.2.0', 'OmniCore 0.2.0 help');
    const packageArchiveDirectory = path.join(directory, 'archives');
    fs.mkdirSync(packageArchiveDirectory);
    const baselineArchive = createRealNpmArchive(npmPath, baselinePackage, packageArchiveDirectory, env);
    const updateArchive = createRealNpmArchive(npmPath, updatedPackage, packageArchiveDirectory, env);

    const prefix = path.join(directory, 'prefix & isolated');
    const packageDirectory = process.platform === 'win32'
      ? path.join(prefix, 'node_modules', 'omnicore-cli')
      : path.join(prefix, 'lib', 'node_modules', 'omnicore-cli');
    runRealNpm(npmPath, ['install', '-g', '--prefix', prefix, '--ignore-scripts', '--no-audit', '--no-fund', baselineArchive], directory, env);
    assert.equal(JSON.parse(fs.readFileSync(path.join(packageDirectory, 'package.json'), 'utf8')).version, '0.1.0');

    const backup = path.join(directory, 'backup-0.1.0');
    fs.cpSync(baselinePackage, backup, { recursive: true, errorOnExist: true });
    const backupSha256 = fingerprintDirectory(backup);
    const statePath = stateFile(directory, {
      status: 'Pending',
      operation: 'Apply',
      operationId,
      assetPath: updateArchive,
      backupPath: backup,
      backupSha256,
      npmPrefix: prefix,
      expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(updateArchive)).digest('hex'),
      expectedSize: fs.statSync(updateArchive).size,
    });
    const applied = invokeHelper({ directory, statePath, archive: updateArchive, npmPath,
      packageDirectory, prefix, expectedVersion: '0.2.0', env, timeout: 120_000, operationTimeoutSeconds: 60 });
    assert.equal(applied.status, 0, JSON.stringify({ stderr: applied.stderr, stdout: applied.stdout,
      state: fs.readFileSync(statePath, 'utf8'), installedPaths: fs.existsSync(prefix) ? fs.readdirSync(prefix, { recursive: true }) : [] }));
    assert.equal(JSON.parse(fs.readFileSync(path.join(packageDirectory, 'package.json'), 'utf8')).version, '0.2.0');
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Succeeded');

    const rollbackId = 'fedcbafedcbafedcbafedcbafedcbafe';
    fs.writeFileSync(statePath, JSON.stringify({
      schemaVersion: 1,
      operation: 'Rollback',
      status: 'Pending',
      operationId: rollbackId,
      parentPid: deadPid,
      helperPid: null,
      npmPrefix: prefix,
      assetPath: null,
      backupPath: backup,
      backupSha256,
      expectedSha256: null,
      expectedSize: null,
      updatedAtUtc: new Date().toISOString(),
    }));
    const rolledBack = invokeHelper({ directory, statePath, archive: updateArchive, npmPath,
      packageDirectory, prefix, mode: 'rollback', expectedVersion: '0.1.0',
      backupPath: backup, backupSha256, operationId: rollbackId, env, timeout: 120_000, operationTimeoutSeconds: 60 });
    assert.equal(rolledBack.status, 0, JSON.stringify({ stderr: rolledBack.stderr, stdout: rolledBack.stdout,
      state: fs.readFileSync(statePath, 'utf8') }));
    assert.equal(JSON.parse(fs.readFileSync(path.join(packageDirectory, 'package.json'), 'utf8')).version, '0.1.0');
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Succeeded');
    assert.equal(fingerprintDirectory(backup), backupSha256);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper reclaims an orphaned owner lock only for its matching Pending operation', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
      expectedSize: fs.statSync(archive).size,
    });
    const fake = fakeNpm(directory, { statePath });
    fs.writeFileSync(fake.lockPath, JSON.stringify({ pid: deadPid, operation: 'Apply', operationId, statePath }));
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Succeeded');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper safely reclaims an empty legacy lock only when durable helper and parent PIDs are dead', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, {
      assetPath: archive,
      npmPrefix: prefix,
      helperPid: deadPid,
      expectedSha256: crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex'),
      expectedSize: fs.statSync(archive).size,
    });
    const fake = fakeNpm(directory, { statePath });
    fs.writeFileSync(fake.lockPath, '');
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Succeeded');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('helper marks an orphaned Running operation Unknown and never retries npm', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, {
      status: 'Running',
      helperPid: deadPid,
      assetPath: archive,
      npmPrefix: prefix,
    });
    const fake = fakeNpm(directory, { statePath, captureEnabled: false });
    fs.writeFileSync(fake.lockPath, JSON.stringify({ pid: deadPid, operation: 'Apply', operationId, statePath }));
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(result.status, 0);
    assert.equal(fs.existsSync(fake.capture), false);
    assert.equal(JSON.parse(fs.readFileSync(statePath, 'utf8')).status, 'Unknown');
    assert.equal(fs.existsSync(fake.lockPath), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('a live owner lock and malformed legacy lock remain untouched', () => {
  const directory = tempDirectory();
  try {
    const archive = createArchive(directory);
    const prefix = path.join(directory, 'prefix');
    const statePath = stateFile(directory, { assetPath: archive, npmPrefix: prefix, helperPid: process.pid });
    const fake = fakeNpm(directory, { statePath, captureEnabled: false });
    const owner = { pid: process.pid, operation: 'Apply', operationId, statePath };
    fs.writeFileSync(fake.lockPath, JSON.stringify(owner));
    const result = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(result.status, 0);
    assert.deepEqual(JSON.parse(fs.readFileSync(fake.lockPath, 'utf8')), owner);
    assert.equal(fs.existsSync(fake.capture), false);

    fs.writeFileSync(fake.lockPath, 'not-json');
    const malformed = invokeHelper({ directory, statePath, archive, npmPath: fake.npmPath,
      packageDirectory: fake.packageDirectory, prefix });
    assert.notEqual(malformed.status, 0);
    assert.equal(fs.readFileSync(fake.lockPath, 'utf8'), 'not-json');
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
