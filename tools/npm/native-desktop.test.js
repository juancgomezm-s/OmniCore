'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { verifyPublishedDesktop } = require('./native-desktop');

test('Windows distribution must include the native bytes named by its build pin', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'omni-native-package-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, 'native', 'desktop-control'), { recursive: true });
  fs.mkdirSync(path.join(root, 'dist', 'native'), { recursive: true });
  const pin = path.join(root, 'native', 'desktop-control', 'desktop.sha256');
  const binary = path.join(root, 'dist', 'native', 'omnicore-desktop.exe');
  const bytes = Buffer.from('authorized fixture binary');
  fs.writeFileSync(pin, crypto.createHash('sha256').update(bytes).digest('hex'));
  assert.throws(() => verifyPublishedDesktop(root, path.join(root, 'dist'), 'win32'));
  fs.writeFileSync(binary, bytes);
  assert.doesNotThrow(() => verifyPublishedDesktop(root, path.join(root, 'dist'), 'win32'));
  fs.appendFileSync(binary, 'tampered');
  assert.throws(() => verifyPublishedDesktop(root, path.join(root, 'dist'), 'win32'));
});

test('Non-Windows distributions do not require a Windows desktop binary', () => {
  assert.doesNotThrow(() => verifyPublishedDesktop('missing', 'missing', 'linux'));
});
