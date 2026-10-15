'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

function install(args = process.argv.slice(2), options = {}) {
  const root = options.root ?? path.resolve(__dirname, '..', '..');
  const env = options.env ?? process.env;
  const platform = options.platform ?? process.platform;
  const execute = options.spawnSync ?? spawnSync;
  const stderr = options.stderr ?? process.stderr;
  const stdout = options.stdout ?? process.stdout;
  let prefix = platform === 'linux' ? path.join(options.home ?? os.homedir(), '.local') : null;
  if (args.length) {
    if (args.length !== 2 || args[0] !== '--prefix' || !args[1].trim()) {
      stderr.write('Uso: npm run install:cli -- [--prefix <carpeta>]. El mismo comando actualiza la instalación.\n');
      return 2;
    }
    prefix = path.resolve(args[1]);
  }
  if (!env.npm_execpath || !fs.existsSync(env.npm_execpath)) {
    stderr.write('Ejecuta este instalador mediante npm run install:cli.\n');
    return 1;
  }

  function run(executable, commandArgs) {
    const result = execute(executable, commandArgs, { cwd: root, stdio: 'inherit', windowsHide: true });
    if (result.error) {
      stderr.write(`No se pudo ejecutar ${path.basename(executable)}: ${result.error.message}\n`);
      return 1;
    }
    if (result.signal) {
      stderr.write(`Instalación interrumpida por ${result.signal}.\n`);
      return 1;
    }
    return result.status ?? 1;
  }

  // Compile explicitly: an npm policy must never silently skip prepare and install old output.
  let status = run(process.execPath, [path.join(root, 'tools', 'npm', 'prepare.js')]);
  if (status !== 0) return status;
  status = run('dotnet', [path.join(root, 'dist', 'npm', 'omni.dll'), '--help']);
  if (status !== 0) return status;

  const staging = fs.mkdtempSync(path.join(options.temp ?? os.tmpdir(), 'omnicore-install-'));
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
  const archive = path.join(staging, `${manifest.name}-${manifest.version}.tgz`);
  // The archive already contains the validated CLI. It needs no installation lifecycle scripts.
  status = run(process.execPath, [env.npm_execpath, 'pack', '--ignore-scripts', '--pack-destination', staging]);
  if (status !== 0) return status;
  if (!fs.existsSync(archive)) {
    stderr.write(`npm pack no generó el paquete esperado: ${archive}\n`);
    return 1;
  }
  const npmArgs = [env.npm_execpath, 'install', '--global', '--ignore-scripts', '--no-audit', '--no-fund'];
  if (prefix !== null) npmArgs.push('--prefix', prefix);
  npmArgs.push(archive);
  status = run(process.execPath, npmArgs);
  if (status !== 0) return status;
  stdout.write(`OmniCore instalado/actualizado. Paquete conservado en: ${archive}\n`);
  if (prefix !== null) {
    const commandDir = platform === 'win32' ? prefix : path.join(prefix, 'bin');
    stdout.write(`Carpeta del comando omni (debe estar en PATH): ${commandDir}\n`);
  }
  stdout.write('Ejecuta omni --help para comprobarlo y omni para abrir la interfaz.\n');
  return 0;
}

if (require.main === module) process.exitCode = install();
module.exports = { install };
