'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

function verifyPublishedDesktop(root, output, platform = process.platform) {
  if (platform !== 'win32') return;
  const pin = fs.readFileSync(path.join(root, 'native', 'desktop-control', 'desktop.sha256'), 'utf8').trim();
  const executable = fs.readFileSync(path.join(output, 'native', 'omnicore-desktop.exe'));
  const digest = crypto.createHash('sha256').update(executable).digest('hex');
  if (!/^[a-f0-9]{64}$/.test(pin) || pin !== digest)
    throw new Error('El paquete Windows no contiene el componente de escritorio verificado.');
}
module.exports = { verifyPublishedDesktop };
