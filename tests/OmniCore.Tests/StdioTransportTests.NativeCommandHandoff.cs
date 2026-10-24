using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Sandbox;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_orchestrate_selection_reads_back_the_canonical_orq_wire_mode()
    {
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"));
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync("plan");
        var before = fixture.QueryJson("humanActionScope");
        var runId = before.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The setup turn did not leave an active Run.");
        var defaultMode = fixture.QueryJson("modePreference").GetProperty("mode").GetString();

        var selection = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select",
            runId,
            mode = "orq",
            expectedRevision = before.GetProperty("revision").GetInt64(),
            effort = "ultracode",
            adaptive = true,
            allowedModes = "plan,act,orq",
            maxAgents = 2,
            maxDepth = 1,
            maxTurns = 8,
            maxToolCalls = 12,
            maxElapsedSeconds = 900,
            maxSpendUsd = 1m,
        }));
        var ack = Ack(fixture.ExchangeTrusted(selection, sessionId, runId));
        Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"Orchestrate selection failed: status={ack.Status}, error={ack.Error}, reason={ack.Outcome?.Reason}");

        var scope = fixture.QueryJson("humanActionScope");
        Assert.Equal(sessionId, scope.GetProperty("sessionId").GetString());
        Assert.Equal(runId, scope.GetProperty("runId").GetString());
        Assert.Equal("orq", scope.GetProperty("mode").GetString());

        var authority = fixture.QueryJson("modeAuthority");
        Assert.Equal(runId, authority.GetProperty("runId").GetString());
        Assert.Equal("orq", authority.GetProperty("mode").GetString());
        Assert.Contains("orq", authority.GetProperty("authorization").GetProperty("allowedModes")
            .EnumerateArray().Select(item => item.GetString()));

        // Selecting a Run mode must not mutate the separate user default preference.
        Assert.Equal(defaultMode, fixture.QueryJson("modePreference").GetProperty("mode").GetString());
        Assert.Equal(1, fixture.Provider.RequestCount);
    }

    [Fact]
    public async Task First_native_explain_admits_a_blank_session_and_stale_scopes_never_reach_the_provider()
    {
        var invocationId = Guid.NewGuid().ToString("D");
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(Guid.NewGuid().ToString("D"));
        var sessionId = fixture.CreateEmptySession();
        var before = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, before.GetProperty("sessionId").GetString());
        Assert.Null(NullableJsonString(before.GetProperty("runId")));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("modeRevision").ValueKind);
        var explain = Assert.Single(before.GetProperty("commands").EnumerateArray(),
            item => item.GetProperty("canonicalCommandId").GetString() == "core:explain");
        Assert.True(explain.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, fixture.ReadEvents(sessionId).Count(item => item.Payload is UserInputReceived));

        WireEnvelope Invoke(string commandId, string expectedSessionId, long expectedPolicyRevision,
            string sourceFingerprint)
        {
            var command = WireEnvelope.Command(commandId, JsonSerializer.Serialize(new
            {
                cmd = "command.invoke.user",
                canonicalCommandId = "core:explain",
                arguments = new[] { "README.md" },
                expectedSessionId,
                expectedRunId = (string?)null,
                expectedModeRevision = (long?)null,
                expectedPolicyRevision,
                expectedSelectionRevision = before.GetProperty("selectionRevision").GetInt32(),
                expectedRoutingPolicyRevision = NullableJsonInt64(before.GetProperty("routingPolicyRevision")),
                catalogRevision = before.GetProperty("catalogRevision").GetString(),
                sourceFingerprint,
            }));
            return fixture.ExchangeTrusted(command, expectedSessionId, null);
        }

        var stalePolicy = Ack(Invoke(Guid.NewGuid().ToString("D"), sessionId,
            before.GetProperty("policyRevision").GetInt64() + 1, explain.GetProperty("sourceFingerprint").GetString()!));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted, stalePolicy.Outcome?.Kind);

        var staleFingerprint = Ack(Invoke(Guid.NewGuid().ToString("D"), sessionId,
            before.GetProperty("policyRevision").GetInt64(), new string('c', 64)));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted, staleFingerprint.Outcome?.Kind);

        var otherSessionId = Guid.NewGuid().ToString("D");
        var wrongSession = Ack(Invoke(Guid.NewGuid().ToString("D"), otherSessionId,
            before.GetProperty("policyRevision").GetInt64(), explain.GetProperty("sourceFingerprint").GetString()!));
        Assert.NotEqual(RuntimeCommandOutcomeKind.Accepted, wrongSession.Outcome?.Kind);

        var nullRunContinue = WireEnvelope.Command(Guid.NewGuid().ToString("D"), JsonSerializer.Serialize(new
        {
            cmd = "command.continue",
            invocationId = Guid.NewGuid().ToString("D"),
            expectedSessionId = sessionId,
            expectedRunId = (string?)null,
            expectedModeRevision = (long?)null,
            expectedPolicyRevision = before.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = before.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(before.GetProperty("routingPolicyRevision")),
            catalogRevision = before.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = explain.GetProperty("sourceFingerprint").GetString(),
        }));
        var invalidContinue = Ack(fixture.ExchangeTrusted(nullRunContinue, sessionId, null));
        Assert.Equal("trusted_action_invalid", invalidContinue.Error);

        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Payload is NativeCommandInvocationAdmitted);
        Assert.Equal(0, fixture.Provider.RequestCount);
        Assert.Equal(0, fixture.ReadEvents(sessionId).Count(item => item.Payload is UserInputReceived));

        var validInvoke = Ack(Invoke(invocationId, sessionId,
            before.GetProperty("policyRevision").GetInt64(), explain.GetProperty("sourceFingerprint").GetString()!));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, validInvoke.Outcome?.Kind);
        var admissions = fixture.ReadEvents(sessionId).Where(item => item.Payload is NativeCommandInvocationAdmitted).ToArray();
        var admissionEnvelope = Assert.Single(admissions);
        var admission = Assert.IsType<NativeCommandInvocationAdmitted>(admissionEnvelope.Payload);
        Assert.Equal(invocationId, admission.InvocationId.ToString());
        Assert.Equal(sessionId, admission.SessionId.ToString());
        Assert.NotNull(admission.RunId);
        Assert.Contains(admission.ProtectedPayload, admissionEnvelope.Envelope.ArtifactRefs);
        Assert.DoesNotContain("README.md", admissionEnvelope.Envelope.PayloadJson, StringComparison.Ordinal);
        var runId = admission.RunId!.Value.ToString();

        await fixture.Provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.WaitForInvocationAndTurnAsync(sessionId, runId, invocationId, minimumCompletedTurns: 1);

        var events = fixture.ReadEvents(sessionId);
        var graph = events.Where(item => item.Envelope.RunId?.ToString() == runId).ToArray();
        Assert.Single(graph, item => item.Payload is RunCreated);
        Assert.Single(graph, item => item.Payload is TaskCreated);
        Assert.Single(graph, item => item.Payload is LaneCreated);
        Assert.Single(graph, item => item.Payload is PlanCreated);
        Assert.Single(graph, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.Single(graph, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId && finished.OutcomeCode == "completed");
        Assert.Equal(1, events.Count(item => item.Payload is UserInputReceived input
            && input.RunId.ToString() == runId));
        Assert.Contains("Explain the repository content at the path README.md", fixture.ProviderBodies.Single(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_command_ack_cut_recovers_ready_invocation_only_after_explicit_continue()
    {
        var invocationId = Guid.NewGuid().ToString("D");
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(invocationId);
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync();
        var before = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, before.GetProperty("sessionId").GetString());
        Assert.NotNull(before.GetProperty("runId").GetString());
        var explain = Assert.Single(before.GetProperty("commands").EnumerateArray(),
            item => item.GetProperty("canonicalCommandId").GetString() == "core:explain");
        Assert.Equal("prompt", explain.GetProperty("kind").GetString());
        Assert.True(explain.GetProperty("enabled").GetBoolean());
        var userInputCountBeforeInvocation = fixture.ReadEvents(sessionId)
            .Count(item => item.Payload is UserInputReceived);
        Assert.Equal(1, userInputCountBeforeInvocation);

        var invocation = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = explain.GetProperty("canonicalCommandId").GetString(),
            arguments = new[] { "README.md" },
            expectedSessionId = sessionId,
            expectedRunId = before.GetProperty("runId").GetString(),
            expectedModeRevision = before.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = before.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = before.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(before.GetProperty("routingPolicyRevision")),
            catalogRevision = before.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = explain.GetProperty("sourceFingerprint").GetString(),
        }));

        var ackEnvelope = fixture.ExchangeTrusted(invocation, sessionId,
            before.GetProperty("runId").GetString());
        var ack = Ack(ackEnvelope);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        Assert.Equal(invocationId, ack.CommandId);
        await fixture.FailAckWriteAndStopHostAsync();
        Assert.Equal(1, fixture.Provider.RequestCount); // No provider request before the ACK writer returns.

        var admission = Assert.Single(fixture.ReadEvents(sessionId),
            item => item.Payload is NativeCommandInvocationAdmitted);
        var admissionPayload = Assert.IsType<NativeCommandInvocationAdmitted>(admission.Payload);
        Assert.Equal(invocationId, admissionPayload.InvocationId.ToString());
        Assert.Contains(admissionPayload.ProtectedPayload, admission.Envelope.ArtifactRefs);
        Assert.DoesNotContain("README.md", admission.Envelope.PayloadJson, StringComparison.Ordinal);
        var initialReceipt = fixture.ReadReceipt(invocationId);
        Assert.Equal(CommandReceiptState.Completed, initialReceipt?.State);
        Assert.Equal("Accepted", initialReceipt?.OutcomeKind);
        Assert.Null(initialReceipt?.ExecutionState);
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item =>
            item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.Equal(userInputCountBeforeInvocation, fixture.ReadEvents(sessionId)
            .Count(item => item.Payload is UserInputReceived));

        // The complete ACK reached the client, but the transport failed before WriteFrame
        // returned and before StdioProtocolHost called StartPendingAfterAck.
        Assert.Equal(1, fixture.Provider.RequestCount);
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item =>
            item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);

        await fixture.ReopenHostAsync();
        var recovery = fixture.QueryJson("command.recovery");
        var ready = Assert.Single(recovery.GetProperty("invocations").EnumerateArray(),
            item => item.GetProperty("invocationId").GetString() == invocationId);
        Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.DoesNotContain("README.md", recovery.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Provider.RequestCount); // Query/replay cannot wake a ready command.

        var current = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, current.GetProperty("sessionId").GetString());
        Assert.Equal(before.GetProperty("runId").GetString(), current.GetProperty("runId").GetString());
        var continueCommand = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "command.continue",
            invocationId,
            expectedSessionId = sessionId,
            expectedRunId = current.GetProperty("runId").GetString(),
            expectedModeRevision = current.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = current.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = current.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(current.GetProperty("routingPolicyRevision")),
            catalogRevision = current.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = explain.GetProperty("sourceFingerprint").GetString(),
        }));
        var continueAck = Ack(fixture.ExchangeTrusted(continueCommand, sessionId,
            current.GetProperty("runId").GetString()));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, continueAck.Outcome?.Kind);
        await fixture.Provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await fixture.WaitForInvocationAndTurnAsync(sessionId,
            current.GetProperty("runId").GetString()!, invocationId);

        var requestBodies = fixture.ProviderBodies.ToArray();
        Assert.Equal(2, requestBodies.Length);
        Assert.Contains("Explain the repository content at the path README.md", requestBodies[1],
            StringComparison.Ordinal);
        var invocationEvents = fixture.ReadEvents(sessionId);
        Assert.Single(invocationEvents, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.Single(invocationEvents, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId
            && finished.OutcomeCode == "completed");
        Assert.Equal(userInputCountBeforeInvocation + 1,
            invocationEvents.Count(item => item.Payload is UserInputReceived));

        // Replaying the same continuation receipt is idempotent and never schedules another request.
        var duplicate = Ack(fixture.ExchangeTrusted(continueCommand, sessionId,
            current.GetProperty("runId").GetString()));
        Assert.Equal(continueAck.FirstSeq, duplicate.FirstSeq);
        Assert.Equal(continueAck.LastSeq, duplicate.LastSeq);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, duplicate.Outcome?.Kind);
        Assert.Equal(2, fixture.Provider.RequestCount);
    }

    [Fact]
    public async Task Native_workflow_ack_cut_stays_passive_until_explicit_continue_and_duplicate_is_idempotent()
    {
        var invocationId = Guid.NewGuid().ToString("D");
        await using var fixture = await NativeCommandHandoffFixture.StartAsync(invocationId,
            includeWorkflowAgentProfile: true);
        var sessionId = await fixture.CreateSessionAndQuiescentRunAsync();
        var beforeMode = fixture.QueryJson("humanActionScope");
        var runId = beforeMode.GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("The normal setup turn did not create a Run.");
        var security = fixture.QueryJson("sessionSecurity");
        Assert.Equal(sessionId, security.GetProperty("sessionId").GetString());
        Assert.True(security.GetProperty("allowOrchestration").GetBoolean());
        Assert.Equal(1, fixture.Provider.RequestCount);

        var mode = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "run.mode.select",
            runId,
            mode = "orq",
            expectedRevision = beforeMode.GetProperty("revision").GetInt64(),
            effort = "ultracode",
            adaptive = true,
            allowedModes = "plan,act,orq",
            maxAgents = 2,
            maxDepth = 1,
            maxTurns = 8,
            maxToolCalls = 12,
            maxElapsedSeconds = 900,
            maxSpendUsd = 1m,
        }));
        var modeAck = Ack(fixture.ExchangeTrusted(mode, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, modeAck.Outcome?.Kind);
        var current = fixture.QueryJson("command.catalog");
        Assert.Equal(sessionId, current.GetProperty("sessionId").GetString());
        Assert.Equal(runId, current.GetProperty("runId").GetString());
        Assert.Equal("orq", fixture.QueryJson("humanActionScope").GetProperty("mode").GetString());
        var workflow = Assert.Single(current.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("canonicalCommandId").GetString() == "core:orq-auth");
        Assert.True(workflow.GetProperty("enabled").GetBoolean(),
            $"The real workflow command was not enabled: {workflow.GetProperty("disabledReason").GetString() ?? "no reason"}.");

        var executable = new SystemExecutableResolver().Resolve("dotnet", fixture.Workspace).ResolvedPath;
        var arguments = new[]
        {
            "Inspect the repository and report the entry point",
            "--verify-executable", executable,
            "--verify-argv-json", JsonSerializer.Serialize(new[] { "--version" }),
        };
        var invoke = WireEnvelope.Command(invocationId, JsonSerializer.Serialize(new
        {
            cmd = "command.invoke.user",
            canonicalCommandId = workflow.GetProperty("canonicalCommandId").GetString(),
            arguments,
            expectedSessionId = sessionId,
            expectedRunId = runId,
            expectedModeRevision = current.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = current.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = current.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(current.GetProperty("routingPolicyRevision")),
            catalogRevision = current.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = workflow.GetProperty("sourceFingerprint").GetString(),
        }));
        var invokeAck = Ack(fixture.ExchangeTrusted(invoke, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, invokeAck.Outcome?.Kind);
        Assert.Equal(invocationId, invokeAck.CommandId);
        Assert.Equal(1, fixture.Provider.RequestCount);
        await fixture.FailAckWriteAndStopHostAsync();

        var eventsBeforeRecovery = fixture.ReadEvents(sessionId);
        var admissionEvent = Assert.Single(eventsBeforeRecovery, item => item.Payload is NativeCommandInvocationAdmitted);
        var admission = Assert.IsType<NativeCommandInvocationAdmitted>(admissionEvent.Payload);
        Assert.Equal(invocationId, admission.InvocationId.ToString());
        Assert.Equal("compiledWorkflow", admission.Kind);
        Assert.Equal(sessionId, admission.SessionId.ToString());
        Assert.Equal(runId, admission.RunId?.ToString());
        Assert.Contains(admission.ProtectedPayload, admissionEvent.Envelope.ArtifactRefs);
        Assert.DoesNotContain("Inspect the repository", admissionEvent.Envelope.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(eventsBeforeRecovery, item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        Assert.DoesNotContain(eventsBeforeRecovery, item => item.Payload is PlanItemAdded);
        var invokeReceipt = fixture.ReadReceipt(invocationId);
        Assert.Equal(CommandReceiptState.Completed, invokeReceipt?.State);
        Assert.Equal("Accepted", invokeReceipt?.OutcomeKind);
        Assert.Null(invokeReceipt?.ExecutionState);
        Assert.Equal(1, fixture.Provider.RequestCount);

        await fixture.ReopenHostAsync();
        var recovered = fixture.QueryJson("command.recovery");
        var ready = Assert.Single(recovered.GetProperty("invocations").EnumerateArray(), item =>
            item.GetProperty("invocationId").GetString() == invocationId);
        Assert.Equal("compiledWorkflow", ready.GetProperty("kind").GetString());
        Assert.Equal("ready", ready.GetProperty("state").GetString());
        Assert.DoesNotContain("Inspect the repository", recovered.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Provider.RequestCount); // Recovery reads and replay remain passive.
        Assert.DoesNotContain(fixture.ReadEvents(sessionId), item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);

        var refreshed = fixture.QueryJson("command.catalog");
        var continueCommand = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "command.continue",
            invocationId,
            expectedSessionId = sessionId,
            expectedRunId = refreshed.GetProperty("runId").GetString(),
            expectedModeRevision = refreshed.GetProperty("modeRevision").GetInt64(),
            expectedPolicyRevision = refreshed.GetProperty("policyRevision").GetInt64(),
            expectedSelectionRevision = refreshed.GetProperty("selectionRevision").GetInt32(),
            expectedRoutingPolicyRevision = NullableJsonInt64(refreshed.GetProperty("routingPolicyRevision")),
            catalogRevision = refreshed.GetProperty("catalogRevision").GetString(),
            sourceFingerprint = workflow.GetProperty("sourceFingerprint").GetString(),
        }));
        var continueAck = Ack(fixture.ExchangeTrusted(continueCommand, sessionId, runId));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, continueAck.Outcome?.Kind);
        Assert.Equal(continueCommand.MessageId, continueAck.CommandId);
        await WaitForInvocationStartedAsync(fixture, sessionId, invocationId);
        await WaitForInvocationFinishedAsync(fixture, sessionId, invocationId);

        var countAfterExplicitContinue = fixture.Provider.RequestCount;
        var eventsAfterExplicitContinue = fixture.ReadEvents(sessionId);
        var completionGateCount = eventsAfterExplicitContinue.Count(item => item.Payload is PlanItemAdded added
            && added.Description.StartsWith("Workflow completion gate:", StringComparison.Ordinal));
        var duplicate = Ack(fixture.ExchangeTrusted(continueCommand, sessionId, runId));
        Assert.Equal(continueAck.FirstSeq, duplicate.FirstSeq);
        Assert.Equal(continueAck.LastSeq, duplicate.LastSeq);
        Assert.Equal(continueAck.Outcome?.Kind, duplicate.Outcome?.Kind);
        Assert.Equal(countAfterExplicitContinue, fixture.Provider.RequestCount);
        Assert.Single(fixture.ReadEvents(sessionId), item => item.Payload is NativeCommandInvocationStarted started
            && started.InvocationId.ToString() == invocationId);
        var eventsAfterDuplicate = fixture.ReadEvents(sessionId);
        Assert.Equal(completionGateCount, eventsAfterDuplicate.Count(item => item.Payload is PlanItemAdded added
            && added.Description.StartsWith("Workflow completion gate:", StringComparison.Ordinal)));
        Assert.Single(eventsAfterDuplicate, item => item.Payload is NativeCommandInvocationFinished finished
            && finished.InvocationId.ToString() == invocationId);
    }

    private static async Task WaitForInvocationStartedAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string invocationId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (fixture.ReadEvents(sessionId).Any(item => item.Payload is NativeCommandInvocationStarted started
                && started.InvocationId.ToString() == invocationId)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Explicit workflow continuation ACK did not start the admitted invocation.");
    }

    private static async Task WaitForInvocationFinishedAsync(NativeCommandHandoffFixture fixture,
        string sessionId, string invocationId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var events = fixture.ReadEvents(sessionId);
            if (events.Any(item => item.Payload is NativeCommandInvocationFinished finished
                && finished.InvocationId.ToString() == invocationId)) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Explicit workflow continuation did not reach a durable terminal invocation event.");
    }

    private static long? NullableJsonInt64(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();

    private sealed partial class NativeCommandHandoffFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string? _oldData;
        private readonly string? _oldConfig;
        private readonly string _oldDirectory;
        private readonly byte[] _key;
        private readonly SemaphoreSlim _providerBodyArrived = new(0);
        private OmniCliRuntime? _runtime;
        private OmniServer? _server;
        private RecoveryDuplex? _duplex;
        private Task<int>? _hostTask;
        private string? _nonce;
        private bool _disposed;
        private bool _includeNativeChoiceCapability = true;

        private NativeCommandHandoffFixture(string root, string workspace,
            string? oldData, string? oldConfig, string oldDirectory, byte[] key,
            CliEndToEndTests.ScriptedHttpProvider provider)
        {
            _root = root;
            Workspace = workspace;
            _oldData = oldData;
            _oldConfig = oldConfig;
            _oldDirectory = oldDirectory;
            _key = key;
            Provider = provider;
            ProviderBodies = new ConcurrentQueue<string>();
            Diagnostics = new ConcurrentQueue<string>();
            DetailedDiagnostics = new ConcurrentQueue<string>();
            CutStream = null!;
        }

        public string Workspace { get; }
        public OmniServer Server => _server ?? throw new InvalidOperationException("The native Host is not connected.");
        public CliEndToEndTests.ScriptedHttpProvider Provider { get; }
        public ConcurrentQueue<string> ProviderBodies { get; }
        public ConcurrentQueue<string> Diagnostics { get; }
        public ConcurrentQueue<string> DetailedDiagnostics { get; }
        public bool PreserveRootOnDispose { get; set; }
        public Func<CancellationToken, Task>? NativeProcessControlDeferredForTests { get; set; }
        public Action<string>? NativeProcessControlStageForTests { get; set; }
        public AckDeliveryCutStream CutStream { get; private set; }
        public string RootDirectory => _root;
        public string JournalPath => Path.Combine(OmniHost.WorkspaceDataDirectory(
            OmniHost.CreatePlatformPaths(), Workspace), "journal.db");

        public string SaveFailureDiagnostic(string suffix, string contents)
        {
            DirectoryInfo? repository = new(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "OmniCore.slnx")))
                repository = repository.Parent;
            if (repository is null) throw new DirectoryNotFoundException("OmniCore.slnx was not found above the test output directory.");
            var directory = Path.Combine(repository.FullName, "artifacts", "m9-child-process-controls");
            Directory.CreateDirectory(directory);
            var safeSuffix = string.Concat(suffix.Where(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
            var path = Path.Combine(directory, $"diagnostic-{safeSuffix}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.log");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            PreserveRootOnDispose = true;
            return path;
        }

        public async Task WaitForProviderBodiesAsync(int count, CancellationToken cancellationToken)
        {
            while (ProviderBodies.Count < count)
                await _providerBodyArrived.WaitAsync(cancellationToken);
        }

        public string ModelPolicySummary()
        {
            using var policies = OmniHost.CreateModelPolicyService(Path.Combine(_root, "data"));
            var policy = policies.Get(ModelPolicyKey.For("scripted", "scripted-model"), CancellationToken.None);
            return policy is null ? "modelPolicy=missing" : $"modelCategory={policy.Policy.Category}; modelToolMode={policy.Policy.ToolPolicy.Mode}; modelCaps="
                + string.Join(",", policy.Policy.ToolPolicy.CapabilityCeiling.Order());
        }

        public Task<int> ExecuteDelegationAsync(string delegationId, Action<string> diagnostics,
            CancellationToken cancellationToken) => (_runtime
                ?? throw new InvalidOperationException("The native Host runtime is not connected."))
            .DelegationAsync(delegationId, diagnostics, cancellationToken);

        public void ObserveChildTurnResultsForTests(Action<ExplorerTurn.TurnResult> observer) =>
            (_runtime ?? throw new InvalidOperationException("The native Host runtime is not connected."))
                .ChildTurnResultObserverForTests = observer;

        public static async Task<NativeCommandHandoffFixture> StartAsync(string cutCommandId,
            bool includeStrongExtension = false, Func<int, string, string>? providerResponse = null,
            bool includeWorkflowAgentProfile = false,
            Func<int, string, Task<string>>? providerResponseAsync = null,
            string? permissionsYaml = null, bool allowWorkflowPersistentProcess = false,
            bool includeZeroModelPricing = false,
            Func<CancellationToken, Task>? nativeProcessControlDeferredForTests = null,
            Action<string>? nativeProcessControlStageForTests = null,
            bool includeNativeChoiceCapability = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "omni-native-command-handoff-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var config = Path.Combine(root, "config");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(data);
            var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
            var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
            var oldDirectory = Environment.CurrentDirectory;
            var key = RandomNumberGenerator.GetBytes(32);
            var provider = new CliEndToEndTests.ScriptedHttpProvider();
            var fixture = new NativeCommandHandoffFixture(root, workspace, oldData, oldConfig,
                oldDirectory, key, provider);
            fixture._includeNativeChoiceCapability = includeNativeChoiceCapability;
            fixture.NativeProcessControlDeferredForTests = nativeProcessControlDeferredForTests;
            fixture.NativeProcessControlStageForTests = nativeProcessControlStageForTests;
            void RecordProviderBody(string body)
            {
                fixture.ProviderBodies.Enqueue(body);
                fixture._providerBodyArrived.Release();
            }
            if (providerResponseAsync is not null)
                provider.RespondWithAsync(async (index, body) =>
                {
                    RecordProviderBody(body);
                    return await providerResponseAsync(index, body).ConfigureAwait(false);
                });
            else
                provider.RespondWith((index, body) =>
                {
                    RecordProviderBody(body);
                    return providerResponse?.Invoke(index, body)
                        ?? CliEndToEndTests.ResponsesTextResponse("The local native-command fixture completed this Turn.");
                });
            try
            {
                File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                    providers:
                      scripted:
                        family: OpenAIResponses
                        profile: api
                        baseUrl: {{provider.BaseUrl}}
                        auth: none
                        billingMode: Local
                    """, new UTF8Encoding(false));
                var modelConfiguration = "models:\n  scripted-model:\n    provider: scripted\n    context: 32768\n    recommendedUsableContext: 24000\n    maxOutput: 512\n    parametersBillions: 100\n";
                if (includeZeroModelPricing)
                    modelConfiguration += "    inputPricePerMillionUsd: 0\n    outputPricePerMillionUsd: 0\n";
                File.WriteAllText(Path.Combine(config, "models.yaml"), modelConfiguration, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "permissions.yaml"), permissionsYaml ?? (includeStrongExtension
                    ? "profile: balanced\nrules:\n  tool.search: allow\n  ext.fixture.native.mutate: ask\n"
                    : "profile: balanced\nrules:\n  tool.search: allow\n"), new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
                Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
                Environment.CurrentDirectory = workspace;
                if (includeStrongExtension) fixture.ConfigureStrongExtensionPackage();
                if (includeWorkflowAgentProfile) fixture.ConfigureWorkflowAgentProfile(config, allowWorkflowPersistentProcess);
                using (var policies = OmniHost.CreateModelPolicyService(data))
                    policies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
                        ModelPolicyPresets.FullAgent(), CancellationToken.None);
                var defaults = new SessionSecurityDefaultConfiguration(OmniHost.CreatePlatformPaths());
                defaults.Set(defaults.Read().Revision, ModelPolicyCategory.FullAgent, CancellationToken.None);
                fixture.StartRuntimeAndHost(cutCommandId);
                fixture.Negotiate(includeNativeChoiceCapability);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private void ConfigureWorkflowAgentProfile(string config, bool allowPersistentProcess)
        {
            var profileId = Guid.NewGuid();
            var executable = new SystemExecutableResolver().Resolve("dotnet", Workspace).ResolvedPath
                .Replace("\\", "/", StringComparison.Ordinal);
            var processRules = $"        - executablePattern: '{executable}'\n"
                + "          argvPatterns: [\"--version\"]\n          decision: Allow";
            if (allowPersistentProcess)
                processRules += "\n        - executablePattern: '**/OmniCore.SandboxTestProcess.exe'\n"
                    + "          argvPatterns: [\"stdin-state\"]\n          decision: Allow";
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), $$"""
                defaultProfile: {{profileId:D}}
                agentProfiles:
                  workflow:
                    id: {{profileId:D}}
                    revision: 1
                    permissions:
                      reads: ["**"]
                      writes: ["**"]
                      process:
                {{processRules}}
                      network: []
                      secrets: []
                      allowShell: false
                    preferredTools: [filesystem.read, filesystem.write, filesystem.patch]
                """, new UTF8Encoding(false));
        }

        public string CreateEmptySession()
        {
            var initialScope = QueryJson("humanActionScope");
            var create = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}");
            var createAck = Ack(ExchangeTrusted(create, NullableJsonString(initialScope.GetProperty("sessionId")),
                NullableJsonString(initialScope.GetProperty("runId"))));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, createAck.Outcome?.Kind);
            var selection = QueryJson("session.model.selection");
            return selection.GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException("The new Session ID was not returned.");
        }

        public async Task<string> CreateSessionAndQuiescentRunAsync(string mode = "plan")
        {
            var session = CreateEmptySession();
            var selection = QueryJson("session.model.selection");
            var start = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "conversation.start",
                text = "A short setup turn before the native command.",
                mode,
                expectedRevision = (long?)null,
                expectedPolicyRevision = selection.GetProperty("sessionPolicyRevision").GetInt64(),
                expectedDefaultRevision = (string?)null,
            }));
            var startAck = Ack(ExchangeTrusted(start, session, null));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, startAck.Outcome?.Kind);
            await Provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await WaitForTurnCompletedAsync(session);
            await WaitForReceiptExecutionAsync(start.MessageId, "completed");
            return session;
        }

        public JsonElement QueryJson(string name)
        {
            var query = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new { operation = "query", name }));
            var response = Exchange(query);
            using var payload = JsonDocument.Parse(response.PayloadJson);
            using var result = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
            return result.RootElement.Clone();
        }

        public WireEnvelope ExchangeTrusted(WireEnvelope command, string? sessionId, string? runId) =>
            Exchange(TrustedActionEnvelope(command, _key, _nonce!, Workspace, sessionId, runId));

        public WireEnvelope Exchange(WireEnvelope request)
        {
            var duplex = _duplex ?? throw new InvalidOperationException("The native Host stream is not open.");
            WireFrame.Write(duplex.ClientInput, request.ToJson());
            var response = WireFrame.Read(duplex.ClientOutput)
                ?? throw new EndOfStreamException("The native Host closed before returning a response frame.");
            return WireEnvelope.FromJson(response);
        }

        public IReadOnlyList<(DomainEvent Envelope, DomainEventPayload Payload)> ReadEvents(string sessionId)
        {
            using var store = new SqliteEventStore(JournalPath, readOnly: true);
            var codecs = EventCodecs.Create();
            return store.ReadFrom(SessionId.Parse(sessionId), 1)
                .Select(envelope => (envelope, codecs.Decode(envelope))).ToArray();
        }

        public CommandReceipt? ReadReceipt(string commandId)
        {
            using var store = new SqliteEventStore(JournalPath, readOnly: true);
            return store.ReadCommandReceipt(commandId);
        }

        public async Task WaitForInvocationAndTurnAsync(string sessionId, string runId, string invocationId,
            int minimumCompletedTurns = 2, string expectedOutcomeCode = "completed")
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var events = ReadEvents(sessionId);
                var started = events.Any(item => item.Payload is NativeCommandInvocationStarted command
                    && command.InvocationId.ToString() == invocationId);
                var finished = events.Any(item => item.Payload is NativeCommandInvocationFinished command
                    && command.InvocationId.ToString() == invocationId && command.OutcomeCode == expectedOutcomeCode);
                var expectedRun = RunId.Parse(runId);
                var turns = events.Where(item => item.Envelope.RunId == expectedRun && item.Payload is TurnStarted)
                    .Select(item => item.Envelope.TurnId).Where(id => id is not null).ToHashSet();
                var completedTurns = events.Where(item => item.Envelope.RunId == expectedRun
                        && item.Payload is TurnCompleted)
                    .Select(item => item.Envelope.TurnId).Where(id => id is not null).ToHashSet();
                if (started && finished && turns.Intersect(completedTurns).Count() >= minimumCompletedTurns) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var finalEvents = ReadEvents(sessionId);
            var eventSummary = string.Join(",", finalEvents.Select(item =>
                $"{item.Envelope.Sequence}:{item.Payload.GetType().Name}:run={item.Envelope.RunId?.ToString() ?? "-"}:turn={item.Envelope.TurnId?.ToString() ?? "-"}"));
            var finalReceipt = ReadReceipt(invocationId);
            throw new TimeoutException($"The native command did not complete its durable Run Turn. providerRequests={Provider.RequestCount}; "
                + $"started={finalEvents.Any(item => item.Payload is NativeCommandInvocationStarted command && command.InvocationId.ToString() == invocationId)}; "
                + $"finished={finalEvents.Any(item => item.Payload is NativeCommandInvocationFinished command && command.InvocationId.ToString() == invocationId)}; "
                + $"receipt={finalReceipt?.State}/{finalReceipt?.OutcomeKind}/{finalReceipt?.ExecutionState}; "
                + $"diagnostics={string.Join(",", Diagnostics)}; events={eventSummary}");
        }

        private static string SafeDiagnostic(string value) => value.Length <= 160
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':')
                ? value : "non_code_diagnostic";

        public async Task FailAckWriteAndStopHostAsync()
        {
            var hostResult = await (_hostTask ?? throw new InvalidOperationException("Host task missing."))
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(2, hostResult);
            _hostTask = null;
            CutStream.Dispose();
            _duplex?.Dispose();
            _duplex = null;
            _runtime?.Dispose();
            _runtime = null;
        }

        public Task ReopenHostAsync()
        {
            StartRuntimeAndHost(null);
            Negotiate(_includeNativeChoiceCapability);
            return Task.CompletedTask;
        }

        public async Task RestartHostAsync()
        {
            _duplex?.CompleteClientInput();
            if (_hostTask is not null)
            {
                var hostResult = await _hostTask.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(0, hostResult);
            }
            _hostTask = null;
            _duplex?.Dispose();
            _duplex = null;
            _runtime?.Dispose();
            _runtime = null;
            StartRuntimeAndHost(null);
            Negotiate(_includeNativeChoiceCapability);
        }

        private async Task WaitForTurnCompletedAsync(string sessionId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadEvents(sessionId).Any(item => item.Payload is TurnCompleted)) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("The setup conversation Turn did not complete.");
        }

        public async Task WaitForReceiptExecutionAsync(string commandId, string expectedState)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                if (ReadReceipt(commandId)?.ExecutionState == expectedState) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var receipt = ReadReceipt(commandId);
            throw new TimeoutException($"The setup receipt did not reach {expectedState}: "
                + $"state={receipt?.State}; outcome={receipt?.OutcomeKind}; execution={receipt?.ExecutionState}/{receipt?.ExecutionCode}.");
        }

        private void StartRuntimeAndHost(string? cutCommandId)
        {
            var runtime = OmniCliRuntime.Create(Workspace);
            runtime.UseConsoleInput = false;
            runtime.QuestionnaireInput = null;
            runtime.PrepareConversationSessionPolicy();
            var client = runtime.Connect(TestContext.Current.CancellationToken);
            _server = Assert.IsType<OmniServer>(client);
            var duplex = new RecoveryDuplex();
            _runtime = runtime;
            _duplex = duplex;
            Stream output = duplex.HostOutput;
            if (cutCommandId is not null)
            {
                CutStream = new AckDeliveryCutStream(output, cutCommandId);
                output = CutStream;
            }
            _hostTask = Task.Run(() => StdioProtocolHost.RunWithTrustedActions(client,
                duplex.HostInput, output, TextWriter.Null, _key,
                runPersistedConversation: async (admitted, token) =>
                    await runtime.ExecutePersistedConversationAsync(admitted,
                        diagnostic =>
                        {
                            DetailedDiagnostics.Enqueue(diagnostic);
                            Diagnostics.Enqueue(SafeDiagnostic(diagnostic));
                        }, token),
                nativeProcessControlDeferredForTests: NativeProcessControlDeferredForTests,
                nativeProcessControlStageForTests: NativeProcessControlStageForTests));
        }

        private void Negotiate(bool includeNativeChoiceCapability = true)
        {
            var capabilities = new List<string>
            {
                "query.commandCatalog", "query.commandRecovery", "query.processSessions",
                "command.process.control.user", "command.invoke.user", "command.invoke.continue.user",
                "command.run.mode.select.user",
            };
            if (includeNativeChoiceCapability)
                capabilities.Add("command.conversation.interaction.choice.respond.user");
            var hello = Exchange(WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":"
                + JsonSerializer.Serialize("native-command-hello-" + Guid.NewGuid().ToString("N"))
                + ",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":"
                + JsonSerializer.Serialize(capabilities) + "}}"));
            using var json = JsonDocument.Parse(hello.PayloadJson);
            if (!json.RootElement.TryGetProperty("trustedActionNonce", out var nonce))
            {
                var errorCode = json.RootElement.TryGetProperty("errorCode", out var code) ? code.GetString() : null;
                var keys = string.Join(",", json.RootElement.EnumerateObject().Select(property => property.Name));
                throw new InvalidOperationException($"Trusted action nonce was not negotiated (messageType={hello.MessageType}, errorCode={errorCode ?? "none"}, payloadKeys={keys}).");
            }
            _nonce = nonce.GetString()
                ?? throw new InvalidOperationException("Trusted action nonce was null.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _duplex?.CompleteClientInput();
            if (_hostTask is not null)
            {
                try { await _hostTask.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); }
                catch (Exception) { }
            }
            CutStream?.Dispose();
            _duplex?.Dispose();
            _runtime?.Dispose();
            await Provider.DisposeAsync();
            _providerBodyArrived.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", _oldConfig);
            Environment.CurrentDirectory = _oldDirectory;
            if (!PreserveRootOnDispose)
                try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
