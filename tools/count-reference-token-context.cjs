// Count captured initial request context with a pinned OpenAI-family reference tokenizer.
// This reads existing captures only; it never launches a harness or calls a provider.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { normalizePayload } = require('./initial-context-payload.cjs');

const args = process.argv.slice(2);
function option(name, fallback) {
  const at = args.indexOf(name);
  return at < 0 ? fallback : args[at + 1];
}

const repo = path.resolve(__dirname, '..');
const measurementPath = path.resolve(option('--measurement', path.join(repo, 'TestResults', 'initial-context-measurement.json')));
if (!fs.existsSync(measurementPath)) throw new Error(`Measurement report not found: ${measurementPath}`);
const measurement = JSON.parse(fs.readFileSync(measurementPath, 'utf8'));
const capturesPath = path.resolve(option('--captures', path.join(measurement.root, 'requests.json')));
if (!fs.existsSync(capturesPath)) throw new Error(`Request captures not found: ${capturesPath}`);
const tokenizerPackagePath = path.resolve(option('--tokenizer-package', ''));
if (!tokenizerPackagePath || !fs.existsSync(tokenizerPackagePath))
  throw new Error('Provide --tokenizer-package <gpt-tokenizer package directory>.');

const packageJson = JSON.parse(fs.readFileSync(path.join(tokenizerPackagePath, 'package.json'), 'utf8'));
if (packageJson.name !== 'gpt-tokenizer' || packageJson.version !== '4.0.0')
  throw new Error(`Expected gpt-tokenizer@4.0.0, found ${packageJson.name}@${packageJson.version}.`);
const modelModule = path.join(tokenizerPackagePath, 'cjs', 'model', 'gpt-5.6-luna.js');
const tokenizer = require(modelModule);
if (typeof tokenizer.encode !== 'function') throw new Error('Pinned tokenizer module did not export encode().');

const prompt = measurement.prompt;
const captures = JSON.parse(fs.readFileSync(capturesPath, 'utf8'));
const results = new Map((measurement.measurements || []).map(item => [item.case, item]));
const outputPath = path.resolve(option('--output', path.join(repo, 'TestResults', 'initial-context-reference-token-counts.json')));

function sha256(filePath) {
  return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
}
function stats(text) {
  const bytes = Buffer.byteLength(text, 'utf8');
  return {
    characters: [...text].length,
    utf8Bytes: bytes,
    referenceTokens: tokenizer.encode(text).length,
    estimatedTokensBytesOverFour: Math.ceil(bytes / 4),
  };
}

const caseResults = captures.map(capture => {
  const summary = results.get(capture.case);
  if (!summary) throw new Error(`No measurement summary for ${capture.case}.`);
  const { body } = capture;
  const { system: systemOriginal, user: userEnvironmentOriginal, messages, tools, toolIds } = normalizePayload(body);
  // Remove only the original user message. If the harness echoes the objective
  // into its system/context projection, that copy really is injected overhead.
  const removedPromptOccurrences = userEnvironmentOriginal.includes(prompt) ? 1 : 0;
  if (removedPromptOccurrences !== 1) throw new Error(`Original user prompt not found in ${capture.case}.`);
  const systemWithoutPrompt = systemOriginal;
  const userEnvironmentWithoutPrompt = userEnvironmentOriginal.replace(prompt, '');
  const toolSchemas = JSON.stringify(tools);
  const contentAndSchemasWithoutPrompt = [systemWithoutPrompt, userEnvironmentWithoutPrompt, toolSchemas]
    .filter(text => text.length > 0).join('\n');
  const transportJson = JSON.stringify(body);
  const result = {
    case: capture.case,
    model: body.model,
    wireApi: capture.wireApi || 'chat_completions',
    requestCount: summary.requestCount,
    toolCount: toolIds.length,
    removedPromptOccurrences,
    system: stats(systemOriginal),
    userAndEnvironment: stats(userEnvironmentOriginal),
    toolSchemas: stats(toolSchemas),
    contentAndSchemasSansOriginalUserMessage: stats(contentAndSchemasWithoutPrompt),
    reconstructedCompactRequestJson: {
      utf8Bytes: Buffer.byteLength(transportJson, 'utf8'),
      referenceTokensIfTreatedAsPlainText: tokenizer.encode(transportJson).length,
      note: 'Transport serialization measurement; this is not a model prompt-token count.',
    },
    compactMessagesJsonUtf8Bytes: Buffer.byteLength(JSON.stringify(messages), 'utf8'),
    requestHashFromMeasurement: summary.requestHash,
    canonicalBodyHash: crypto.createHash('sha256').update(transportJson).digest('hex'),
  };
  if (result.reconstructedCompactRequestJson.utf8Bytes !== summary.wholeRequest.utf8Bytes)
    throw new Error(`${capture.case}: compact request byte count differs from original measurement.`);
  return result;
});

const evidence = {
  capturedAtUtc: measurement.capturedAtUtc,
  sourceSnapshotCommit: measurement.sourceCommit || '29c2c26',
  omniCliSha256: measurement.omniCliSha256 || null,
  opencodeVersion: measurement.opencodeVersion,
  piVersion: measurement.piVersion || null,
  piCliSha256: measurement.piCliSha256 || null,
  claudeVersion: measurement.claudeVersion || null,
  claudeCliSha256: measurement.claudeCliSha256 || null,
  codexVersion: measurement.codexVersion || null,
  codexCliSha256: measurement.codexCliSha256 || null,
  prompt: measurement.prompt,
  workspace: measurement.workspace,
  capturesPath,
  measurementPath,
  capturesSha256: sha256(capturesPath),
  measurementSha256: sha256(measurementPath),
  tokenizer: {
    package: packageJson.name,
    version: packageJson.version,
    modelModule: 'gpt-tokenizer/model/gpt-5.6-luna',
    encoding: 'o200k_base',
    license: packageJson.license,
    method: 'encode(text).length over each UTF-8-decoded text component; the joined sans-prompt aggregate is tokenized as one string.',
  },
  method: {
    textBlocks: 'Read string content directly; flatten text-only content arrays by joining block.text with newlines. Exclude JSON type/text wrappers. Reject unsupported non-text content.',
    catalogs: 'Include body.tools and Responses input.additional_tools catalogs; count callable namespace leaves, while retaining namespace descriptions in the schema reference text.',
    instructions: 'Combine body.system, body.instructions and system/developer message text. Developer permissions and environment instructions are harness overhead, not the original user prompt.',
    promptRemoval: 'Remove the first exact occurrence from user message text only. Preserve any system/context copy of the objective because it is actual harness overhead.',
    aggregate: 'Join system text, remaining user/environment text, and compact JSON tool schemas with newline separators, then count reference tokens. This excludes chat-role/template overhead and is not provider-native usage.',
    bytes: 'UTF-8 byte counts over decoded components and compact JSON.stringify output. Raw HTTP request bytes were not preserved in the capture file.',
    byteRatio: 'ceil(UTF-8 bytes / 4), shown only as a rough comparison baseline.',
    nativeUsage: 'Unknown. The loopback fixture returned zero usage and does not establish native prompt-token consumption.',
  },
  cases: caseResults,
};
fs.mkdirSync(path.dirname(outputPath), { recursive: true });
fs.writeFileSync(outputPath, JSON.stringify(evidence, null, 2));
console.log(JSON.stringify({ output: outputPath, cases: caseResults.map(item => ({
  case: item.case,
  systemTokens: item.system.referenceTokens,
  userEnvironmentTokens: item.userAndEnvironment.referenceTokens,
  schemasTokens: item.toolSchemas.referenceTokens,
  sansPromptTokens: item.contentAndSchemasSansOriginalUserMessage.referenceTokens,
  sansPromptBytes: item.contentAndSchemasSansOriginalUserMessage.utf8Bytes,
  transportBytes: item.reconstructedCompactRequestJson.utf8Bytes,
})), capturesSha256: evidence.capturesSha256 }, null, 2));
