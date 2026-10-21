using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Sandbox;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public Task Native_forced_extension_without_required_tool_effect_is_terminal_failed()
        => RunNativeForcedExtensionChoiceAsync("deny", shouldWriteMarker: false, providerInvokesTool: false);

    private async Task RunNativeForcedExtensionChoiceAsync(string optionId, bool shouldWriteMarker,
        bool providerInvokesTool = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The real Strong extension child fixture requires Windows AppContainer.");
            return;
        }
        var sandbox = new WindowsAppContainerCapabilitiesProbe().Probe();
        if (!sandbox.StrongAvailable)
        {
            Assert.Skip("Strong AppContainer is unavailable: " + sandbox.StrongUnavailableReason);
            return;
        }

        await using var fixture = await NativeCommandHandoffFixture.StartAsync(
            Guid.NewGuid().ToString("D"), includeStrongExtension: true,
            providerResponse: (index, _) => index switch
            {
                1 when providerInvokesTool => CliEndToEndTests.ResponsesToolCallResponse("extension-write", "ext.fixture.native.mutate", "{}"),
                1 => CliEndToEndTests.ResponsesTextResponse("The provider returned without the required extension call."),
                _ => CliEndToEndTests.ResponsesTextResponse("The local extension permission fixture completed this Turn."),
            });
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync("plan");
        var setupScope = fixture.QueryJson("humanActionScope");
        var runId = setupScope.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The setup Turn did not leave an active Run.");
        var selectAct = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select",
            runId,
            mode = "act",
            expectedRevision = setupScope.GetProperty("revision").GetInt64(),
        }));
        var selectActAck = Ack(fixture.ExchangeTrusted(selectAct, sessionId, runId));
        Assert.True(selectActAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Could not select Act on the existing quiescent Run: {selectActAck.Error}/{selectActAck.Outcome?.Reason}");
        var runIdAfterModeSelection = fixture.QueryJson("humanActionScope").GetProperty("runId").GetString();
        Assert.Equal(runId, runIdAfterModeSelection);
        var catalog = await fixture.WaitForEnabledCommandAsync("fixture.native:write-marker");
        var command = Assert.Single(catalog.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "fixture.native:write-marker");
        Assert.Equal("forcedExtension", command.GetProperty("kind").GetString());
        Assert.Equal("before", await File.ReadAllTextAsync(fixture.ExtensionMarkerPath,
            TestContext.Current.CancellationToken));

        var invocationId = Guid.NewGuid().ToString("D");
        var invocation = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = command.GetProperty("canonicalCommandId").GetString(),
            arguments = new[] { "{}" },
            expectedSessionId = sessionId,
            expectedRunId = runId,
            expectedModeRevision = catalog.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = catalog.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = catalog.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(catalog.GetProperty("routingPolicyRevision")),
            catalogRevision = catalog.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = command.GetProperty("sourceFingerprint").GetString(),
        }));
        var invocationAck = Ack(fixture.ExchangeTrusted(invocation, sessionId, runId));
        Assert.True(invocationAck.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Native invocation was not admitted: status={invocationAck.Status}; error={invocationAck.Error}; "
            + $"outcome={invocationAck.Outcome?.Kind}/{invocationAck.Outcome?.Reason}; "
            + $"receipt={fixture.ReadReceipt(invocationId)?.State}/{fixture.ReadReceipt(invocationId)?.OutcomeKind}; "
            + $"events={fixture.EventSummary(sessionId)}");

        await fixture.Provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        var forcedRequest = JsonDocument.Parse(fixture.ProviderBodies.ToArray()[1]);
        var wireToolName = "omni_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("ext.fixture.native.mutate"))).ToLowerInvariant()[..48];
        var providerToolNames = forcedRequest.RootElement.GetProperty("tools").EnumerateArray()
            .Where(tool => tool.GetProperty("type").GetString() == "function")
            .Select(tool => tool.GetProperty("name").GetString()).ToArray();
        var choiceName = forcedRequest.RootElement.TryGetProperty("tool_choice", out var toolChoice)
            && toolChoice.ValueKind == JsonValueKind.Object && toolChoice.TryGetProperty("name", out var name)
            ? name.GetString() : null;
        var diagnosticTools = fixture.QueryJson("tools");
        var exactDiagnosticTool = diagnosticTools.GetProperty("tools").EnumerateArray().FirstOrDefault(item =>
            item.GetProperty("visibleName").GetString() == "ext.fixture.native.mutate");
        var boundaryDiagnostic = exactDiagnosticTool.ValueKind == JsonValueKind.Object
            ? $"toolCatalogMode={diagnosticTools.GetProperty("mode").GetString()}; visible={exactDiagnosticTool.GetProperty("visible").GetBoolean()}; decision={exactDiagnosticTool.GetProperty("decision").GetString()}"
            : "exactTool=not_in_diagnostic_catalog";
        var sessionSecurity = fixture.QueryJson("sessionSecurity");
        var sessionPolicySummary = $"sessionCategory={sessionSecurity.GetProperty("category").GetString()}; sessionCaps="
            + string.Join(",", sessionSecurity.GetProperty("capabilityCeiling").EnumerateArray().Select(item => item.GetString()));
        var modelPolicySummary = fixture.ModelPolicySummary();
        Assert.True(providerToolNames.Contains(wireToolName, StringComparer.Ordinal),
            $"Forced extension was not present in provider tool definitions: expected={wireToolName}; choice={choiceName}; "
            + boundaryDiagnostic + "; " + sessionPolicySummary + "; " + modelPolicySummary
            + "; providerTools=" + string.Join(",", providerToolNames));
        Assert.True(choiceName == wireToolName, $"Exact tool choice mismatch: expected={wireToolName}; actual={choiceName}; "
            + boundaryDiagnostic + "; providerTools=" + string.Join(",", providerToolNames));
        if (!providerInvokesTool)
        {
            await fixture.WaitForInvocationAndTurnAsync(sessionId, runId, invocationId,
                expectedOutcomeCode: "failed");
            var terminalEvents = fixture.ReadEvents(sessionId);
            Assert.Single(terminalEvents, item => item.Payload is NativeCommandInvocationStarted started
                && started.InvocationId.ToString() == invocationId);
            Assert.Single(terminalEvents, item => item.Payload is NativeCommandInvocationFinished finished
                && finished.InvocationId.ToString() == invocationId && finished.OutcomeCode == "failed");
            Assert.DoesNotContain(terminalEvents, item => item.Payload is ToolCallRequested requested
                && requested.ToolName == "ext.fixture.native.mutate");
            Assert.Equal("before", await File.ReadAllTextAsync(fixture.ExtensionMarkerPath,
                TestContext.Current.CancellationToken));
            Assert.Equal(2, fixture.Provider.RequestCount);
            var recovery = fixture.QueryJson("command.recovery");
            Assert.Single(recovery.GetProperty("invocations").EnumerateArray(), item =>
                item.GetProperty("invocationId").GetString() == invocationId
                && item.GetProperty("state").GetString() == "blocked");
            Assert.Equal("Accepted", fixture.ReadReceipt(invocationId)?.OutcomeKind);
            return;
        }
        var (interaction, toolCall, turnId) = await fixture.WaitForExtensionPermissionAsync(sessionId, runId);
        await fixture.WaitForInvocationDeferredAsync(invocationId);
        Assert.Equal("before", await File.ReadAllTextAsync(fixture.ExtensionMarkerPath,
            TestContext.Current.CancellationToken));
        Assert.Contains("allow_once", interaction.OptionsJson, StringComparison.Ordinal);
        Assert.Contains("deny", interaction.OptionsJson, StringComparison.Ordinal);
        var permissionEvaluation = Assert.Single(fixture.ReadEvents(sessionId), item =>
            item.Payload is PermissionEvaluated evaluated && evaluated.ToolCallId == toolCall.ToolCallId);
        Assert.Equal(PermissionDecision.Ask,
            Assert.IsType<PermissionEvaluated>(permissionEvaluation.Payload).Decision);

        var security = fixture.QueryJson("sessionSecurity");
        var policyRevision = security.GetProperty("revision").GetInt64();
        var responseId = Guid.NewGuid().ToString("D");
        var response = WireEnvelope.Command(responseId, JsonSerializer.Serialize(new
        {
            cmd = "interaction.respond",
            responseType = "choice",
            interactionId = interaction.InteractionId.ToString(),
            optionId,
            expectedRunId = runId,
            expectedPolicyRevision = policyRevision,
        }));
        var responseAck = Ack(fixture.ExchangeTrusted(response, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, responseAck.Outcome?.Kind);

        await fixture.Provider.WaitForRequestAsync(3, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.WaitForInvocationAndTurnAsync(sessionId, runId, invocationId);

        var events = fixture.ReadEvents(sessionId);
        Assert.Single(events, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.Single(events, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId
            && finished.OutcomeCode == (shouldWriteMarker ? "completed" : "failed"));
        var toolCallEnvelope = Assert.Single(events, item => item.Payload is ToolCallRequested requested
            && requested.ToolCallId == toolCall.ToolCallId);
        Assert.Equal(runId, toolCallEnvelope.Envelope.RunId?.ToString());
        Assert.Equal(turnId, toolCallEnvelope.Envelope.TurnId);
        var runInputs = events.Where(item => item.Envelope.RunId?.ToString() == runId
            && item.Payload is UserInputReceived).ToArray();
        Assert.Equal(2, runInputs.Length); // Initial setup plus exactly one native command prompt.
        Assert.Single(runInputs, item => item.Envelope.TurnId == turnId);
        Assert.Single(events, item => item.Payload is PermissionRequested requested
            && requested.ToolCallId == toolCall.ToolCallId && requested.InteractionId == interaction.InteractionId);
        if (shouldWriteMarker)
        {
            Assert.Equal("before", await File.ReadAllTextAsync(fixture.ExtensionMarkerPath,
                TestContext.Current.CancellationToken));
            Assert.Single(events, item => item.Payload is PermissionGranted granted
                && granted.ToolCallId == toolCall.ToolCallId);
            var started = Assert.Single(events, item => item.Payload is ToolCallStarted started
                && started.ToolCallId == toolCall.ToolCallId);
            Assert.Equal(runId, started.Envelope.RunId?.ToString());
            Assert.Equal(turnId, started.Envelope.TurnId);
            var success = Assert.Single(events, item => item.Payload is ToolCallSucceeded succeeded
                && succeeded.ToolCallId == toolCall.ToolCallId);
            Assert.NotNull(Assert.IsType<ToolCallSucceeded>(success.Payload).ResultArtifact);
        }
        else
        {
            Assert.Equal("before", await File.ReadAllTextAsync(fixture.ExtensionMarkerPath,
                TestContext.Current.CancellationToken));
            Assert.Single(events, item => item.Payload is PermissionDenied denied
                && denied.ToolCallId == toolCall.ToolCallId);
            Assert.DoesNotContain(events, item => item.Payload is ToolCallStarted started
                && started.ToolCallId == toolCall.ToolCallId);
            Assert.DoesNotContain(events, item => item.Payload is ToolCallSucceeded succeeded
                && succeeded.ToolCallId == toolCall.ToolCallId);
        }
        Assert.Equal(3, fixture.Provider.RequestCount);
        Assert.Single(events, item => item.Payload is NativeCommandInvocationAdmitted admitted
            && admitted.InvocationId.ToString() == invocationId);
    }

    private sealed partial class NativeCommandHandoffFixture
    {
        private const string ExtensionMarkerName = "m9-extension-marker.txt";

        public string ExtensionMarkerPath => Path.Combine(Workspace, ExtensionMarkerName);

        public void ConfigureStrongExtensionPackage()
        {
            var package = Path.Combine(Workspace, ".omnicore", "extensions", "fixture.native");
            Directory.CreateDirectory(package);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "OmniCore.SandboxTestProcess.*"))
                File.Copy(file, Path.Combine(package, Path.GetFileName(file)), overwrite: true);
            var sandboxAssembly = Path.Combine(AppContext.BaseDirectory, "OmniCore.Sandbox.dll");
            if (File.Exists(sandboxAssembly))
                File.Copy(sandboxAssembly, Path.Combine(package, Path.GetFileName(sandboxAssembly)), overwrite: true);
            File.WriteAllText(Path.Combine(package, "schema.json"), "{\"type\":\"object\"}", new UTF8Encoding(false));
            var manifest = $$"""
                manifestVersion: 1
                id: fixture.native
                version: 1.0.0
                displayName: Native command fixture
                publisher: tests
                omnicoreApi: '1.0'
                provides:
                  tools:
                    - name: mutate
                      schema: schema.json
                  commands:
                    - name: write-marker
                      kind: Extension
                      tool: mutate
                requests:
                  capabilities: [tool.register, command.register]
                  permissions:
                    process:
                      - executable: OmniCore.SandboxTestProcess.exe
                    paths:
                      write: [m9-extension-marker.txt]
                runtime:
                  kind: process
                  command: OmniCore.SandboxTestProcess.exe
                  args: [extension-rpc-write-marker, m9-extension-marker.txt]
                """;
            File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), manifest,
                new UTF8Encoding(false));
            File.WriteAllText(ExtensionMarkerPath, "before", new UTF8Encoding(false));

            var paths = OmniHost.CreatePlatformPaths();
            new WorkspaceTrustStore(paths).SetTrusted(Workspace, true);
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(
                Path.Combine(Workspace, ".omnicore", "extensions"), TrustLevel.Project,
                workspaceTrusted: true));
            Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, extension.Status);
            new ExtensionConsentStore(paths, Workspace).Grant(extension,
                new HashSet<string>(["tool.register", "command.register"], StringComparer.Ordinal));
        }

        public async Task<(InteractionRequested Interaction, ToolCallRequested ToolCall, TurnId TurnId)>
            WaitForExtensionPermissionAsync(string sessionId, string runId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            var expectedRun = RunId.Parse(runId);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var events = ReadEvents(sessionId).Where(item => item.Envelope.RunId == expectedRun).ToArray();
                var request = events.SingleOrDefault(item => item.Payload is InteractionRequested interaction
                    && interaction.Kind == InteractionKind.Permission
                    && events.Any(candidate => candidate.Payload is PermissionRequested permission
                        && permission.InteractionId == interaction.InteractionId));
                if (request.Payload is InteractionRequested interactionRequest
                    && request.Envelope.TurnId is { } turnId)
                {
                    var permission = Assert.Single(events, item => item.Payload is PermissionRequested value
                        && value.InteractionId == interactionRequest.InteractionId);
                    var permissionPayload = Assert.IsType<PermissionRequested>(permission.Payload);
                    var toolCall = Assert.Single(events, item => item.Payload is ToolCallRequested requested
                        && requested.ToolCallId == permissionPayload.ToolCallId
                        && requested.ToolName == "ext.fixture.native.mutate");
                    Assert.Equal(turnId, toolCall.Envelope.TurnId);
                    return (interactionRequest, Assert.IsType<ToolCallRequested>(toolCall.Payload), turnId);
                }
                var requestedCall = events.FirstOrDefault(item => item.Payload is ToolCallRequested requested
                    && requested.ToolName == "ext.fixture.native.mutate");
                if (requestedCall.Payload is ToolCallRequested call && events.FirstOrDefault(item =>
                    item.Payload is ToolCallRejected rejected && rejected.ToolCallId == call.ToolCallId).Payload is ToolCallRejected rejected)
                {
                    var scope = QueryJson("humanActionScope");
                    var tools = QueryJson("tools");
                    var exactTool = tools.GetProperty("tools").EnumerateArray().FirstOrDefault(item =>
                        item.GetProperty("visibleName").GetString() == "ext.fixture.native.mutate");
                    var toolDecision = exactTool.ValueKind == JsonValueKind.Object
                        ? $"mode={tools.GetProperty("mode").GetString()}; visible={exactTool.GetProperty("visible").GetBoolean()}; decision={exactTool.GetProperty("decision").GetString()}"
                        : "exactTool=not_in_diagnostic_catalog";
                    throw new InvalidOperationException("The forced extension call was rejected before its permission interaction: "
                        + "errorCode=" + rejected.ErrorCode + "; reason=" + rejected.Reason + "; " + toolDecision
                        + "; runMode=" + NullableJsonString(scope.GetProperty("mode")) + "; " + EventSummary(sessionId));
                }
                if (events.Any(item => item.Payload is RunCompleted) && requestedCall.Payload is ToolCallRequested completedCall)
                    throw new InvalidOperationException("The Run completed without a permission interaction for the forced extension call: "
                        + "toolCall=" + completedCall.ToolCallId + "; " + EventSummary(sessionId));
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            var summary = string.Join(";", ReadEvents(sessionId).Where(item => item.Envelope.RunId == expectedRun)
                .Select(item => $"{item.Envelope.Sequence}:{item.Payload.GetType().Name}:{item.Envelope.TurnId}"));
            throw new TimeoutException("The Host did not persist the real extension permission request. providerRequests="
                + Provider.RequestCount + "; diagnostics=" + string.Join(",", Diagnostics) + "; events=" + summary);
        }

        public async Task WaitForInvocationDeferredAsync(string invocationId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var receipt = ReadReceipt(invocationId);
                if (receipt is { State: CommandReceiptState.Completed, OutcomeKind: "Accepted",
                    ExecutionState: "deferred", ExecutionCode: "input_required" }) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var finalReceipt = ReadReceipt(invocationId);
            throw new TimeoutException("The native extension invocation did not reach its durable permission wait. receipt="
                + $"{finalReceipt?.State}/{finalReceipt?.OutcomeKind}/{finalReceipt?.ExecutionState}/{finalReceipt?.ExecutionCode}; "
                + "providerRequests=" + Provider.RequestCount);
        }

        public async Task<JsonElement> WaitForEnabledCommandAsync(string canonicalCommandId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            JsonElement catalog = default;
            JsonElement command = default;
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                catalog = QueryJson("command.catalog");
                var match = catalog.GetProperty("commands").EnumerateArray()
                    .FirstOrDefault(item => item.GetProperty("canonicalCommandId").GetString() == canonicalCommandId);
                if (match.ValueKind == JsonValueKind.Object)
                {
                    command = match;
                    if (command.GetProperty("enabled").GetBoolean()) return catalog;
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("The native extension command never became enabled after the setup Turn. "
                + $"session={catalog.GetProperty("sessionId").GetString()}; run={catalog.GetProperty("runId").GetString()}; "
                + $"modeRevision={catalog.GetProperty("modeRevision")}; policyRevision={catalog.GetProperty("policyRevision")}; "
                + $"selectionRevision={catalog.GetProperty("selectionRevision")}; command={command}; "
                + $"events={EventSummary(catalog.GetProperty("sessionId").GetString()!)}");
        }

        public string EventSummary(string sessionId) => string.Join(",", ReadEvents(sessionId).Select(item =>
            $"{item.Envelope.Sequence}:{item.Payload.GetType().Name}:run={item.Envelope.RunId?.ToString() ?? "-"}:turn={item.Envelope.TurnId?.ToString() ?? "-"}"
            + (item.Payload is ToolCallRejected rejected ? $":code={rejected.ErrorCode}:reason={rejected.Reason}" : "")));
    }
}
