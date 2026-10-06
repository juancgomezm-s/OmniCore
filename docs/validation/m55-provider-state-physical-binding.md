# ProviderState replay bound to the physical destination

## Defect and contract

The migration RouteId deliberately remains the model id. Changing a provider's
configured BaseUrl therefore need not change that RouteId. Comparing only model
and RouteId was insufficient to authorize opaque continuation replay after resume.
New spending consent for the replacement endpoint does not authorize transferring
state from the former endpoint.

ModelSelection now optionally retains the immutable ModelRoute and its canonical
identity hash, using the same SHA-256 binding as AuthorizedModelRoute. The CLI
runtime passes the actual selected route. The binding covers provider, endpoint,
protocol, profile and provider-side model name; it is not a registry key or billing
authorization. Legacy RouteId and existing constructor call sites remain compatible.

ProviderStateCheckpoint writes descriptor version 2 with RouteIdentityHash.
Restore requires matching model, RouteId, TurnId, step and valid physical binding
before reading the opaque state artifact. Version 1 remains readable but cannot
authorize replay. Missing physical binding also cannot authorize replay. Such
checkpoints return no continuation; they are not silently attributed to the
current physical destination. Ordinary same-invocation step threading is unchanged.

The artifact remains Sensitive, integrity checked and subject to the existing
redactor. This change does not bypass redaction, add raw opaque storage, solve
ReasoningReplayPolicy, authorize spending, or introduce M6 scheduling.

## Reproducible verification

Logs: `C:\Users\juanc\.codex\omni-m55-three-20261006`.

- Build: zero warnings/errors, `provider-state-physical-binding-build.log`.
- Focal: 62 PASS, zero FAIL/SKIP, 4.655 s,
  `provider-state-physical-binding-focal.log`.
- Initial full: 1705 total, 1700 PASS, one FAIL, four symlink skips, 206.223 s
  (`provider-state-physical-binding-full.log`). The native Anthropic resume fixture
  had no physical binding; its signed-thinking assertion failed as expected under
  the new fail-closed contract. It now declares the same physical route as its
  provider descriptor, preserving every signature and ordering assertion.
- Native-adapter focal after correction: 58 PASS, zero FAIL/SKIP, 4.144 s
  (`provider-state-physical-binding-native-focal.log`).
- Final combined focal with reconciliation: 110 PASS, zero FAIL/SKIP, 4.577 s,
  build zero warnings/errors (`reconciliation-binding-final-focal.log`,
  `reconciliation-binding-final-build.log`). Counts overlap.
- Final full: 1706 total, 1702 PASS, zero FAIL, four symlink-permission SKIP,
  204.762 s (`reconciliation-binding-full-final.log`).

The SQLite/CAS/OmniServer integration suspends on a questionnaire, closes and
reopens the journal, resolves the interaction, and verifies exact state replay
for the unchanged destination. Changing endpoint/provider/protocol/profile while
keeping the same legacy RouteId results in no continuation. Model/RouteId mismatch,
missing binding, corrupted state, redaction failure and null-state clearing remain
covered. Checkpoint tests verify v1/unbound/mismatched state is not read from CAS.
Selection tests distinguish a physical binding from the unchanged legacy id.

These are deterministic scripted-provider fixtures with real journal/artifact
integration, not authenticated provider consumption or quota measurements.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
# Run only after a successful build.
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*ProviderStateCheckpointTests' -class '*ExplorerTurnDurableProviderStateTests' -class '*ExplorerTurnProviderStateIsolationTests' -class '*ExplorerTurnProviderStateNullClearingTests' -class '*ExplorerTurnContinuationRegressionTests' -class '*ExplorerTurnOpaqueReasoningCasTests' -class '*ExplorerTurnResumeInputRegressionTests' -class '*ModelRouteContractTests' -class '*CliEndToEndTests' -class '*RoutingConsentResumeTests'
```
