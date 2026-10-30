'use strict';

const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
const windowsTerminal = require('./windows-terminal');

const signalExitCodes = {
  SIGHUP: 129,
  SIGINT: 130,
  SIGTERM: 143
};

function run(args = process.argv.slice(2), options = {}) {
  const packageRoot = options.packageRoot ?? path.resolve(__dirname, '..', '..');
  const spawnProcess = options.spawn ?? spawn;
  const fileExists = options.existsSync ?? fs.existsSync;
  const parentProcess = options.process ?? process;
  const stderr = options.stderr ?? process.stderr;
  const dllPath = path.join(packageRoot, 'dist', 'npm', 'omni.dll');
  const newWindow = args.includes('--new-window');
  const stayHere = options.currentTerminal || args.includes(windowsTerminal.marker) || !newWindow;
  args = args.filter(arg => arg !== windowsTerminal.marker && arg !== '--new-window');

  if (!fileExists(dllPath)) {
    stderr.write('Falta la CLI publicada (dist/npm/omni.dll). Reinstala el paquete sin --ignore-scripts para que npm ejecute prepare.\n');
    parentProcess.exitCode = 1;
    return null;
  }

  if (!stayHere && windowsTerminal.eligible(args, parentProcess)) {
    let launch;
    try { launch = windowsTerminal.prepare(packageRoot, parentProcess, options.terminalFs); }
    catch { /* Read-only profile folders retain the current-terminal path. */ }
    if (launch) {
      let settled = false;
      const fallback = () => {
        if (settled) return;
        settled = true;
        run(args, { ...options, currentTerminal: true });
      };
      try {
        // This is the user-facing window; SW_HIDE would leave a live TUI invisible.
        const terminal = spawnProcess(launch.terminal, launch.args, { stdio: 'inherit', windowsHide: false });
        terminal.once('error', fallback);
        terminal.once('exit', code => {
          if (code !== 0) fallback();
          else if (!settled) { settled = true; parentProcess.exitCode = 0; }
        });
        return terminal;
      } catch { fallback(); return null; }
    }
  }

  const startup = require('./startup-updates');
  if (parentProcess.env && (parentProcess === process || options.startupUpdates === true || options.startupOptions)
      && startup.eligible(args) && options.startupUpdates !== false) {
    const completion = startup.runManaged(dllPath, args, {
      spawn: spawnProcess, process: parentProcess, stderr, ...options.startupOptions
    });
    completion.then(code => { parentProcess.exitCode = code; }, () => { parentProcess.exitCode = 1; });
    return completion;
  }

  const child = spawnProcess('dotnet', [dllPath, ...args], {
    stdio: 'inherit',
    windowsHide: true
  });

  const forwardSignal = signal => {
    if (child.exitCode !== null) return;
    try {
      child.kill(signal);
    } catch (error) {
      stderr.write(`No se pudo reenviar ${signal} a OmniCore: ${error.message}\n`);
    }
  };

  const signalHandlers = new Map();
  for (const signal of Object.keys(signalExitCodes)) {
    const handler = () => forwardSignal(signal);
    signalHandlers.set(signal, handler);
    parentProcess.on(signal, handler);
  }

  const removeSignalHandlers = () => {
    for (const [signal, handler] of signalHandlers) {
      parentProcess.removeListener(signal, handler);
    }
  };

  child.once('error', error => {
    removeSignalHandlers();
    if (error.code === 'ENOENT') {
      stderr.write('No se encontró dotnet. Instala el runtime de .NET 10 y vuelve a ejecutar omni.\n');
    } else {
      stderr.write(`No se pudo iniciar OmniCore: ${error.message}\n`);
    }
    parentProcess.exitCode = 1;
  });

  child.once('exit', (code, signal) => {
    removeSignalHandlers();
    parentProcess.exitCode = signal ? (signalExitCodes[signal] ?? 1) : (code ?? 1);
  });

  return child;
}

module.exports = { run };
