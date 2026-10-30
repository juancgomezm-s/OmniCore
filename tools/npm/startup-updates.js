'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { randomUUID } = require('node:crypto');

const restartCode = 74;
const blockedCode = 75;
const signalCodes = { SIGHUP: 129, SIGINT: 130, SIGTERM: 143 };

function eligible(args) {
  return !args.some(arg => ['--help', '-h', '--version'].includes(arg))
    && !['update', 'help', 'version', 'doctor', 'status'].includes(args[0]);
}

// A download/verifier does not need provider tokens, GitHub credentials or the
// authenticated user-action bootstrap inherited by the foreground Host.
function backgroundEnvironment(environment) {
  const allowed = new Set(['PATH', 'PATHEXT', 'SYSTEMROOT', 'WINDIR', 'COMSPEC', 'HOME',
    'USERPROFILE', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP', 'TMPDIR', 'DOTNET_ROOT',
    'DOTNET_ROOT_X64', 'OMNICORE_DATA_DIR', 'OMNICORE_CONFIG_DIR', 'OMNI_LOCALE',
    'XDG_DATA_HOME', 'XDG_CONFIG_HOME', 'LANG', 'LC_ALL']);
  return Object.fromEntries(Object.entries(environment).filter(([key]) => allowed.has(key.toUpperCase())));
}

function rpc(dll, args, options) {
  return new Promise((resolve, reject) => {
    const child = options.spawn('dotnet', [dll, 'update', 'startup', ...args], {
      windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'], env: options.backgroundEnv
    });
    let output = ''; let settled = false;
    const finish = (error, result) => {
      if (settled) return;
      settled = true; clearTimeout(deadline);
      error ? reject(error) : resolve(result);
    };
    const deadline = setTimeout(() => {
      child.kill(); finish(new Error('update_startup_timeout'));
    }, 125000);
    child.stdout.on('data', bytes => {
      output += bytes.toString('utf8');
      if (Buffer.byteLength(output) > 256 * 1024) {
        child.kill(); finish(new Error('update_startup_output_invalid'));
      }
    });
    child.once('error', error => finish(error));
    child.once('exit', code => {
      try {
        if (code !== 0) throw new Error('update_startup_failed');
        const result = JSON.parse(output);
        if (!result || !['continue', 'wait', 'blocked'].includes(result.action)
            || typeof result.dataDirectory !== 'string' || typeof result.statePath !== 'string'
            || !path.isAbsolute(result.dataDirectory)
            || path.resolve(result.statePath) !== path.join(path.resolve(result.dataDirectory), 'updates', 'state.json'))
          throw new Error('update_startup_output_invalid');
        finish(null, result);
      } catch (error) { finish(error); }
    });
  });
}

function launchChild(dll, args, options, environment, cwd) {
  return new Promise(resolve => {
    let child;
    try { child = options.spawn('dotnet', [dll, ...args], {
      stdio: 'inherit', windowsHide: true, env: environment, cwd
    }); } catch { resolve(1); return; }
    const handlers = new Map();
    for (const signal of Object.keys(signalCodes)) {
      const handler = () => { try { child.kill(signal); } catch { /* Already exited. */ } };
      handlers.set(signal, handler); options.process.on(signal, handler);
    }
    const done = code => {
      for (const [signal, handler] of handlers) options.process.removeListener(signal, handler);
      resolve(code);
    };
    child.once('error', error => {
      options.stderr.write(error.code === 'ENOENT'
        ? 'No se encontró dotnet. Instala el runtime de .NET 10.\n'
        : 'No se pudo iniciar OmniCore.\n'); done(1);
    });
    child.once('exit', (code, signal) => done(signal ? (signalCodes[signal] ?? 1) : (code ?? 1)));
  });
}

function readOperation(file, readFile = fs.readFileSync) {
  if (readFile === fs.readFileSync) {
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 256 * 1024)
      throw new Error('update_state_invalid');
  }
  const bytes = readFile(file);
  if (bytes.length > 256 * 1024) throw new Error('update_state_invalid');
  return JSON.parse(bytes.toString('utf8'));
}

async function waitForOperation(launch, options) {
  if (!/^[a-f0-9]{32}$/i.test(launch.operationId ?? '')) throw new Error('update_operation_unknown');
  const until = options.now() + 16 * 60 * 1000;
  while (options.now() < until) {
    const operation = options.readOperation(launch.statePath);
    if (operation.schemaVersion !== 1 || operation.operationId !== launch.operationId)
      throw new Error('update_operation_unknown');
    if (operation.status === 'Succeeded') return;
    if (!['Pending', 'Running'].includes(operation.status)) throw new Error('update_operation_unknown');
    // Observe only. A stopped helper is never relaunched or retried here.
    await options.sleep(300);
  }
  throw new Error('update_operation_timeout');
}

function showRecovery(launch, stderr) {
  const command = launch.recoveryCommand;
  if (typeof command === 'string' && command.length <= 16384 && !/[\x00-\x1f\x7f]/.test(command))
    stderr.write(`Recuperación manual:\n${command}\n`);
}

async function runManaged(dll, args, supplied = {}) {
  const options = { spawn, process, stderr: process.stderr, now: Date.now,
    sleep: ms => new Promise(resolve => setTimeout(resolve, ms)), readOperation,
    ...supplied };
  const environment = options.process.env ?? process.env;
  options.backgroundEnv = backgroundEnvironment(environment);
  const launchId = (options.launchId ?? randomUUID()).replaceAll('-', '');
  const call = options.rpc ?? ((values) => rpc(dll, values, options));
  const foreground = options.launchChild ?? ((env, cwd) => launchChild(dll, args, options, env, cwd));
  let restart = false; let backgroundStarted = false; let barriers = 0; let restarts = 0; let waitingShown = false;
  let cwd = options.process.cwd(); let resumeSession;
  for (;;) {
    let launch;
    try { launch = await call(['launch', launchId, ...(restart ? ['restart'] : [])]); }
    catch { launch = { action: 'continue', prepareInBackground: false }; }
    if (restart) {
      if (launch.workspace) cwd = launch.workspace;
      if (launch.sessionId) resumeSession = launch.sessionId;
    }
    if (launch.action === 'blocked') {
      options.stderr.write('La actualización necesita revisión: omni update status.\n');
      showRecovery(launch, options.stderr); return blockedCode;
    }
    if (launch.action === 'wait') {
      if (!waitingShown) { options.stderr.write('Aplicando la actualización verificada…\n'); waitingShown = true; }
      try { await waitForOperation(launch, options); }
      catch {
        options.stderr.write('No se confirmó la actualización. Ejecuta omni update status para revisar o recuperar.\n');
        showRecovery(launch, options.stderr); return blockedCode;
      }
      // The completed helper is classified by the new CLI. Never repeat apply.
      continue;
    }
    if (launch.prepareInBackground && !backgroundStarted) {
      backgroundStarted = true;
      try {
        const prepare = options.spawn('dotnet', [dll, 'update', 'startup', 'prepare'], {
          detached: true, windowsHide: true, stdio: 'ignore', env: options.backgroundEnv
        });
        prepare.once('error', () => {}); prepare.unref();
      } catch { /* Offline/read-only preparation does not interrupt work. */ }
    }
    const code = await foreground({ ...environment, OMNICORE_STARTUP_MANAGED: '1',
      OMNICORE_STARTUP_LAUNCH_ID: launchId,
      ...(resumeSession ? { OMNICORE_RESTART_SESSION: resumeSession } : {}) }, cwd);
    if (code === restartCode && restarts++ === 0) { restart = true; continue; }
    if (code === blockedCode && barriers++ < 2) { await options.sleep(300); continue; }
    return code;
  }
}

module.exports = { eligible, backgroundEnvironment, runManaged, readOperation, waitForOperation };
