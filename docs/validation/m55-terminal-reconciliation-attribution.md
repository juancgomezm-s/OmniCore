# Terminal-run reconciliation keeps its causal owner

## Reproduced defect

ReconcileTerminalRuns previously reused an EventStream without an explicit scope.
An unknown effect of an older terminal Run could inherit the latest Run's
correlation and the prior appended event as its cause. Reconciliation results
also fall outside the older Run's chronological slice after a later Run starts.
Reading only that slice tried to reconcile the same ToolCall again.

The deterministic two-terminal-Run regression failed before the fix on the second
reconciliation call: `InvalidStateTransitionException`, Reconciled to reconciled.
Log: `reconciliation-attribution-red-test.log`, one FAIL, 0.407 s; preceding build
had zero warnings/errors. This is a reproduced defect, not only a code inference.

## Correction

Each result now uses an explicit EventCausation referencing its own
ToolCallEffectUnknown event. Its ExecutionScope uses the owning Run and entity
ids retained on that source event, with ToolCallStarted's recorded ids filling
missing values. It never borrows ids from another Run's current ambient scope.
Payload ToolCallId remains authoritative.

Terminal outcomes are checked across the session by the unique ToolCallId before
reconciling an older Run's slice. This also recognizes already-written legacy
results even if their old envelope attribution was incorrect. The fix does not
rewrite journals or reconcile terminal outcomes a second time.

## Verification and remaining boundary

Logs: `C:\Users\juanc\.codex\omni-m55-three-20261006`.

- Corrected reconciliation focal: 52 PASS, zero FAIL/SKIP, 0.839 s,
  `reconciliation-attribution-fixed-focal.log`; build zero warnings/errors.
- Combined final focal: 110 PASS, zero FAIL/SKIP, 4.577 s,
  `reconciliation-binding-final-focal.log`; build zero warnings/errors.
- Full suite: 1706 total, 1702 PASS, zero FAIL, four symlink-permission SKIP,
  204.762 s (`reconciliation-binding-full-final.log`).

Assertions cover both Runs' RunId/CorrelationId, TaskId, LaneId, TurnId,
ToolCallId and direct source-event causation; the second query returns zero,
the reconciler is called exactly twice, and no pending effects remain.
The reconciler and event store are deterministic fixtures, not actual filesystem
effects or authenticated provider consumption. Counts overlap.

This scoped fix does not remove EventStream's global last-event fallback. That
ADR-0046 boundary and conflict-interaction attribution remain separate work.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
# Run only after a successful build.
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*TerminalRunReconciliationAttributionTests' -class '*RunControlTests' -class '*RunControlEdgeTests' -class '*RunControlCommandOutcomeTests' -class '*ReconcilerTests' -class '*ReconcilerWatchdogTests'
```
