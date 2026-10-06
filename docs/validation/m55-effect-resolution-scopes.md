# Conflict interactions and effect results have different owners

## Reproduced defects

An unresolved effect of terminal Run A can pause current Run B. The interaction
and B's waiting/resume lifecycle belong to B; the eventual human effect outcome
still belongs to A's ToolCall. Neither may inherit ownership from whichever
event was appended most recently.

The first integration regression failed on the conflict request's RunId:
`effect-resolution-attribution-red-test.log`, one FAIL, 0.447 s; build zero
warnings/errors. Publication had inherited A after its terminal reconciliation,
even though the waiting Run was B.

After explicit publication and response scopes, the first 72 focal cases passed.
The expanded memory/SQLite cases with no active Run reproduced a further defect:
the late interaction's explicit A ownership was overwritten by chronological B.
`effect-resolution-terminal-priority-test.log`: four cases, two PASS/two FAIL,
0.590 s. Explicit RunId now takes precedence; chronological reconstruction is
only a fallback for legacy source envelopes without recorded RunId.

## Implementation contract

EventStream retains its existing two-argument AppendBatch signature and adds an
overload with an optional-per-item scope list. Its length must match payloads
before reading or writing. Each envelope takes supplied scope or ambient scope,
with payload identities retaining priority. All payloads are validated and
persisted in one store AppendBatch; this never splits an atomic commit or changes
ambient command causation. Caller scopes do not leak into AsyncLocal state.

PublishEffectResolutionRequests explicitly binds the interaction to the current
non-terminal Run (including AwaitingInput). If there is no active Run, it retains
the effect's recorded owner instead. The optional RunAwaitingInput has its own
scope. The owner is taken from original EffectUnknown/Started evidence, not an
earlier legacy reconciliation envelope that might have been misattributed.

RunControl response persists InteractionResolved, the human ToolCallReconciled
and optional UserInputReceived in one atomic batch under the real response command.
Only the effect outcome uses A's Run/Task/Lane/Turn/Execution/ToolCall identity;
interaction and current-run reactivation retain the request's owner. A terminal
Run is not reactivated. Missing source ownership rejects the response instead of
claiming a different owner. No old journal is rewritten.

## Reproducible evidence

Logs: `C:\Users\juanc\.codex\omni-m55-three-20261006`.

- Corrected focal: 74 PASS, zero FAIL/SKIP, 1.005 s,
  `effect-resolution-priority-fixed-focal.log`; build zero warnings/errors.
- Final build preserving the two-argument signature: zero warnings/errors,
  `effect-resolution-final-build.log`.
- Full execution: 1714 total, 1710 PASS, zero FAIL, four symlink-permission SKIP,
  204.828 s (`effect-resolution-scoped-full.log`). Counts overlap.

The integration runs through actual OmniServer.Send, memory and SQLite journals,
recovery, response command causation, audit effect.human_resolution and Run
projections. Cases cover an active waiting Run and a later terminal Run; both
retain the old effect's ownership. Batch tests independently cover per-item ids,
payload priority, ambient restoration, count validation before store access and
no partial state on a controlled failed store commit. These are fixtures; they
do not establish authenticated provider consumption or actual filesystem effects.

This does not close the separate Source/global-causation fallback and writer-guard
requirements of ADR-0046, nor implement M6 scheduling.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
# Run only after a successful build.
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*EffectResolutionOriginAttributionTests' -class '*ScopedEventBatchTests' -class '*M3OrphanEffectNotLostTests' -class '*TerminalRunReconciliationAttributionTests' -class '*RunControlTests' -class '*RunControlEdgeTests' -class '*RunControlCommandOutcomeTests' -class '*EventStreamFailedWriteCausationTests' -class '*JournalEnvelopeTests'
```
