// Capture the first real outbound request from each installed harness against a
// loopback fixture. No inference, user configuration or real credential is used.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');
const { normalizePayload } = require('./initial-context-payload.cjs');

const args = process.argv.slice(2);
function option(name, fallback) {
  const at = args.indexOf(name);
  return at < 0 ? fallback : args[at + 1];
}
const repo = path.resolve(__dirname, '..');
const omni = path.resolve(option('--omni', path.join(repo, 'src/OmniCore.Cli/bin/Debug/net10.0/omni.dll')));
const opencode = option('--opencode');
const pi = option('--pi'); // Installed Node CLI entrypoint, not a PowerShell/npm shim.
const claude = option('--claude');
const codex = option('--codex');
if (!opencode || !fs.existsSync(omni) || !fs.existsSync(opencode)) {
  throw new Error('Provide --opencode <installed exe> and a built --omni <omni.dll>.');
}
if (pi && !fs.existsSync(pi)) throw new Error('Provide --pi <installed pi CLI JavaScript entrypoint>.');
for (const cli of [claude, codex]) if (cli && !fs.existsSync(cli)) throw new Error('Native CLI executable not found.');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'omnicore-context-measure-'));
const workspace = path.join(root, 'workspace');
fs.mkdirSync(workspace);
fs.writeFileSync(path.join(workspace, 'README.md'), '# Measurement fixture\nA fresh empty project with one README.\n');
const prompt = 'Examina este proyecto y explica su estructura sin modificar archivos.';
const captures = [];
let activeCase;
const server = http.createServer(async (req, res) => {
  let raw = '';
  for await (const chunk of req) raw += chunk.toString('utf8');
  if (req.url.endsWith('/chat/completions')) {
    const body = JSON.parse(raw);
    captures.push({ case: activeCase, body });
    fs.writeFileSync(path.join(root, 'requests.json'), JSON.stringify(captures, null, 2));
    const base = { id: 'context-fixture', object: 'chat.completion.chunk', created: 1, model: 'context-fixture' };
    if (body.stream) {
      res.writeHead(200, { 'Content-Type': 'text/event-stream' });
      res.end('data: ' + JSON.stringify({ ...base, choices: [{ index: 0, delta: { role: 'assistant', content: 'Fixture complete.' }, finish_reason: null }] })
        + '\n\ndata: ' + JSON.stringify({ ...base, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }], usage: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 } }) + '\n\ndata: [DONE]\n\n');
    } else {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ...base, object: 'chat.completion', choices: [{ index: 0, message: { role: 'assistant', content: 'Fixture complete.' }, finish_reason: 'stop' }], usage: { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 } }));
    }
  } else if (req.url.split('?')[0].endsWith('/messages') || req.url.split('?')[0].endsWith('/responses')) {
    const body = JSON.parse(raw);
    const wireApi = req.url.split('?')[0].endsWith('/messages') ? 'messages' : 'responses';
    captures.push({ case: activeCase, wireApi, body });
    fs.writeFileSync(path.join(root, 'requests.json'), JSON.stringify(captures, null, 2));
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    const event = (type, data) => `event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`;
    if (wireApi === 'messages') {
      res.end(event('message_start', { message: { id: 'msg_fixture', type: 'message', role: 'assistant', model: body.model,
        content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 0, output_tokens: 0 } } })
        + event('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
        + event('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'Fixture complete.' } })
        + event('content_block_stop', { index: 0 })
        + event('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 0 } })
        + event('message_stop', {}));
    } else {
      const item = { id: 'msg_fixture', type: 'message', role: 'assistant', status: 'completed',
        content: [{ type: 'output_text', text: 'Fixture complete.', annotations: [] }] };
      const response = { id: 'resp_fixture', object: 'response', created_at: 1, model: body.model, status: 'completed',
        output: [item], usage: { input_tokens: 0, output_tokens: 0, total_tokens: 0 } };
      res.end(event('response.created', { response: { ...response, status: 'in_progress', output: [] } })
        + event('response.output_item.added', { output_index: 0, item: { ...item, status: 'in_progress', content: [] } })
        + event('response.content_part.added', { item_id: item.id, output_index: 0, content_index: 0,
          part: { type: 'output_text', text: '', annotations: [] } })
        + event('response.output_text.delta', { item_id: item.id, output_index: 0, content_index: 0, delta: 'Fixture complete.' })
        + event('response.output_text.done', { item_id: item.id, output_index: 0, content_index: 0, text: 'Fixture complete.' })
        + event('response.output_item.done', { output_index: 0, item })
        + event('response.completed', { response }));
    }
  } else if (req.url.endsWith('/models')) {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ data: [{ id: 'context-fixture', object: 'model' }] }));
  } else { res.writeHead(404); res.end('{}'); }
});
function run(command, argv, env, cwd = workspace) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, argv, { cwd, env, windowsHide: true });
    // OpenCode also reads redirected stdin; close it so a fresh run does not
    // wait forever for input despite receiving the positional message.
    child.stdin.end();
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    const timer = setTimeout(() => {
      child.kill();
      resolve({ code: 124, stdout, stderr: stderr + '\nFixture process exceeded its 240s initialization/request deadline.' });
    }, 240000);
    child.once('error', reject);
    child.once('close', code => { clearTimeout(timer); resolve({ code, stdout, stderr }); });
  });
}
function stats(text) {
  return { characters: [...text].length, utf8Bytes: Buffer.byteLength(text),
    estimatedTokensBytesOverFour: Math.ceil(Buffer.byteLength(text) / 4) };
}
function summarize(capture) {
  const { body } = capture;
  const { system, user, messages: messageList, toolIds, tools } = normalizePayload(body);
  const schemas = JSON.stringify(tools);
  const messages = JSON.stringify(messageList);
  const bytes = JSON.stringify(body);
  return { case: capture.case, requestCount: captures.filter(c => c.case === capture.case).length,
    system: stats(system), userAndEnvironment: stats(user), toolSchemas: stats(schemas),
    messages: stats(messages), wholeRequest: stats(bytes),
    toolCount: toolIds.length, toolIds,
    requestHash: crypto.createHash('sha256').update(bytes).digest('hex') };
}
(async () => {
  server.listen(0, '127.0.0.1');
  await new Promise(resolve => server.once('listening', resolve));
  const baseURL = `http://127.0.0.1:${server.address().port}/v1`;
  const results = [];
  for (const category of ['ObserveOnly', 'PatchOnly', 'ScopedCoder', 'FullAgent']) {
    const config = path.join(root, 'omni-' + category, 'config');
    const data = path.join(root, 'omni-' + category, 'data');
    fs.mkdirSync(config, { recursive: true });
    fs.mkdirSync(data, { recursive: true });
    fs.writeFileSync(path.join(config, 'providers.yaml'), `providers:\n  measurement:\n    family: OpenAiChatCompatible\n    baseUrl: ${baseURL}\n    auth: none\n    billingMode: Local\n`);
    fs.writeFileSync(path.join(config, 'models.yaml'), 'models:\n  context-fixture:\n    provider: measurement\n    context: 65536\n    maxOutput: 2048\n');
    const env = { ...process.env, OMNICORE_DATA_DIR: data, OMNICORE_CONFIG_DIR: config, OMNI_LOCALE: 'es', OMNI_MODEL: 'context-fixture', OMNI_BASE_URL: baseURL };
    const policy = await run('dotnet', [omni, 'model', 'policy', 'set', 'context-fixture', '--category', category, '--revision', '0'], env);
    activeCase = 'OmniCore/' + category;
    const response = await run('dotnet', [omni, category === 'ObserveOnly' ? 'ask' : 'act', prompt], env);
    results.push({ case: activeCase, policyExit: policy.code, processExit: response.code,
      diagnostic: response.code ? response.stdout.slice(-1000) : null });
    console.error(`${activeCase}: exit=${response.code}, captures=${captures.filter(c => c.case === activeCase).length}`);
  }
  const ocHome = path.join(root, 'opencode-home');
  fs.mkdirSync(ocHome);
  const env = { ...process.env, XDG_CONFIG_HOME: path.join(ocHome, 'config'), XDG_DATA_HOME: path.join(ocHome, 'data'), XDG_CACHE_HOME: path.join(ocHome, 'cache'),
    OPENCODE_TEST_HOME: ocHome, OPENCODE_CONFIG_DIR: path.join(ocHome, 'config'), OPENCODE_DISABLE_AUTOUPDATE: 'true', OPENCODE_DISABLE_MODELS_FETCH: 'true',
    OPENCODE_DISABLE_CLAUDE_CODE: 'true', OPENCODE_DISABLE_EXTERNAL_SKILLS: 'true',
    OPENCODE_DISABLE_DEFAULT_PLUGINS: 'true', OPENCODE_CONFIG_CONTENT: JSON.stringify({
      enabled_providers: ['measurement'], model: 'measurement/context-fixture', small_model: 'measurement/context-fixture',
      provider: { measurement: { npm: '@ai-sdk/openai-compatible', name: 'Measurement fixture', options: { baseURL, apiKey: 'fixture-not-a-secret' }, models: { 'context-fixture': { name: 'Context fixture', limit: { context: 65536, output: 2048 } } } } }, permission: 'ask', share: 'disabled' }) };
  delete env.OPENCODE_CONFIG;
  delete env.OPENCODE_TUI_CONFIG;
  for (const agent of ['plan', 'build']) {
    activeCase = 'OpenCode/' + agent;
    const result = await run(opencode, ['run', '--pure', '--print-logs', '--log-level', 'DEBUG', '--agent', agent, '--format', 'json', '--title', 'Context measurement', prompt], env);
    results.push({ case: activeCase, processExit: result.code, diagnostic: result.code ? result.stderr.slice(-1000) : null });
    console.error(`${activeCase}: exit=${result.code}, captures=${captures.filter(c => c.case === activeCase).length}`);
  }
  const version = await run(opencode, ['--version'], env);
  let piVersion = null;
  if (pi) {
    const piAgentDir = path.join(root, 'pi-agent');
    fs.mkdirSync(piAgentDir);
    fs.writeFileSync(path.join(piAgentDir, 'models.json'), JSON.stringify({ providers: {
      measurement: { baseUrl: baseURL, api: 'openai-completions', apiKey: 'fixture-not-a-secret',
        models: [{ id: 'context-fixture', name: 'Context fixture', reasoning: false, input: ['text'],
          contextWindow: 65536, maxTokens: 2048, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } }] }
    } }));
    const piEnv = { ...process.env };
    for (const name of Object.keys(piEnv)) if (name.startsWith('PI_')) delete piEnv[name];
    Object.assign(piEnv, { PI_CODING_AGENT_DIR: piAgentDir, PI_OFFLINE: '1', PI_SKIP_VERSION_CHECK: '1', PI_TELEMETRY: '0' });
    activeCase = 'Pi/default';
    const result = await run(process.execPath, [pi, '--provider', 'measurement', '--model', 'context-fixture',
      '--thinking', 'off', '--print', '--no-session', '--no-extensions', '--no-mcp', '--no-skills',
      '--no-prompt-templates', '--no-context-files', prompt], piEnv);
    results.push({ case: activeCase, processExit: result.code,
      diagnostic: result.code ? result.stderr.slice(-2000) : null });
    console.error(`${activeCase}: exit=${result.code}, captures=${captures.filter(c => c.case === activeCase).length}`);
    piVersion = (await run(process.execPath, [pi, '--version'], piEnv)).stdout.trim();
  }
  let claudeVersion = null, codexVersion = null;
  function cleanNativeEnv() {
    const clean = { ...process.env };
    for (const name of Object.keys(clean)) if (/^(CLAUDE|ANTHROPIC|CODEX|OPENAI|OTEL)_?/.test(name)) delete clean[name];
    return clean;
  }
  if (claude) {
    const home = path.join(root, 'claude-home'); fs.mkdirSync(home);
    const claudeEnv = { ...cleanNativeEnv(), CLAUDE_CONFIG_DIR: home, ANTHROPIC_BASE_URL: baseURL.replace(/\/v1$/, ''),
      ANTHROPIC_API_KEY: 'fixture-not-a-secret', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
      CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL: '1', DISABLE_TELEMETRY: '1', DISABLE_AUTOUPDATER: '1' };
    activeCase = 'ClaudeCode/default';
    const result = await run(claude, ['--print', '--safe-mode', '--no-session-persistence', '--strict-mcp-config',
      '--mcp-config', '{"mcpServers":{}}', '--setting-sources', '', '--permission-mode', 'manual',
      '--output-format', 'json', prompt], claudeEnv);
    results.push({ case: activeCase, processExit: result.code, diagnostic: result.code ? result.stderr.slice(-2000) + result.stdout.slice(-2000) : null });
    console.error(`${activeCase}: exit=${result.code}, captures=${captures.filter(c => c.case === activeCase).length}`);
    claudeVersion = (await run(claude, ['--version'], claudeEnv)).stdout.trim();
  }
  if (codex) {
    const home = path.join(root, 'codex-home'); fs.mkdirSync(home);
    const codexEnv = { ...cleanNativeEnv(), CODEX_HOME: home };
    activeCase = 'Codex/default';
    const settings = { model_provider: 'measurement', model_context_window: 65536, web_search: 'disabled',
      'model_providers.measurement.name': 'Measurement fixture', 'model_providers.measurement.base_url': baseURL,
      'model_providers.measurement.wire_api': 'responses', 'model_providers.measurement.requires_openai_auth': false,
      'model_providers.measurement.supports_websockets': false, 'model_providers.measurement.request_max_retries': 0,
      'model_providers.measurement.stream_max_retries': 0 };
    const configArgs = Object.entries(settings).flatMap(([name, value]) => ['-c', `${name}=${JSON.stringify(value)}`]);
    const result = await run(codex, ['exec', '--ignore-user-config', '--ignore-rules', '--skip-git-repo-check', '--ephemeral',
      '--sandbox', 'read-only', '--json', ...configArgs, prompt], codexEnv);
    results.push({ case: activeCase, processExit: result.code, diagnostic: result.code ? result.stderr.slice(-2000) + result.stdout.slice(-2000) : null });
    console.error(`${activeCase}: exit=${result.code}, captures=${captures.filter(c => c.case === activeCase).length}`);
    codexVersion = (await run(codex, ['--version'], codexEnv)).stdout.trim();
  }
  const commit = await run('git', ['rev-parse', 'HEAD'], process.env, repo);
  const evidence = { capturedAtUtc: new Date().toISOString(), opencodeVersion: version.stdout.trim(), piVersion, claudeVersion, codexVersion, prompt,
    claudeCliSha256: claude ? crypto.createHash('sha256').update(fs.readFileSync(claude)).digest('hex') : null,
    codexCliSha256: codex ? crypto.createHash('sha256').update(fs.readFileSync(codex)).digest('hex') : null,
    piCliSha256: pi ? crypto.createHash('sha256').update(fs.readFileSync(pi)).digest('hex') : null,
    sourceCommit: commit.stdout.trim(), omniCliSha256: crypto.createHash('sha256').update(fs.readFileSync(omni)).digest('hex'),
    workspace: 'one README, no history, no activated skills/memory, isolated configuration',
    countMethod: 'Text components flatten text-only content arrays, excluding JSON type/text wrappers. UTF-8 bytes and Unicode characters exact; tokens=ceil(bytes/4) estimate, not native tokenizer or provider usage. JSON envelope bytes are not all prompt tokens.',
    root, results, measurements: captures.map(summarize) };
  const out = path.resolve(option('--output', path.join(repo, 'TestResults', 'initial-context-measurement.json')));
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, JSON.stringify(evidence, null, 2));
  fs.writeFileSync(path.join(root, 'requests.json'), JSON.stringify(captures, null, 2));
  console.log(JSON.stringify({ output: out, evidence }, null, 2));
  server.close();
  if (results.some(result => result.processExit !== 0 || result.policyExit != null && result.policyExit !== 0
      || captures.filter(capture => capture.case === result.case).length !== 1))
    throw new Error('Measurement incomplete: expected one request and a successful fixture process per case; evidence preserved.');
})().catch(error => { console.error(error.message); server.close(); process.exitCode = 1; });
