'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');
const { syncMain } = require('./sync-main');

function fixture(t) {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'omnicore-upgrade-test-'));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }));
  function git(cwd, ...args) {
    const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
  }
  const remote = path.join(temp, 'origin.git');
  const source = path.join(temp, 'source');
  const checkout = path.join(temp, 'clone con espacios ñ');
  git(temp, 'init', '--bare', '--initial-branch=main', remote);
  git(temp, 'clone', remote, source);
  function identity(repo) {
    git(repo, 'config', 'user.name', 'Upgrade fixture');
    git(repo, 'config', 'user.email', 'fixture@example.invalid');
    git(repo, 'config', 'commit.gpgsign', 'false');
  }
  identity(source);
  function commit(repo, content) {
    fs.writeFileSync(path.join(repo, 'tracked.txt'), content);
    git(repo, 'add', 'tracked.txt');
    git(repo, 'commit', '-m', 'Fixture revision');
    return git(repo, 'rev-parse', 'HEAD');
  }
  const first = commit(source, 'original');
  git(source, 'push', 'origin', 'main');
  git(temp, 'clone', remote, checkout);
  identity(checkout);
  const messages = [];
  const options = { root: checkout, expectedRemote: remote,
    stdout: { write: value => messages.push(value) }, stderr: { write: value => messages.push(value) } };
  return { temp, source, checkout, remote, git, commit, first, messages, options };
}

test('an upgrade fast-forwards the exact fetched Main and is idempotent', t => {
  const fx = fixture(t);
  const next = fx.commit(fx.source, 'updated from remote');
  fx.git(fx.source, 'push', 'origin', 'main');
  assert.equal(syncMain(fx.options), 0);
  assert.equal(fx.git(fx.checkout, 'rev-parse', 'HEAD'), next);
  assert.equal(fx.git(fx.checkout, 'symbolic-ref', '--short', 'HEAD'), 'main');
  assert.equal(fs.readFileSync(path.join(fx.checkout, 'tracked.txt'), 'utf8'), 'updated from remote');
  assert.ok(fx.messages.some(value => value.includes(next)));
  assert.equal(syncMain(fx.options), 0);
  assert.equal(fx.git(fx.checkout, 'rev-parse', 'HEAD'), next);
});

for (const tracked of [true, false]) {
  test(`an upgrade preserves and rejects ${tracked ? 'modified tracked' : 'untracked'} files before fetching`, t => {
    const fx = fixture(t);
    const file = path.join(fx.checkout, tracked ? 'tracked.txt' : 'new-file.txt');
    fs.writeFileSync(file, 'user changes');
    assert.equal(syncMain(fx.options), 1);
    assert.equal(fx.git(fx.checkout, 'rev-parse', 'HEAD'), fx.first);
    assert.equal(fs.readFileSync(file, 'utf8'), 'user changes');
    assert.equal(fs.existsSync(path.join(fx.checkout, '.git', 'FETCH_HEAD')), false);
  });
}

test('unpublished commits are preserved even when Main could otherwise remain ahead', t => {
  const fx = fixture(t);
  const own = fx.commit(fx.checkout, 'unpublished work');
  assert.equal(syncMain(fx.options), 1);
  assert.equal(fx.git(fx.checkout, 'rev-parse', 'HEAD'), own);
  assert.equal(fs.readFileSync(path.join(fx.checkout, 'tracked.txt'), 'utf8'), 'unpublished work');
  assert.ok(fx.messages.some(value => value.includes('commits locales')));
});

test('another branch is never switched, fetched or merged automatically', t => {
  const fx = fixture(t);
  fx.git(fx.checkout, 'switch', '-c', 'work-in-progress');
  assert.equal(syncMain(fx.options), 1);
  assert.equal(fx.git(fx.checkout, 'symbolic-ref', '--short', 'HEAD'), 'work-in-progress');
  assert.equal(fs.existsSync(path.join(fx.checkout, '.git', 'FETCH_HEAD')), false);
});

test('divergent histories are not merged and both remote and local work remain available', t => {
  const fx = fixture(t);
  const own = fx.commit(fx.checkout, 'local work');
  const upstream = fx.commit(fx.source, 'upstream work');
  fx.git(fx.source, 'push', 'origin', 'main');
  assert.equal(syncMain(fx.options), 1);
  assert.equal(fx.git(fx.checkout, 'rev-parse', 'HEAD'), own);
  assert.equal(fx.git(fx.checkout, 'rev-parse', 'FETCH_HEAD'), upstream);
  assert.equal(fs.readFileSync(path.join(fx.checkout, 'tracked.txt'), 'utf8'), 'local work');
  assert.equal(fx.git(fx.checkout, 'status', '--porcelain=v1'), '');
});

test('a foreign origin is rejected without printing credentials or accessing the network', t => {
  const fx = fixture(t);
  fx.git(fx.checkout, 'remote', 'set-url', 'origin', 'https://fixture:private-value@example.invalid/repo');
  assert.equal(syncMain({ ...fx.options, expectedRemote: undefined }), 1);
  assert.equal(fx.messages.join('').includes('private-value'), false);
  assert.equal(fx.git(fx.checkout, 'rev-parse', 'HEAD'), fx.first);
  assert.equal(fs.existsSync(path.join(fx.checkout, '.git', 'FETCH_HEAD')), false);
});

test('a failed fetch cannot change HEAD or advance to a stale cached revision', t => {
  const fx = fixture(t);
  const missing = path.join(fx.temp, 'missing-origin.git');
  fx.git(fx.checkout, 'remote', 'set-url', 'origin', missing);
  assert.equal(syncMain({ ...fx.options, expectedRemote: missing }), 1);
  assert.equal(fx.git(fx.checkout, 'rev-parse', 'HEAD'), fx.first);
  assert.equal(fs.readFileSync(path.join(fx.checkout, 'tracked.txt'), 'utf8'), 'original');
  assert.ok(fx.messages.some(value => value.includes('no se pudo descargar')));
});
