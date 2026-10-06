# Routing by physical RouteId

The pure Engine router selects concrete `ModelRoute` candidates. `RouteCandidate`
retains `ModelId` separately for registry lookup, pricing and display; its `RouteId`
comes from the retained immutable route. Preference lists and rejection identities
are typed `RouteId`, compared by value rather than object reference. The former `Alias`
field is removed; display and registry lookup use `ModelId` explicitly.

Host resolves YAML aliases to model definitions and then to candidates' physical route
identities. Candidate routes capture the endpoint override once for the selection.
Preference construction uses those same candidates, so a hash never becomes a model
registry or pricing key. Local classification uses the effective endpoint, not the
configured endpoint that an override may replace. Circuit availability remains keyed
by provider, not route.

The runtime retains the selected route through provider construction, consent and
ModelStepStarted. Auto escalation carries that exact route into the continued invocation;
consented resume carries the route checked against the durable offer. New choices still
require their existing SessionRoutingPolicy authorization: route selection is not consent.
Protocol/profile/model/provider mismatches fail closed before invocation.

Escalation excludes the current physical route. If exclusion leaves an empty chain it
returns no candidate instead of treating an empty preference list as unconstrained
selection. The string-model overload resolves the existing default route for callers;
production runtime passes the actual originating RouteId.

This does not add multiple endpoints to the YAML schema, a new scheduler, joins or
cross-route ProviderState replay. The existing 1:1 default route preserves the model id;
an endpoint override has a derived physical identity.

## Verification

Tests distinguish two physical routes of the same logical model; resolve YAML aliases
under an endpoint override; preserve pricing/model lookup; exclude the current route;
and capture endpoint before a callback changes the environment. A real CLI/Host/SQLite
test invokes the controlled loopback endpoint and checks ModelStepStarted's route hash
alongside its logical model id. HTTP fixtures are not authenticated consumption.

Core focal verification: 102 PASS, 0 FAIL, 0 SKIP, 8.116 s
(`routeid-router-core-focal-fixed.log`), including controlled CLI HTTP and escalation
price regressions. Full suite: 1696 total, 1692 PASS, 0 FAIL, 4 SKIP (symlink
permissions), 201.818 s (`routeid-router-affinity-full-suite.log`). Build: zero
warnings/errors. Counts overlap; fixtures do not establish authenticated usage.

First build failed on the new diagnostic test's deprecated TextView reference; its
narrowly scoped pragma matches the production control being inspected and changes no
assertion. Removal of Alias then exposed six old test call sites; those now explicitly
assert ModelId. Corrected typed build: 0 warnings/errors. No tests used an older DLL
after a failed build. The invalid `*Escalation*Tests` filter ran no tests and is retained
in `routeid-router-core-focal.log`, not counted as evidence.

Initial focal suites: 127 total/126 PASS/1 FAIL and 128 total/127 PASS/1 FAIL
(`routeid-router-focal.log`, `routeid-router-final-focal.log`). The composer color failure
was a NO_COLOR environment mismatch: that variable was present and production correctly
uses Color.None in that mode. A color-enabled repeat passed the composer test but failed
during a different driver's resize (`routeid-router-color-focal.log`), OutputBase.Write
IndexOutOfRange. The 20-cycle login diagnostic passed, not a root-cause proof.

An explicit affinity regression reproduced the fixture defect: the UI loop thread id
was 11 while Application.MainThreadId was 6 (`tui-thread-affinity-before-test.log`,
1 FAIL). Init and Run occurred on different threads, invalidating the assumption that
test-side Application.Invoke dispatches to the render loop. Init and Run now share the
UI thread; the corrected TUI suite passed 59 tests, including live resize and 20 login
cycles (`tui-affinity-wiring-tests.log`, 86.261 s). This is a fixture correction,
not a claimed production or framework fix of every intermittent initialization failure.
