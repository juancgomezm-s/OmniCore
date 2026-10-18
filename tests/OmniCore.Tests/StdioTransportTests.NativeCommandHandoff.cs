using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
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

    private static long? NullableJsonInt64(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();

    private sealed class NativeCommandHandoffFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string? _oldData;
        private readonly string? _oldConfig;
        private readonly string _oldDirectory;
        private readonly byte[] _key;
        private OmniCliRuntime? _runtime;
        private RecoveryDuplex? _duplex;
        private Task<int>? _hostTask;
        private string? _nonce;
        private bool _disposed;

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
            CutStream = null!;
        }

        public string Workspace { get; }
        public CliEndToEndTests.ScriptedHttpProvider Provider { get; }
        public ConcurrentQueue<string> ProviderBodies { get; }
        public AckDeliveryCutStream CutStream { get; private set; }
        public string JournalPath => Path.Combine(OmniHost.WorkspaceDataDirectory(
            OmniHost.CreatePlatformPaths(), Workspace), "journal.db");

        public static async Task<NativeCommandHandoffFixture> StartAsync(string cutCommandId)
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
            provider.RespondWith((_, body) =>
            {
                fixture.ProviderBodies.Enqueue(body);
                return CliEndToEndTests.ResponsesTextResponse("The local native-command fixture completed this Turn.");
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
                File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  scripted-model:\n    provider: scripted\n    context: 32768\n    recommendedUsableContext: 24000\n    maxOutput: 512\n    parametersBillions: 100\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "permissions.yaml"), "profile: balanced\nrules:\n  tool.search: allow\n", new UTF8Encoding(false));
                Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
                Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
                Environment.CurrentDirectory = workspace;
                using (var policies = OmniHost.CreateModelPolicyService(data))
                    policies.Set(ModelPolicyKey.For("scripted", "scripted-model"), 0,
                        ModelPolicyPresets.FullAgent(), CancellationToken.None);
                var defaults = new SessionSecurityDefaultConfiguration(OmniHost.CreatePlatformPaths());
                defaults.Set(defaults.Read().Revision, ModelPolicyCategory.FullAgent, CancellationToken.None);
                fixture.StartRuntimeAndHost(cutCommandId);
                fixture.Negotiate();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<string> CreateSessionAndQuiescentRunAsync()
        {
            var initialScope = QueryJson("humanActionScope");
            var create = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}");
            var createAck = Ack(ExchangeTrusted(create, NullableJsonString(initialScope.GetProperty("sessionId")),
                NullableJsonString(initialScope.GetProperty("runId"))));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, createAck.Outcome?.Kind);
            var selection = QueryJson("session.model.selection");
            var session = selection.GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException("The new Session ID was not returned.");
            var start = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "conversation.start",
                text = "A short setup turn before the native command.",
                mode = "plan",
                expectedRevision = (long?)null,
                expectedPolicyRevision = selection.GetProperty("sessionPolicyRevision").GetInt64(),
                expectedDefaultRevision = (string?)null,
            }));
            var startAck = Ack(ExchangeTrusted(start, session, null));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, startAck.Outcome?.Kind);
            await Provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await WaitForTurnCompletedAsync(session);
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

        public async Task WaitForInvocationAndTurnAsync(string sessionId, string runId, string invocationId)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var events = ReadEvents(sessionId);
                var started = events.Any(item => item.Payload is NativeCommandInvocationStarted command
                    && command.InvocationId.ToString() == invocationId);
                var finished = events.Any(item => item.Payload is NativeCommandInvocationFinished command
                    && command.InvocationId.ToString() == invocationId && command.OutcomeCode == "completed");
                var expectedRun = RunId.Parse(runId);
                var turns = events.Where(item => item.Envelope.RunId == expectedRun && item.Payload is TurnStarted)
                    .Select(item => item.Envelope.TurnId).Where(id => id is not null).ToHashSet();
                var completedTurns = events.Where(item => item.Envelope.RunId == expectedRun
                        && item.Payload is TurnCompleted)
                    .Select(item => item.Envelope.TurnId).Where(id => id is not null).ToHashSet();
                if (started && finished && turns.Intersect(completedTurns).Count() >= 2) return;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("The native command did not complete its durable Run Turn.");
        }

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
            Negotiate();
            return Task.CompletedTask;
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

        private void StartRuntimeAndHost(string? cutCommandId)
        {
            var runtime = OmniCliRuntime.Create(Workspace);
            runtime.UseConsoleInput = false;
            runtime.QuestionnaireInput = null;
            runtime.PrepareConversationSessionPolicy();
            var client = runtime.Connect(TestContext.Current.CancellationToken);
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
                    await runtime.ExecutePersistedConversationAsync(admitted, _ => { }, token)));
        }

        private void Negotiate()
        {
            var hello = Exchange(WireEnvelope.FromJson("{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":"
                + JsonSerializer.Serialize("native-command-hello-" + Guid.NewGuid().ToString("N"))
                + ",\"payload\":{\"supportedVersions\":[\"1.0\"],\"capabilities\":[\"query.commandCatalog\",\"query.commandRecovery\",\"command.invoke.user\",\"command.invoke.continue.user\"]}}"));
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
            CryptographicOperations.ZeroMemory(_key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", _oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", _oldConfig);
            Environment.CurrentDirectory = _oldDirectory;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
