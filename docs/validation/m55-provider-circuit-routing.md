# Provider circuit availability and routing

The Host reuses the existing provider breaker rather than performing readiness requests.
`OmniCliRuntime` owns one in-memory `ProviderResilienceCatalog` for its lifetime and
passes it to the provider factory, initial router and escalation router. It is not
global or durable: a new runtime/process starts without historical circuit measurements.
The key is the configured provider identity, consistent with ADR-0011, not the model
or RouteId. Multiple models of the same provider therefore share the circuit.

`Snapshot(providerId)` is nullable for an unseen provider. A present snapshot reports
`MeasuredAt`, `OpenUntil`, `ProbeInFlight` and `CanAttempt` from the existing clock and
breaker lock. Reads neither send HTTP requests nor reserve a half-open probe. Unknown
means no observed breaker rejection; it is not a claim that an authenticated health
request succeeded. The actual adapter invocation still atomically enters the circuit,
so a selection is not a reservation or a concurrency guarantee.

Open circuits and in-flight half-open probes are excluded by the router's existing
`Unavailable` rejection. After cooldown a single invocation may probe; a successful
completion clears the circuit. Cancellation releases its probe without reporting success.
The shared catalog retains its first options configuration per provider; constructing
another adapter does not reset failures or replace the clock/cooldown.

Callers that omit the catalog retain isolated adapter behavior for compatibility.
CLI direct/default/manual selection still receives the adapter's typed rejection; this
change does not authorize another route, alter session consent or replay provider state
onto a fallback route. RouteCandidate identity migration remains a separate open block.

## Evidence

Build verified with zero warnings/errors. Focused suite: 66 PASS, 0 FAIL, 0 SKIP
(`provider-circuit-focal.log`, 3.052 s). First full suite: 1690 total,
1685 PASS, 1 FAIL, 4 SKIP symlink (`provider-circuit-full-suite.log`, 144.362 s).
Failure: `TuiWiringTests.Esc_cancels_pending_login_and_ignores_late_progress`,
Terminal.Gui TextView initialization throws a Lazy ValueFactory reentrancy exception
inside `StartLogin`. Isolated repeat: 1 PASS (`provider-circuit-login-isolated.log`);
this is not a root-cause diagnosis or a full-suite pass. Unchanged full repeat:
1690 total, 1686 PASS, 0 FAIL, 4 SKIP symlink (`provider-circuit-full-repeat.log`,
142.008 s). The intermittent login failure remains open; neither the repeat nor this
circuit change establishes that Terminal.Gui initialization is fixed.
The deterministic adapter tests use controlled handlers/clocks.
Host factory integration uses real loopback HTTP errors for each native family. Neither
fixtures nor loopback HTTP demonstrate authenticated provider access or real consumption.

The first build failed on missing fixture imports and xUnit cancellation-token rules;
`provider-circuit-build.log` is retained, then `provider-circuit-build-fixed.log` proves
the successful corrected build. No tests were run against the failed build's old DLL.
All logs are under `C:\Users\juanc\.codex\omni-m55-three-20261006`.

The half-open reservation has a generation token: an abandoned old probe cannot release
a newer reservation. Existing success/failure accounting from other already-running
requests still resets circuit state globally; this block does not claim a new concurrency
epoch model. No scheduler or parallel Lane behavior is introduced.

```powershell
dotnet build tests/OmniCore.Tests/OmniCore.Tests.csproj --no-restore -v quiet
dotnet tests/OmniCore.Tests/bin/Debug/net10.0/OmniCore.Tests.dll -noLogo -parallelMode none -class '*SharedProviderCircuitTests' -class '*ModelRoutingHostTests' -class '*HostProviderCircuitIntegrationTests' -class '*ModelProviderResilienceTests' -class '*OpenAIResponsesProviderTests' -class '*AnthropicMessagesProviderTests' -class '*CliEndToEndTests'
```
