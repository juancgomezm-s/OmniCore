# Initial harness context measurement — 2026-10-13

This note records one first-request snapshot for OmniCore, OpenCode, Pi, Claude Code
and Codex CLI. It is a
static measurement of captured outbound request bodies, not a claim about
provider billing or a native model tokenizer. It describes OmniCore snapshot
base commit `29c2c26` plus the working changes from this session, including the
corrected profile disclosure ordering. It is not a measurement of pristine `main`.

## Capture and reproducibility

- Captured at `2026-10-13T19:10:21.918Z` against the same isolated fresh
  workspace containing one README, with no history, activated skills, memory,
  or user configuration. The first prompt was: `Examina este proyecto y
  explica su estructura sin modificar archivos.`
- Cases: OmniCore ObserveOnly, PatchOnly, ScopedCoder, and FullAgent; OpenCode
  plan and build, version `1.18.35`; Pi `1.1.0` with its default four tools
  (`read`, `bash`, `edit`, `write`); Claude Code `2.1.296`, manual mode; Codex CLI
  `0.153.4`, read-only sandbox. One captured request per case, nine total.
  Pi uses an isolated agent directory, in-memory session, offline mode, disabled
  telemetry/update checks and no extensions, MCP, skills, templates or context
  files. Its default system prompt is retained, not replaced with a shorter one.
  Claude uses a fresh config directory, safe-mode, no saved sessions or MCP,
  disabled nonessential traffic and a dummy key against loopback Messages.
  Codex uses a fresh CODEX_HOME, no user config/rules or persisted session,
  disabled web search and a no-auth loopback Responses provider. No real key or
  account is read. Neither native base instruction set is replaced.
  Native selected model IDs were `claude-opus-5-5` and `gpt-6-astra`; the fixture
  does not verify account entitlement or actual inference for those IDs.
- Raw parsed request capture:
  `C:\Users\juanc\AppData\Local\Temp\omnicore-context-measure-nsfNCT\requests.json`
  (SHA-256 `72fa30a1eab0ca85a790b65b5439f18843be508d24b37026b1749169b3c93b91`).
  Generated capture summary: `TestResults/initial-context-five-harnesses-final-measurement.json`
  (SHA-256 `8840b8884fb1fdffea2352c30344f1b6e4c548af4336849549b9855729561eb0`).
  Compiled `omni.dll` SHA-256:
  `c52c6b3912367024d9004912de37afa360da06820e9de24f7942ddf2cb68ae10`.
  Installed Pi bundle SHA-256:
  `e79626f2dd6f94aa45d30f3fa63cd84319a6eefcd150b353cfaf274366926774`.
  Installed Claude executable SHA-256:
  `76de74f7694a5bdc03378a5f837c9e132827bf004c98c276bc4e9094ff06769a`.
  Installed Codex executable SHA-256:
  `444a3f0008050605cae73cd9b7a2dcac61294062dfaab56dd20430fd6498518b`.
- Reproduce the reference counts with Node.js and `gpt-tokenizer@4.0.0`
  installed outside the repository:

  ```powershell
  dotnet build src/OmniCore.Cli/OmniCore.Cli.csproj
  $opencodeBase = Split-Path (Get-Command opencode).Source
  $opencodeBinary = Join-Path $opencodeBase 'node_modules\opencode-ai\bin\opencode.exe'
  $piBase = Split-Path (Get-Command pi).Source
  $piCli = Join-Path $piBase 'node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js'
  node tools/measure-initial-context.cjs --opencode $opencodeBinary --pi $piCli --claude (Get-Command claude).Source --codex (Get-Command codex).Source --output TestResults/initial-context-five-harnesses-final-measurement.json
  npm install --prefix "$env:TEMP\omnicore-context-reference-gpt-tokenizer-4.0.0" --no-save --no-audit --no-fund gpt-tokenizer@4.0.0
  node tools/test-initial-context-payload.cjs
  node tools/count-reference-token-context.cjs --measurement TestResults/initial-context-five-harnesses-final-measurement.json --output TestResults/initial-context-five-harnesses-final-reference.json --tokenizer-package "$env:TEMP\omnicore-context-reference-gpt-tokenizer-4.0.0\node_modules\gpt-tokenizer"
  ```

  The capture script runs the installed harnesses against a loopback HTTP fixture;
  it does not perform real inference. The reference-count script reads those
  captures without launching a harness or contacting a provider. It writes
  `TestResults/initial-context-five-harnesses-final-reference.json`; a published snapshot
  is included in [the measurement data](assets/initial-context-20261013.json).
  The reference implementation is the `gpt-tokenizer` GPT-5.6-Luna model
  module using `o200k_base` ([upstream project](https://github.com/niieani/gpt-tokenizer)).
  This is a deterministic OpenAI-family reference encoding, not a claim that
  the Luna service, Qwen, or any provider applies the same tokenizer or chat
  template.

## Results

“System” includes native system/instructions fields and developer messages,
including permission/environment instructions. “System”, “user/environment”, and “tool schemas” each report UTF-8 bytes and
reference tokens over that component, including the original user
prompt where present. “Context + schemas sans prompt” removes the first exact
occurrence of the original prompt from user content only, preserving any copy
in the system context because that is actual injected harness overhead, then joins
system text, remaining user/environment content, and compact JSON tool schemas
with newline separators before tokenizing. Its byte total uses the same joined
text. “Compact request JSON” is the UTF-8 byte count of `JSON.stringify` on the
captured parsed body; raw HTTP wire bytes were not retained. It is transport
serialization, not a prompt-token count.

Text-only content arrays are normalized by joining their `text` fields with
newlines, rather than tokenizing JSON `type`/`text` wrappers as injected text.
Unsupported non-text content fails the comparison. This corrects the earlier
OpenCode Plan count that included those wrappers; it is a methodology correction,
not evidence that OpenCode itself changed. Schema JSON is still counted using
the same reference representation for every harness.

Codex's native request has catalogs inside `input.additional_tools`, not top-level
`tools`. These are included in schema text; namespace wrappers and descriptions
are retained, while the tool count reports 11 callable leaves in three namespaces.
The `exec` surface can expose more operations through instructions, so the count
is not the total number of possible capabilities. Ignoring this input item would
incorrectly report zero tools and omit 4,507 reference schema tokens.

| Case | System bytes / ref. tokens | User+environment bytes / ref. tokens | Tool schemas bytes / ref. tokens | Context + schemas sans prompt bytes / ref. tokens | Compact request JSON bytes |
|---|---:|---:|---:|---:|---:|
| OmniCore ObserveOnly | 977 / 218 | 69 / 12 | 4,105 / 943 | 5,083 / 1,161 | 5,314 |
| OmniCore PatchOnly | 825 / 180 | 69 / 12 | 4,515 / 1,034 | 5,341 / 1,215 | 5,571 |
| OmniCore ScopedCoder | 825 / 180 | 69 / 12 | 5,639 / 1,274 | 6,465 / 1,455 | 6,695 |
| OmniCore FullAgent | 825 / 180 | 69 / 12 | 6,523 / 1,460 | 7,349 / 1,641 | 7,579 |
| OpenCode plan | 9,623 / 2,025 | 1,556 / 310 | 21,471 / 4,889 | 32,583 / 7,213 | 33,062 |
| OpenCode build | 9,623 / 2,025 | 71 / 13 | 21,471 / 4,889 | 31,098 / 6,916 | 31,501 |
| Pi default | 3,116 / 776 | 69 / 12 | 2,841 / 609 | 5,958 / 1,385 | 6,333 |
| Claude Code default | 12,201 / 2,761 | 653 / 146 | 51,333 / 11,910 | 64,120 / 14,805 | 65,158 |
| Codex CLI default | 28,187 / 5,588 | 621 / 162 | 19,442 / 4,507 | 48,183 / 10,245 | 50,865 |

Compared with OpenCode Build in this snapshot, OmniCore's four profiles have
76.3–83.2% less fixed reference context; Pi's default has approximately 80.0% less.
This leaves more of the same context window available for task data. It does not
prove equivalent capabilities, answer quality, generation speed or billing savings:
the offered tools and their contracts differ. Pi is smaller than OmniCore's
ScopedCoder and FullAgent profiles, not larger than all OmniCore configurations.
Against the captured Codex CLI and Claude Code requests, OmniCore's four profiles
use 84.0–88.7% and 88.9–92.2% less fixed reference context, respectively.

## Product and transport scope

Codex CLI is not ChatGPT web or the current desktop conversation. The web product's
internal injected context is not measured here and remains unknown. A client request
cannot establish provider-side hidden context or native prompt-token framing.
The routes and model metadata are intentionally recorded rather than presented as
identical: Chat Completions for the first three harnesses, Messages for Claude and
Responses for Codex. Native tools, permissions and model-specific contracts differ.

Isolation uses [official Codex provider configuration](https://learn.chatgpt.com/docs/config-file/config-reference)
and [Claude environment controls](https://code.claude.com/docs/en/env-vars);
[Claude config-directory isolation](https://code.claude.com/docs/en/settings)
keeps existing accounts and settings untouched. The loopback fixture is not a TLS
interceptor and never forwards a request to an external model service.

## Validation

All nine fixture processes exited zero and produced exactly one generation request.
The shared payload tests cover Chat, Messages, Responses instructions, inline tool
namespaces and non-text rejection. The capture and summary hashes above identify the
final evidence; reference values are checked against the HTML/README tables.

## Limits

The previous measurement report also shows `ceil(UTF-8 bytes / 4)` as a rough
rule-of-thumb estimate; it is not used as a token count here. The reference
tokenizer counts plain text components and the joined aggregate. It does not
model message-role framing, tool-call protocol framing, hidden system
instructions, or provider-specific serialization. The loopback fixture
reported zero usage, so native prompt-token usage is unknown. No provider call,
account change, or live credential was involved.

Counts are one captured sample, not a universal constant: paths, session IDs,
the selected tools and the installed harness version can change them. Configured
user instructions, memories, skills and conversation history add further context.
