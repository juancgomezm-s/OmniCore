# Regression measurements

`measure.py` extracts the Core arm of a regression receipt from explicitly supplied,
closed SQLite journal snapshots. It never opens a runtime Session, recovers Runs,
calls a provider, executes a task or modifies the journal. Python 3.11+ and the
standard library suffice. This tooling does not itself implement the fixed
agentic suite of spec §84 or prove the comparison required by §97.

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
