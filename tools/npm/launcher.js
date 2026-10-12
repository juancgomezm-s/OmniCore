'use strict';

const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');

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

  if (!fileExists(dllPath)) {
    stderr.write('Falta la CLI publicada (dist/npm/omni.dll). Reinstala el paquete sin --ignore-scripts para que npm ejecute prepare.\n');
    parentProcess.exitCode = 1;
    return null;
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
