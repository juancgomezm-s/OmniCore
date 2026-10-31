'use strict';

const path = require('node:path');
const fs = require('node:fs');
const { spawnSync } = require('node:child_process');

const root = fs.realpathSync(path.resolve(__dirname, '..', '..'));
const output = path.resolve(root, 'dist', 'npm');
const nodeMajor = Number(process.versions.node.split('.')[0]);

function isWithin(parent, candidate) {
  const relative = path.relative(parent, candidate);
  return relative !== '' && relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}

function validateOutputPath() {
  if (!isWithin(root, output)) {
    throw new Error(`La carpeta de publicación debe quedar dentro del repositorio: ${output}`);
  }

  for (const candidate of [path.join(root, 'dist'), output]) {
    let details;
    try {
      details = fs.lstatSync(candidate);
    } catch (error) {
      if (error.code === 'ENOENT') continue;
      throw error;
    }

    if (details.isSymbolicLink()) {
      throw new Error(`No se publicará en ${candidate}: la ruta es un enlace simbólico o reparse point.`);
    }
    if (!details.isDirectory()) {
      throw new Error(`No se puede publicar: ${candidate} existe y no es una carpeta.`);
    }

    const resolved = fs.realpathSync(candidate);
    if (!isWithin(root, resolved)) {
      throw new Error(`La carpeta de publicación resuelta queda fuera del repositorio: ${resolved}`);
    }
  }
}

if (nodeMajor < 20) {
  process.stderr.write('OmniCore requiere Node.js 20 o posterior para preparar el paquete.\n');
  process.exit(1);
}

try {
  validateOutputPath();
} catch (error) {
  process.stderr.write(`No se puede preparar el paquete npm: ${error.message}\n`);
  process.exit(1);
}

// Build the fixed Windows component before .NET embeds its current digest.
// Source builds require Rust/MSVC; prebuilt release installs contain this binary.
if (process.platform === 'win32') {
  const native = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    path.join(root, 'native', 'desktop-control', 'build.ps1')],
    { cwd: root, stdio: 'inherit', windowsHide: true });
  if (native.error || native.status !== 0) {
    process.stderr.write('No se pudo compilar el componente de escritorio. Revisa Rust, MSVC y Windows SDK.\n');
    process.exit(native.status ?? 1);
  }
}

fs.rmSync(output, { recursive: true, force: true });

const result = spawnSync('dotnet', [
  'publish',
  path.join(root, 'src', 'OmniCore.Cli', 'OmniCore.Cli.csproj'),
  '--configuration', 'Release',
  '--framework', 'net10.0',
  '--output', output,
  '--property:UseAppHost=false',
  '--no-self-contained'
], {
  cwd: root,
  stdio: 'inherit',
  windowsHide: true
});

if (result.error) {
  if (result.error.code === 'ENOENT') {
    process.stderr.write('No se encontró dotnet. Instala el SDK de .NET 10 y vuelve a ejecutar npm install o npm pack.\n');
  } else {
    process.stderr.write(`No se pudo iniciar dotnet publish: ${result.error.message}\n`);
  }
  process.exit(1);
}

if (result.status !== 0) {
  process.exit(result.status ?? 1);
}

if (!fs.existsSync(path.join(output, 'omni.dll'))) {
  process.stderr.write(`dotnet publish terminó sin generar ${path.join(output, 'omni.dll')}.\n`);
  process.exit(1);
}

require('./native-desktop').verifyPublishedDesktop(root, output);
