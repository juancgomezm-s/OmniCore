# Regression measurements

`measure.py` extracts the Core arm of a regression receipt from explicitly supplied,
closed SQLite journal snapshots. It never opens a runtime Session, recovers Runs,
calls a provider, executes a task or modifies the journal. Python 3.11+ and the
standard library suffice. The exporter alone does not execute the agentic battery. The opt-in drivers below
implement the fixed eleven-category suite of spec §84 and produce the §97 comparison.

Create a consistent SQLite backup **after the test process has closed**, including
committed WAL data in the backup. Do not copy only the main file of a live database.
The exporter requires a standalone snapshot with no `-wal`, `-shm` or `-journal`
sidecars and a complete Session sequence starting at one. Supply only disposable
evaluation data; no automatic discovery of personal journals occurs. Input and
artifact paths must not traverse symlinks or junctions.

Example input (`request.json`; replace the example hashes and IDs with real ones):

```json
{
  "schema": "omnicore.regression-snapshot-input.v1",
  "measurementSource": "fixture",
  "benchmarkIdentity": {
    "suiteId": "my-pinned-suite",
    "suiteVersion": "1.0.0",
    "taskSetHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "seed": null,
    "temperature": null,
    "samplingParametersSent": false
  },
  "modelIdentity": null,
  "tasks": [{
    "id": "repository-exploration",
    "success": null,
    "runs": [{
      "journalSnapshot": "evaluation-snapshot.db",
      "sessionId": "00000000-0000-4000-8000-000000000001",
      "runId": "00000000-0000-4000-8000-000000000002",
      "artifactRoot": "artifacts"
    }]
  }]
}
```

Paths resolve relative to the input file. `artifactRoot` is optional and denotes
the CAS root containing `blobs/sha256/aa/bb/<hash>`; it is not a workspace search
path. `modelIdentity` may contain `providerId`, `modelId`, `modelRevision`,
`adapterProfile`, `endpoint`, `protocol`. Credential-bearing URLs are refused.
When `success` is a boolean, `oracleEvidenceSha256` must identify the harness's
independent oracle receipt. Completion alone never establishes task success.
Optional per-Run `frontierPolicy` contains `modelIds` and `evidenceSha256`, pinning
the harness's classification rather than guessing from model names.

```powershell
python tools/evaluation/measure.py request.json --json-output core.json --locale es
python tools/evaluation/compare.py core.json pi.json --json-output comparison.json --locale es
python -m unittest discover -s tests -p "test_regression_*.py"
```

`--locale` accepts es/en/fr/it. Machine keys and numeric values are invariant.
The exporter creates a new output file exclusively and refuses overwrites. Its
fixed error text does not expose SQL payloads or provider responses. The output
uses `omnicore.regression-measurements.v1`, retaining snapshot SHA-256 and scope
IDs but omitting raw prompts, tool arguments, context items and source paths.

## Measurement policy

- Time is the single Run's journal clock from `run.created` to its terminal
  event, including waits. Missing/backward timestamps and multiple Runs yield
  `null`; overlapping Run durations are not added as wall time.
- Turns count unique `turn.started`; tools count unique `toolcall.requested`,
  including rejected requests and Host-owned children. Other lifecycle events
  do not multiply the count.
- Tokens sum reported input plus output across main and meta invocations. Cache
  tokens are input subsets; reasoning tokens are output subsets. Missing fields,
  unresolved invocations, legacy unobserved calls or insufficient retry coverage
  make the total unknown. Cost requires complete reported usage/attempt coverage
  and a complete current cost receipt. Explicit zero remains zero. No model
  receipt does **not** prove zero consumption.
- Exact context growth is the maximum, across lanes, of peak minus initial
  token count from hash-verified, Session/Run/Turn/Lane-scoped snapshots. Missing
  or estimated snapshots yield `null`; tampered or foreign snapshots are errors.
- Frontier escalations require an explicit harness policy when escalations exist;
  otherwise classification remains unknown. No escalation observed gives zero.
- A nonterminal Run yields unknown task result and metrics rather than partial
  totals presented as complete. At most 128 tasks/32 Runs per task are accepted;
  a Run cannot be reused in multiple tasks.

`measurementPolicy` travels with the receipt; `compare.py` refuses differing
policies as well as task/sampling identity mismatches. Two legacy receipts without
a policy remain readable, but a legacy receipt cannot silently match a new one.
Integrity hashes are **not authentication**: `provenanceVerified` stays false;
live/fixture, model identity, oracle and frontier policy are harness declarations.
The operator must independently retain and review their underlying evidence.
Do not relabel simulator/qualification fixtures as the agentic battery, or change
another harness's runtime name to manufacture a Pi comparison. Missing external
measurements remain missing.


## Opt-in live agentic battery

`agentic_suite.py` pins eleven dependency-free JavaScript repositories, original
file hashes, identical objectives for both arms, immutable public test gates and
an independent behavior/preservation oracle stored outside the workspace.
`run_agentic.py` drives the production Core CLI/Host and the existing consumer's
`PiRpcClient` plus the installed Pi executable. It never edits the consumer.
`finalize_agentic.py` rechecks settled receipts without calling a model.
The [2026-10-30 acceptance](../../docs/validation/m10-agentic-20261030.md)
records actual results, controls, image hashes and measurement limits.

Prerequisites: Python 3.11+, Node 24, .NET 10, Git and (for Core's Windows console
permissions) `pywinpty` in an explicitly supplied private dependency directory.
This is not part of automatic qualification or CI model traffic. CI builds the
Host driver and runs offline oracle/receipt tests only. No OS or global package
installation is performed by these scripts.

Prepare an explicitly marked disposable evaluation root with
`evaluation-root.json`; its contents are operator-owned. Supply settings with:

- `evaluationRoot`, `coreConfig`, `coreData`, `piAgent`, `output`, `coreCli`,
  `coreDriver`: absolute paths inside that root. The two executable DLLs must be
  adjacent in **one** CLI image. Rebuilding and mixing Host/Engine/Models assemblies
  between interruption and resume correctly invalidates runtime fingerprints.
- `node`, `piCli`, `piClient`: explicit existing Node, Pi CLI and consumer RPC
  module paths; the latter two are read-only. `pythonDependencies` is optional.
- `billingMode: "IncludedQuota"`, `profile: "codex"`, and optional
  `taskTimeoutSeconds` (180 by default). The pinned model is `gpt-6.1-sol`, medium
  reasoning, without sending seed or temperature.

Provision only the subscription access needed for the private profiles, without
logging secrets or copying a personal configuration tree. This harness never
provisions paid API credentials. Keep private `auth.json`/`credentials.ini`
outside reports and source control; remove evaluation access copies after use.
Use an autonomous private permissions profile and versioned FullAgent conversation
security; the denial scenario installs its own explicit refusal. Never point the
drivers at a personal journal, workspace, installation or authentication store.

Build the CLI and driver into an isolated artifacts directory. Copy only
`CoreDriver.dll`, `CoreDriver.deps.json` and `CoreDriver.runtimeconfig.json` from
its output next to `omni.dll`; retain the CLI's existing Core assemblies.
Before the runs, invoke that driver with a JSON request containing `mode: "prepare"`,
`workspace` and `evaluationRoot`, with the same private `OMNICORE_CONFIG_DIR` and
`OMNICORE_DATA_DIR`; this records explicit medium reasoning through the public
production preference store. Every scenario gets a fresh fixture/config clone.

```powershell
python tools/evaluation/run_agentic.py settings.json --arm core
python tools/evaluation/run_agentic.py settings.json --arm pi
python tools/evaluation/finalize_agentic.py settings.json
```

Output directories are exclusive; failed attempts are retained, never silently
replaced. `--ids` selects diagnostic cases. For incremental finalization,
`sourceRoot` identifies the original complete run and `caseSources` explicitly
maps replacement receipts by arm/id. Every selected objective must equal the
current pinned objective, and Core/Pi baselines must match byte for byte.
Retain the original per-version inputs and image manifests; receipt composition
must be disclosed, not presented as one fresh batch.

Controls are real: an explicit write refusal, a single HTTP 503 from a loopback
relay to a fixed HTTPS subscription origin, a supplied context above 12,000
reported input tokens, a process restart after the first write, and bounded live
child work. The relay neither logs nor persists request headers/bodies. Core uses
its native Explore/Implement/Verify workflow and accepted owned implementation
worktree, with a Node verifier bound to the precise argv. The independent oracle
checks that tree, its native repository/owner binding and accepted results;
publication into the parent is a separate action and is not claimed. Pi uses an
explicit test extension with bounded file tools and one live child through the
same RPC client. These are different orchestration designs under the same task.
Node's preserve-symlinks flags let the fixed verifier run inside Windows Strong
without resolving a forbidden parent directory. No sandbox policy is widened.

The harness measures wall time through process exit, including recovery and
Core's native validation; the independent oracle runs afterwards. Turns and tools
are native admitted turns/requested calls, including children and denials; resume
can preserve a Core Turn whereas Pi admits another user message. Reported total
input (including cache) plus output is counted once. Incomplete retry usage and
unobserved subscription charges remain `null`; Pi's nominal API price is not a
subscription bill. Context growth is reported input growth, not an exact shared
tokenizer/context snapshot. Compaction is an observed event count; externalized
artifact counts remain unknown when not exposed. Finalization may report complete
invocation usage through a closed benchmark checkpoint even when a parent workflow
Run remains open for a later publication; the strict terminal-Run exporter is
unchanged. Model identity is declared, not cryptographically authenticated.
One sample per case supports functional acceptance, not statistical performance
ranking or consumer UI acceptance.
