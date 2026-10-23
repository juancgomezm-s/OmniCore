'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

// Development-channel upgrade. Release discovery, automatic updates and rollback
// remain separate M10 work; this only fast-forwards a clean official Main checkout.
function syncMain(options = {}) {
  const root = fs.realpathSync(options.root ?? path.resolve(__dirname, '..', '..'));
  const execute = options.spawnSync ?? spawnSync;
  const stderr = options.stderr ?? process.stderr;
  const stdout = options.stdout ?? process.stdout;
  const expectedRemote = options.expectedRemote ?? 'https://github.com/juancgomezm-s/OmniCore.git';
  function fail(message) {
    stderr.write(`No se actualizó el checkout: ${message}\n`);
    return 1;
  }
  function git(args) {
    return execute('git', args, { cwd: root, encoding: 'utf8', windowsHide: true });
  }
  function ok(result) { return !result.error && !result.signal && result.status === 0; }
  function officialRemote(value) {
    if (expectedRemote !== 'https://github.com/juancgomezm-s/OmniCore.git') return value === expectedRemote;
    return /^(?:https:\/\/github\.com\/|git@github\.com:)juancgomezm-s\/OmniCore(?:\.git)?$/i.test(value);
  }

  const top = git(['rev-parse', '--show-toplevel']);
  if (!ok(top)) return fail('no se pudo identificar el repositorio Git.');
  if (fs.realpathSync(top.stdout.trim()) !== root) return fail('ejecuta el instalador desde su repositorio.');
  const branch = git(['symbolic-ref', '--quiet', '--short', 'HEAD']);
  if (!ok(branch) || branch.stdout.trim() !== 'main') return fail('requiere un clon en la rama main.');
  const status = git(['status', '--porcelain=v1', '--untracked-files=normal']);
  if (!ok(status)) return fail('no se pudo comprobar el estado del checkout.');
  if (status.stdout.trim()) return fail('hay cambios locales o archivos sin seguimiento; consérvalos antes de actualizar.');
  const remote = git(['remote', 'get-url', 'origin']);
  if (!ok(remote) || !officialRemote(remote.stdout.trim())) return fail('origin debe apuntar a juancgomezm-s/OmniCore en GitHub.');

  const fetched = git(['fetch', '--no-tags', 'origin', 'refs/heads/main']);
  if (!ok(fetched)) return fail('no se pudo descargar Main desde GitHub; la instalación no se inició.');
  const revision = git(['rev-parse', 'FETCH_HEAD']);
  if (!ok(revision) || !/^(?:[a-f0-9]{40}|[a-f0-9]{64})$/i.test(revision.stdout.trim()))
    return fail('Git no devolvió una revisión válida de Main.');
  const commit = revision.stdout.trim();
  if (!ok(git(['merge-base', '--is-ancestor', 'HEAD', commit])))
    return fail('Main contiene commits locales sin publicar o ha divergido; no se hará reset, stash ni merge automático.');
  const stillMain = git(['symbolic-ref', '--quiet', '--short', 'HEAD']);
  if (!ok(stillMain) || stillMain.stdout.trim() !== 'main') return fail('la rama cambió durante la descarga.');
  const merged = git(['merge', '--ff-only', '--no-edit', commit]);
  if (!ok(merged)) return fail('no se pudo avanzar Main mediante fast-forward.');
  const installedSource = git(['rev-parse', 'HEAD']);
  if (!ok(installedSource) || installedSource.stdout.trim() !== commit)
    return fail('la revisión resultante no coincide con la descargada.');
  const finalStatus = git(['status', '--porcelain=v1', '--untracked-files=normal']);
  if (!ok(finalStatus) || finalStatus.stdout.trim()) return fail('el checkout cambió durante la actualización; la instalación no se inició.');
  stdout.write(`GitHub Main preparado para instalar: ${commit}\n`);
  return 0;
}

if (require.main === module) {
  try { process.exitCode = syncMain(); }
  catch { process.stderr.write('No se pudo comprobar o actualizar el checkout; la instalación no se inició.\n'); process.exitCode = 1; }
}
module.exports = { syncMain };
