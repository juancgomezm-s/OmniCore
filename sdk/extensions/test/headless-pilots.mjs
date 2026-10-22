// Acceptance-only independent client. Its private bootstrap never enters an extension process.
import { spawn, execFileSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { randomBytes, randomUUID, createHash, createHmac } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
const [cli, workspace, pilots, contextPackage] = process.argv.slice(2);
const clients = [];
class Client {
  constructor(directory) {
    this.directory = directory; this.key = randomBytes(32); this.waiting = new Map();
    this.child = spawn('dotnet', [cli, 'host', '--stdio'], { cwd: directory, stdio: ['pipe', 'pipe', 'inherit'],
      env: { ...process.env, OMNICORE_TRUSTED_ACTION_BOOTSTRAP: this.key.toString('hex') } });
    this.lines = createInterface({ input: this.child.stdout });
    this.lines.on('line', line => { const envelope = JSON.parse(line); this.waiting.get(envelope.messageId)?.(envelope); });
    this.child.on('exit', () => { for (const waiting of this.waiting.values()) waiting({ payload: { error: 'host_exited' } }); });
    clients.push(this);
  }
  async connect() { this.nonce = (await this.exchange('hello', { supportedVersions: ['1.0'] })).trustedActionNonce; assert.ok(this.nonce); return this; }
  async exchange(messageType, payload) {
    const messageId = randomUUID();
    const response = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.waiting.delete(messageId); reject(new Error('host_timeout')); }, 60000);
      this.waiting.set(messageId, value => { clearTimeout(timer); this.waiting.delete(messageId); resolve(value); });
      this.child.stdin.write(JSON.stringify({ version: '1.0', messageType, messageId, payload }) + '\n');
    });
    if (response.payload.error) throw new Error(JSON.stringify(response.payload));
    return response.payload;
  }
  async query(name) {
    const response = await this.exchange('command', { operation: 'query', name });
    assert.equal(typeof response.json, 'string', JSON.stringify({ query: name, response }));
    return JSON.parse(response.json);
  }
  async human(payload, expectedStatus = 'ok') {
    const scope = await this.query('humanActionScope');
    if (payload.cmd === 'session.input') payload.expectedRevision = scope.revision ?? null;
    const command = { version: '1.0', messageType: 'command', messageId: randomUUID(), payload };
    const digest = value => createHash('sha256').update(value).digest('hex').toUpperCase();
    let canonical = path.resolve(this.directory).replaceAll('\\', '/').replace(/\/$/, '');
    if (process.platform === 'win32') canonical = canonical.toLowerCase();
    const signed = ['omnicore-user-action-v1', this.nonce, command.messageId, digest(JSON.stringify(payload)), digest(canonical), scope.sessionId ?? '-', scope.runId ?? '-'].join('\n');
    const signature = createHmac('sha256', this.key).update(signed).digest('hex').toUpperCase();
    const response = await this.exchange('command', { operation: 'command', command,
      actionProof: { nonce: this.nonce, expectedSessionId: scope.sessionId, expectedRunId: scope.runId, signature } });
    assert.equal(response.ack.status, expectedStatus, JSON.stringify(response));
    return command.messageId;
  }
  view(extensionId, viewId) { return this.query('extensions.view:' + encodeURIComponent(JSON.stringify({ extensionId, viewId }))); }
  async close() {
    this.key.fill(0); this.child.stdin.end();
    await Promise.race([new Promise(resolve => this.child.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 3000))]);
    if (this.child.exitCode == null) this.child.kill(); this.lines.close();
  }
}
try {
  const core = await new Client(workspace).connect();
  for (const [folder, id] of [['filetree', 'omnicore.filetree'], ['agent-flow', 'omnicore.agent-flow'], ['time-machine', 'omnicore.time-machine']]) {
    await core.human({ cmd: 'extensions.package', action: 'install', source: path.join(pilots, folder) });
    const state = (await core.query('extensions.packages')).find(item => item.id === id);
    await core.human({ cmd: 'extensions.package', action: 'enable', extensionId: id,
      expectedManifestSha256: state.manifestSha256, expectedPackageSha256: state.packageSha256 });
  }
  await core.human({ cmd: 'session.input', text: 'Keep this original conversation', mode: 'plan' });
  await core.human({ cmd: 'extensions.package', action: 'install', source: contextPackage });
  const source = (await core.query('extensions.packages')).find(item => item.id === 'test.files');
  const identity = { extensionId: source.id, expectedManifestSha256: source.manifestSha256, expectedPackageSha256: source.packageSha256 };
  await core.human({ cmd: 'extensions.package', action: 'enable', trustProcess: true, ...identity });
  assert.deepEqual(await core.query('extensions.context'), []);
  const previewCommand = { cmd: 'extensions.package', action: 'context-preview', contributor: 'notes', ...identity };
  const unsigned = await core.exchange('command', { operation: 'command', command: { version: '1.0', messageType: 'command', messageId: randomUUID(), payload: previewCommand } });
  assert.equal(unsigned.ack.status, 'error');
  await core.human(previewCommand);
  const [preview] = await core.query('extensions.context'); assert.equal(preview.approved, false);
  const approval = { cmd: 'extensions.package', action: 'context-approve', previewId: preview.id, contentSha256: preview.contentSha256, ...identity };
  await core.human({ ...approval, contentSha256: '0'.repeat(64) }, 'error');
  await core.human(approval); assert.equal((await core.query('extensions.context'))[0].approved, true);
  await core.human({ cmd: 'extensions.package', action: 'context-revoke', ...identity });
  assert.deepEqual(await core.query('extensions.context'), []);
  await core.human(approval, 'error');
  await core.human(previewCommand);
  const [approvedForOriginal] = await core.query('extensions.context');
  await core.human({ ...approval, previewId: approvedForOriginal.id, contentSha256: approvedForOriginal.contentSha256 });
  writeFileSync(path.join(workspace, 'captured.txt'), 'checkpoint version');
  await core.human({ cmd: 'session.input', text: 'Checkpoint cut here', mode: 'plan' });
  const originalSession = (await core.query('humanActionScope')).sessionId;
  const files = await core.view('omnicore.filetree', 'files');
  assert.equal(files.state, 'ready'); assert.ok(files.items.some(item => item.id === 'captured.txt'));
  const agents = await core.view('omnicore.agent-flow', 'agents');
  assert.equal(agents.state, 'ready'); assert.ok(agents.items.length > 0); assert.ok(agents.items.some(item => item.cells?.laneState));
  const history = await core.view('omnicore.time-machine', 'history');
  const cut = history.items.filter(item => item.data?.kind === 'instruction' && item.data.checkpoint?.snapshot).at(-1);
  assert.ok(cut); assert.ok(history.actions.some(action => action.id === 'alternative' && action.itemId === cut.id));
  writeFileSync(path.join(workspace, 'captured.txt'), 'original remains dirty');
  const gitState = () => execFileSync('git', ['status', '--porcelain=v1'], { cwd: workspace, encoding: 'utf8' });
  const before = gitState();
  const commandId = await core.human({ cmd: 'conversation.alternative', checkpointId: cut.data.checkpoint.id, instruction: 'Alternative instruction' });
  const result = await core.query('workspace.panel:' + encodeURIComponent(JSON.stringify({ kind: 'alternative', commandId })));
  const alternative = result.alternative; assert.equal(alternative.state, 'ready');
  assert.equal(readFileSync(path.join(alternative.workspace, 'captured.txt'), 'utf8'), 'checkpoint version');
  assert.equal(readFileSync(path.join(workspace, 'captured.txt'), 'utf8'), 'original remains dirty'); assert.equal(gitState(), before);
  const derived = await new Client(alternative.workspace).connect();
  assert.deepEqual(await derived.query('extensions.context'), []); // An alternative never inherits the parent's approval.
  const timeline = await derived.query('workspace.panel:' + encodeURIComponent(JSON.stringify({ kind: 'timeline' })));
  assert.equal(timeline.sessionId, alternative.sessionId); assert.equal(timeline.origin.sourceSessionId, originalSession);
  const replay = await derived.exchange('command', { operation: 'subscribe', fromSequence: 1 });
  const serialized = JSON.stringify(replay.events);
  assert.ok(serialized.includes('Keep this original conversation')); assert.ok(serialized.includes('Alternative instruction'));
  assert.equal((await core.query('humanActionScope')).sessionId, originalSession);
  assert.equal((await core.query('extensions.context'))[0].approved, true);
  const reopenedOriginal = await new Client(workspace).connect();
  assert.deepEqual(await reopenedOriginal.query('extensions.context'), []);
  console.log(JSON.stringify({ contextAuthorization: true, filetree: true, agentFlow: true, timeMachine: true, conversationRestored: true, filesRestored: true, originalUntouched: true }));
} finally { await Promise.all(clients.map(client => client.close())); }
