'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const profileId = '{ed345336-64cb-452b-bc01-f0b69070d532}';
const marker = '--current-terminal';
const hash = value => crypto.createHash('sha256').update(value).digest('hex');

function eligible(args, parent) {
  const env = parent.env ?? {};
  return parent.platform === 'win32' && parent.stdin?.isTTY === true && parent.stdout?.isTTY === true &&
    (args.length === 0 || (args.length === 1 && args[0] === 'tui')) &&
    env.OMNICORE_CURRENT_TERMINAL !== '1' && !env.SSH_CONNECTION && !env.SSH_TTY && !env.TMUX &&
    env.WT_PROFILE_ID?.toLowerCase() !== profileId && Boolean(env.LOCALAPPDATA);
}

// Windows command-line quoting; no shell or PowerShell participates in this profile.
function quote(value) {
  return '"' + value.replace(/(\\*)"/g, '$1$1\\"').replace(/(\\+)$/, '$1$1') + '"';
}

function prepare(packageRoot, parent, io = fs) {
  const local = parent.env.LOCALAPPDATA;
  const terminal = path.join(local, 'Microsoft', 'WindowsApps', 'wt.exe');
  // App Execution Aliases are reparse points: existsSync may return false even
  // though Windows can launch them. Inspect the alias itself without following it.
  try {
    const entry = io.lstatSync(terminal);
    if (!entry.isFile() && !entry.isSymbolicLink()) return null;
  } catch { return null; }
  const cwd = parent.cwd();
  const launcher = path.join(packageRoot, 'bin', 'omni.js');
  // wt treats semicolons as command separators, even when inherited from a path.
  if ([cwd, launcher, parent.execPath].some(value => value.includes(';'))) return null;
  // Explicit commandline also starts OmniCore when an already-running Terminal
  // has not reloaded a newly registered fragment yet (its current profile is used).
  const args = ['-w', 'new', 'new-tab', '--profile', profileId, '--startingDirectory', cwd,
    parent.execPath, launcher, marker, 'tui'];
  const directory = path.join(local, 'Microsoft', 'Windows Terminal', 'Fragments', 'OmniCore');
  const fragment = path.join(directory, 'omnicore.json');
  const receipt = path.join(local, 'OmniCore', 'terminal-profile.sha256');
  const commandline = [parent.execPath, launcher, marker, 'tui'].map(quote).join(' ');
  const content = JSON.stringify({ profiles: [{ guid: profileId, name: 'OmniCore', commandline,
    antialiasingMode: 'cleartype' }] }, null, 2) + '\n';
  if (io.existsSync(fragment)) {
    const existing = io.readFileSync(fragment, 'utf8');
    if (existing !== content) {
      if (!io.existsSync(receipt) || io.readFileSync(receipt, 'utf8').trim() !== hash(existing)) return null;
    } else return { terminal, args };
  }
  io.mkdirSync(directory, { recursive: true });
  io.mkdirSync(path.dirname(receipt), { recursive: true });
  io.writeFileSync(fragment, content, 'utf8');
  io.writeFileSync(receipt, hash(content), 'utf8');
  return { terminal, args };
}

module.exports = { eligible, prepare, profileId, marker, quote };
