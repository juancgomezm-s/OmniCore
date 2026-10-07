using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>Mode-authority revocation across a real SQLite/CAS questionnaire suspension and resume.</summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class ModeAuthoritySuspensionIntegrationTests
{
    private const string ProviderId = "mode-authority-resume-loopback";
    private const string ModelId = "mode-authority-resume-model";
    private static readonly QuestionnaireSchema Schema = new("Choose", null,
        new QuestionField[] { new("approach", "Which?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });

    private static string Payload(params string[] fields) => "{" + string.Join(",", fields) + "}";
    private static WireEnvelope Command(string payload) => WireEnvelope.Command(Ids.NewV7(), payload);

    [Fact]
    public void User_revocation_survives_questionnaire_suspend_reopen_and_same_turn_resume()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mode-authority-suspension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var stateFile = Path.Combine(root, "last-session.txt");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root, new SecretRedactor());
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            var started = server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
                JsonObj.Field("text", "resume the same bounded turn after a user question"))),
                TestContext.Current.CancellationToken);
            Assert.Equal("ok", started.Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var lane = Assert.IsType<LaneId>(server.LastLaneId());

            var selected = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
                JsonObj.Field("mode", "act"), JsonObj.Field("effort", "ultracode"),
                JsonObj.FieldBool("adaptive", true), JsonObj.Field("allowedModes", "plan,act,orq"),
                JsonObj.FieldRaw("maxAgents", "1"), JsonObj.FieldRaw("maxDepth", "1"),
                JsonObj.FieldRaw("maxTurns", "3"), JsonObj.FieldRaw("maxToolCalls", "8"),
                JsonObj.FieldRaw("maxElapsedSeconds", "90"), JsonObj.FieldRaw("maxSpendUsd", "2.50"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selected.Outcome?.Kind);
            var granted = Assert.IsType<RunModeAuthority>(server.CurrentModeAuthority());
            Assert.Equal(ProductEffort.UltraCode, granted.ProductEffort);
            Assert.True(granted.AutoModeSwitch);
            Assert.False(granted.ModePinned);
            var reasoning = new ReasoningRequest("high", null);
            var resolution = new ReasoningResolution(reasoning, reasoning, ReasoningSelectionSource.UltraCode,
                modeAuthorityRevision: granted.Revision, outputReserveTokens: 1024);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 4096, ToolMode.Direct,
                reasoning, route.Id, route, maxOutputTokens: 1024, reasoningResolution: resolution);

            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var callId = ToolCallId.New();
            var calls = 0;
            var requests = new List<ModelRequest>();
            var materializer = new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>());
            var questionnaireService = new QuestionnaireInteractionService(store!, codecs, artifacts);

            ExplorerTurn MakeTurn() => new((request, _) =>
            {
                requests.Add(request);
                calls++;
                return calls == 1
                    ? new ModelResponse(new ContentBlock[] { new ToolCallBlock(callId,
                        "provider-question", "user.ask", QuestionnaireCodec.EncodeSchema(Schema)) },
                        StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0), null,
                        new ProviderMetadata("scripted", string.Empty, null))
                    : new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                        new TokenUsage(4, 1, 0, 0, 0), null, new ProviderMetadata("scripted", string.Empty, null));
            }, executor, catalog, materializer,
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"), selection,
                store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questionnaireService, maximumGenerationRequestAttempts: 1);

            var suspended = MakeTurn().Ask("ask", "system", session, run, lane, string.Empty,
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.Equal(1, calls);
            var interactionId = Assert.IsType<InteractionId>(suspended.PendingInteractionId);
            var firstEvents = store.ReadFrom(session, 1);
            var turnStartedEvent = Assert.Single(firstEvents, evt => codecs.Decode(evt) is TurnStarted);
            var turnStarted = Assert.IsType<TurnStarted>(codecs.Decode(turnStartedEvent));
            var interactionRequestEvent = Assert.Single(firstEvents, evt =>
                codecs.Decode(evt) is InteractionRequested requested && requested.Kind == InteractionKind.Question);
            var interactionRequest = Assert.IsType<InteractionRequested>(codecs.Decode(interactionRequestEvent));
            Assert.Equal(interactionId, interactionRequest.InteractionId);
            Assert.Equal(session, interactionRequestEvent.SessionId);
            Assert.Equal(run, interactionRequestEvent.RunId);
            Assert.Equal(turnStarted.TurnId, interactionRequestEvent.TurnId);
            Assert.Equal(lane, interactionRequestEvent.LaneId);
            Assert.Equal(run, turnStartedEvent.RunId);
            Assert.Equal(lane, turnStarted.LaneId);
            Assert.Single(firstEvents.Select(codecs.Decode).OfType<ModelStepStarted>());
            var firstStep = Assert.Single(firstEvents.Select(codecs.Decode).OfType<ModelStepStarted>());
            Assert.Equal(turnStarted.TurnId, firstStep.TurnId);
            Assert.Equal(4096, firstStep.ContextBudget);
            Assert.True(resolution.IsEquivalentTo(firstStep.ReasoningResolution));
            Assert.Equal(10, Assert.Single(firstEvents.Select(codecs.Decode).OfType<ModelStepCompleted>()).Usage.Input);

            var revoked = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.revoke"))),
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, revoked.Outcome?.Kind);
            var afterRevoke = RunProjection.Replay(session, run, codecs, store.ReadFrom(session, 1));
            var revokedAuthority = Assert.IsType<RunModeAuthority>(afterRevoke.ModeAuthority);
            Assert.Equal(ProductEffort.Standard, revokedAuthority.ProductEffort);
            Assert.True(revokedAuthority.ModePinned);
            Assert.False(revokedAuthority.AutoModeSwitch);
            Assert.Null(revokedAuthority.Authorization);

            var closedConnection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(closedConnection);
            closedConnection.Dispose();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root, new SecretRedactor());
            questionnaireService = new QuestionnaireInteractionService(store, codecs, artifacts);
            File.WriteAllText(stateFile, session + "\n" + run);
            server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);

            var pending = questionnaireService.Pending(session);
            Assert.Collection(pending, item => Assert.Equal(interactionRequest.InteractionId, item.InteractionId));
            Assert.Equal("ok", server.RespondToQuestionnaire(interactionRequest.InteractionId,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);
            Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is InteractionResolved item
                && item.InteractionId == interactionRequest.InteractionId);

            var resumed = MakeTurn().Ask("resume", "system", session, run, lane, string.Empty,
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.Equal("done", resumed.FinalText);
            Assert.Equal(2, calls);
            Assert.Equal(2, requests.Count);
            Assert.All(requests, request =>
            {
                Assert.Equal(reasoning, request.Reasoning);
                Assert.Equal(reasoning, request.Model.Reasoning);
                Assert.Equal(4096, request.Model.ContextBudget);
                Assert.Equal(1024, request.Model.MaxOutputTokens);
                Assert.True(resolution.IsEquivalentTo(request.Model.ReasoningResolution));
            });

            var finalEvents = store.ReadFrom(session, 1);
            Assert.Single(finalEvents, evt => codecs.Decode(evt) is TurnStarted);
            var stepStarts = finalEvents.Select(codecs.Decode).OfType<ModelStepStarted>().ToArray();
            Assert.Equal(2, stepStarts.Length);
            Assert.Equal(new[] { 0, 1 }, stepStarts.Select(item => item.StepIndex));
            Assert.All(stepStarts, item =>
            {
                Assert.Equal(turnStarted.TurnId, item.TurnId);
                Assert.Equal(4096, item.ContextBudget);
                Assert.True(resolution.IsEquivalentTo(item.ReasoningResolution));
            });
            Assert.Equal(2, finalEvents.Select(codecs.Decode).OfType<ModelStepCompleted>().Count());
            Assert.Single(finalEvents, evt => codecs.Decode(evt) is InteractionRequested item
                && item.Kind == InteractionKind.Question);
            Assert.Single(finalEvents, evt => codecs.Decode(evt) is InteractionResolved item
                && item.InteractionId == interactionRequest.InteractionId);
            Assert.DoesNotContain(finalEvents, evt => codecs.Decode(evt) is RunModeTransitionAuthorized
                { Origin: "UltraCodePolicy" });

            var replayed = RunProjection.Replay(session, run, codecs, finalEvents);
            Assert.Equal(RunState.Running, replayed.State);
            AssertAuthorityEqual(revokedAuthority, replayed.ModeAuthority);
            var beforeDeniedTransition = store.CurrentSequence(session);
            var deniedTransition = server.ApplyUltraCodePolicyTransition(session, run, RunMode.Plan,
                "questionnaire response is not adaptive authority", revokedAuthority.Revision,
                TestContext.Current.CancellationToken);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, deniedTransition.Outcome?.Kind);
            Assert.Equal(beforeDeniedTransition, store.CurrentSequence(session));
        }
        finally
        {
            if (store is not null)
            {
                var connection = (SqliteConnection)store.Connection;
                store.Close();
                SqliteConnection.ClearPool(connection);
                connection.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Normal_tui_resume_uses_open_turn_snapshot_after_revocation_but_new_turn_does_not()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-mode-authority-cli-resume-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var endpoint = new ScriptedQuestionResponsesEndpoint();
        var previousData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var previousConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var previousBase = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        var previousModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        SqliteEventStore? journal = null;
        Task<int>? firstTurn = null;
        Task<int>? resumedTurn = null;
        Task<int>? competingResume = null;
        Task<int>? newTurn = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            Directory.CreateDirectory(workspace);
            var data = Path.Combine(root, "data");
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(data, "config"));
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", endpoint.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_MODEL", ModelId);
            WriteConfiguration(data, endpoint.BaseUrl);

            var runtime = OmniCliRuntime.Create(workspace);
            var server = Assert.IsType<OmniServer>(runtime.Connect(stop.Token));
            journal = Assert.IsType<SqliteEventStore>(server.AcquireStore());
            server.ConfigureNewSessionRoutingPolicy(ModelRoutingHost.InitialSessionPolicy(
                OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths(data)), ProviderId));
            Assert.Equal("ok", server.Send(Command(Payload(JsonObj.Field("cmd", "session.input"),
                JsonObj.Field("text", "start one bounded turn"))), stop.Token).Status);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            var lane = Assert.IsType<LaneId>(server.LastLaneId());
            var grant = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.select"),
                JsonObj.Field("mode", "act"), JsonObj.Field("effort", "ultracode"),
                JsonObj.FieldBool("adaptive", true), JsonObj.Field("allowedModes", "plan,act,orq"),
                JsonObj.FieldRaw("maxAgents", "1"), JsonObj.FieldRaw("maxDepth", "1"),
                JsonObj.FieldRaw("maxTurns", "3"), JsonObj.FieldRaw("maxToolCalls", "8"),
                JsonObj.FieldRaw("maxElapsedSeconds", "90"), JsonObj.FieldRaw("maxSpendUsd", "2.50"))),
                stop.Token);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, grant.Outcome?.Kind);

            var tui = new TuiTurnHost(runtime);
            var firstDiagnostics = new ConcurrentQueue<string>();
            firstTurn = Task.Run(async () => await tui.ExecuteAsync("ask one question", firstDiagnostics.Enqueue,
                stop.Token));
            var firstExit = await firstTurn.WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            Assert.Equal(3, firstExit);
            Assert.True(endpoint.RequestCount == 1, "Question must come from the actual provider request, not another pending gate. Diagnostics: "
                + string.Join(" | ", firstDiagnostics));
            Assert.Equal(1, endpoint.RequestCount);
            Assert.Equal("high", endpoint.Requests[0].ReasoningEffort);

            var firstEvents = server.AcquireStore().ReadFrom(session, 1);
            var turnStartEvent = Assert.Single(firstEvents, evt => server.AcquireCodecs().Decode(evt) is TurnStarted);
            var turnStarted = Assert.IsType<TurnStarted>(server.AcquireCodecs().Decode(turnStartEvent));
            var originalResolution = Assert.IsType<ReasoningResolution>(turnStarted.ReasoningResolution);
            Assert.Equal(ReasoningSelectionSource.UltraCode, originalResolution.Source);
            Assert.Equal(new ReasoningRequest("high", null), originalResolution.AppliedRequest);
            Assert.Equal(lane, turnStarted.LaneId);
            var questionEvent = Assert.Single(firstEvents, evt => server.AcquireCodecs().Decode(evt) is InteractionRequested item
                && item.Kind == InteractionKind.Question);
            var question = Assert.IsType<InteractionRequested>(server.AcquireCodecs().Decode(questionEvent));
            Assert.Equal(session, questionEvent.SessionId);
            Assert.Equal(run, questionEvent.RunId);
            Assert.Equal(turnStarted.TurnId, questionEvent.TurnId);
            Assert.Equal(lane, questionEvent.LaneId);
            var originalStep = Assert.Single(firstEvents.Select(server.AcquireCodecs().Decode)
                .OfType<ModelStepStarted>(), step => step.TurnId == turnStarted.TurnId);
            Assert.True(originalResolution.IsEquivalentTo(originalStep.ReasoningResolution));

            var revoke = server.SendUserAction(Command(Payload(JsonObj.Field("cmd", "run.mode.revoke"))), stop.Token);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, revoke.Outcome?.Kind);
            var afterRevoke = RunProjection.Replay(session, run, server.AcquireCodecs(),
                server.AcquireStore().ReadFrom(session, 1));
            var revokedAuthority = Assert.IsType<RunModeAuthority>(afterRevoke.ModeAuthority);
            Assert.Equal(ProductEffort.Standard, revokedAuthority.ProductEffort);
            Assert.True(revokedAuthority.ModePinned);
            Assert.False(revokedAuthority.AutoModeSwitch);
            Assert.Null(revokedAuthority.Authorization);

            CloseJournal(journal);
            journal = null;
            var reopenedRuntime = OmniCliRuntime.Create(workspace);
            var reopened = Assert.IsType<OmniServer>(reopenedRuntime.Connect(stop.Token));
            journal = Assert.IsType<SqliteEventStore>(reopened.AcquireStore());
            Assert.Equal(session, reopened.LastSessionId());
            Assert.Equal(run, reopened.LastRunId());
            Assert.Equal("ok", reopened.RespondToQuestionnaire(question.InteractionId,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            var reopenedTui = new TuiTurnHost(reopenedRuntime);
            endpoint.HoldRequest(2);
            var resumeDiagnostics = new ConcurrentQueue<string>();
            resumedTurn = Task.Run(async () => await reopenedTui.ExecuteAsync("", resumeDiagnostics.Enqueue, stop.Token));
            await endpoint.WaitForHeldRequestAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            competingResume = Task.Run(async () => await reopenedTui.ExecuteAsync("", _ => { }, stop.Token));
            var competingExit = await competingResume.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
            Assert.Equal(1, competingExit);
            Assert.Equal(2, endpoint.RequestCount);
            endpoint.ReleaseHeldRequest();
            var resumedExit = await resumedTurn.WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            Assert.True(resumedExit == 0, "The exact suspended Turn should resume from its durable reasoning selection. Diagnostics: "
                + string.Join(" | ", resumeDiagnostics));
            Assert.Equal(2, endpoint.RequestCount);
            Assert.Equal("high", endpoint.Requests[1].ReasoningEffort);
            Assert.All(endpoint.Requests, request => Assert.Equal(2048, request.MaxOutputTokens));

            var resumedEvents = reopened.AcquireStore().ReadFrom(session, 1);
            Assert.Single(resumedEvents, evt => reopened.AcquireCodecs().Decode(evt) is TurnStarted
                && evt.RunId == run && evt.TurnId == turnStarted.TurnId);
            var steps = resumedEvents.Select(reopened.AcquireCodecs().Decode).OfType<ModelStepStarted>()
                .Where(step => step.TurnId == turnStarted.TurnId).ToArray();
            Assert.Equal(new[] { 0, 1 }, steps.Select(step => step.StepIndex));
            Assert.All(steps, step =>
            {
                Assert.Equal(4096, step.ContextBudget);
                Assert.True(originalResolution.IsEquivalentTo(step.ReasoningResolution));
            });
            var authorityAfterResume = RunProjection.Replay(session, run, reopened.AcquireCodecs(), resumedEvents).ModeAuthority;
            AssertAuthorityEqual(revokedAuthority, authorityAfterResume);

            newTurn = Task.Run(async () => await reopenedTui.ExecuteAsync("new turn after revocation", _ => { }, stop.Token));
            var newTurnExit = await newTurn.WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            Assert.Equal(0, newTurnExit);
            Assert.Equal(3, endpoint.RequestCount);
            Assert.Null(endpoint.Requests[2].ReasoningEffort);
            Assert.All(endpoint.Requests, request => Assert.Equal(2048, request.MaxOutputTokens));
            var allEvents = reopened.AcquireStore().ReadFrom(session, 1).Select(reopened.AcquireCodecs().Decode).ToArray();
            var starts = allEvents.OfType<TurnStarted>().ToArray();
            Assert.Equal(2, starts.Length);
            Assert.Equal(turnStarted.TurnId, starts[0].TurnId);
            Assert.Equal(ReasoningSelectionSource.UltraCode, starts[0].ReasoningResolution?.Source);
            Assert.NotEqual(starts[0].TurnId, starts[1].TurnId);
            Assert.Equal(ReasoningSelectionSource.None, starts[1].ReasoningResolution?.Source);
            Assert.Equal(2, allEvents.OfType<ModelStepStarted>().Count(step => step.TurnId == turnStarted.TurnId));
            Assert.Single(allEvents.OfType<ModelStepStarted>(), step => step.TurnId == starts[1].TurnId);
        }
        finally
        {
            stop.Cancel();
            endpoint.ReleaseHeldRequest();
            await endpoint.DisposeAsync();
            await DrainAsync(firstTurn);
            await DrainAsync(resumedTurn);
            await DrainAsync(competingResume);
            await DrainAsync(newTurn);
            CloseJournal(journal);
            var paths = OmniHost.CreatePlatformPaths(Path.Combine(root, "data"));
            ClearExactPool(paths.UserDatabasePath);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", previousConfig);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBase);
            Environment.SetEnvironmentVariable("OMNI_MODEL", previousModel);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteConfiguration(string dataDirectory, string endpoint)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAIResponses
                baseUrl: {{endpoint}}
                auth: none
                billingMode: Local
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 4096
                maxOutput: 2048
                reasoning:
                  supported: true
                  effortLevels: [high]
                  replayPolicy: PreserveAcrossSteps
            """);
    }

    private static void CloseJournal(SqliteEventStore? store)
    {
        if (store is null) return;
        var connection = (SqliteConnection)store.Connection;
        store.Close();
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
    }

    private static void ClearExactPool(string path)
    {
        if (!File.Exists(path)) return;
        using var connection = new SqliteConnection("DataSource=" + path);
        SqliteConnection.ClearPool(connection);
    }

    private static async Task DrainAsync(Task<int>? operation)
    {
        if (operation is null || operation.IsCompleted) return;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    private sealed record CapturedHttpRequest(string Body, string? ReasoningEffort, int? MaxOutputTokens);

    private sealed class ScriptedQuestionResponsesEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly ConcurrentQueue<CapturedHttpRequest> _requests = new();
        private readonly Task _serve;
        private TaskCompletionSource<bool>? _heldRequestReached;
        private TaskCompletionSource<bool>? _releaseHeldRequest;
        private int _heldRequestNumber;
        private int _requestCount;

        public ScriptedQuestionResponsesEndpoint()
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public IReadOnlyList<CapturedHttpRequest> Requests => _requests.ToArray();

        public void HoldRequest(int requestNumber)
        {
            if (requestNumber < 1) throw new ArgumentOutOfRangeException(nameof(requestNumber));
            _heldRequestReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseHeldRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _heldRequestNumber, requestNumber);
        }

        public Task WaitForHeldRequestAsync(CancellationToken cancellationToken) =>
            (_heldRequestReached ?? throw new InvalidOperationException("No request is configured to be held."))
                .Task.WaitAsync(cancellationToken);

        public void ReleaseHeldRequest() => _releaseHeldRequest?.TrySetResult(true);

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { break; }
                _handlers.Add(HandleAsync(client, _stop.Token));
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { Length: > 0 } header)
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                var body = new char[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await reader.ReadAsync(body.AsMemory(read), token).ConfigureAwait(false);
                    if (count == 0) break;
                    read += count;
                }
                var requestBody = new string(body, 0, read);
                using var document = JsonDocument.Parse(requestBody);
                var root = document.RootElement;
                var effort = root.TryGetProperty("reasoning", out var reasoning)
                    && reasoning.TryGetProperty("effort", out var effortElement) ? effortElement.GetString() : null;
                int? maxOutput = root.TryGetProperty("max_output_tokens", out var maxElement)
                    && maxElement.TryGetInt32(out var parsedMax) ? parsedMax : null;
                var number = Interlocked.Increment(ref _requestCount);
                _requests.Enqueue(new CapturedHttpRequest(requestBody, effort, maxOutput));
                if (number == Volatile.Read(ref _heldRequestNumber))
                {
                    var reached = _heldRequestReached
                        ?? throw new InvalidOperationException("Held request has no barrier.");
                    var release = _releaseHeldRequest
                        ?? throw new InvalidOperationException("Held request has no release signal.");
                    reached.TrySetResult(true);
                    await release.Task.WaitAsync(token).ConfigureAwait(false);
                }
                var responseBody = Encoding.UTF8.GetBytes(number == 1 ? QuestionStream() : TextStream("done"));
                var headers = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + responseBody.Length) + "\r\n\r\n");
                await stream.WriteAsync(headers, token).ConfigureAwait(false);
                await stream.WriteAsync(responseBody, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }

        private static string QuestionStream()
        {
            var arguments = QuestionnaireCodec.EncodeSchema(Schema);
            return "event: response.output_item.added\ndata: " + JsonSerializer.Serialize(new
                { type = "response.output_item.added", output_index = 0, item = new
                    { type = "function_call", call_id = "provider-question", name = "user.ask", arguments = "" } }) + "\n\n"
                + "event: response.function_call_arguments.delta\ndata: " + JsonSerializer.Serialize(new
                    { type = "response.function_call_arguments.delta", output_index = 0, delta = arguments }) + "\n\n"
                + "event: response.output_item.done\ndata: " + JsonSerializer.Serialize(new
                    { type = "response.output_item.done", output_index = 0, item = new
                        { type = "function_call", call_id = "provider-question", name = "user.ask", arguments } }) + "\n\n"
                + "event: response.completed\ndata: " + JsonSerializer.Serialize(new
                    { type = "response.completed", response = new
                        { status = "completed", usage = new { input_tokens = 10, output_tokens = 2 } } }) + "\n\n";
        }

        private static string TextStream(string text)
        {
            return "event: response.output_item.added\ndata: " + JsonSerializer.Serialize(new
                { type = "response.output_item.added", output_index = 0, item = new { type = "message", role = "assistant" } }) + "\n\n"
                + "event: response.output_text.delta\ndata: " + JsonSerializer.Serialize(new
                    { type = "response.output_text.delta", output_index = 0, delta = text }) + "\n\n"
                + "event: response.output_item.done\ndata: " + JsonSerializer.Serialize(new
                    { type = "response.output_item.done", output_index = 0, item = new
                        { type = "message", content = new[] { new { type = "output_text", text } } } }) + "\n\n"
                + "event: response.completed\ndata: " + JsonSerializer.Serialize(new
                    { type = "response.completed", response = new
                        { status = "completed", usage = new { input_tokens = 4, output_tokens = 1 } } }) + "\n\n";
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseHeldRequest();
            _stop.Cancel();
            _listener.Stop();
            try { await _serve.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            try { await Task.WhenAll(_handlers).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }

    private static void AssertAuthorityEqual(RunModeAuthority expected, RunModeAuthority? actual)
    {
        var value = Assert.IsType<RunModeAuthority>(actual);
        Assert.Equal(expected.RunId, value.RunId);
        Assert.Equal(expected.Revision, value.Revision);
        Assert.Equal(expected.Mode, value.Mode);
        Assert.Equal(expected.Strategy, value.Strategy);
        Assert.Equal(expected.ProductEffort, value.ProductEffort);
        Assert.Equal(expected.ModePinned, value.ModePinned);
        Assert.Equal(expected.AutoModeSwitch, value.AutoModeSwitch);
        Assert.Equal(expected.ObjectiveRevision, value.ObjectiveRevision);
        Assert.Equal(expected.ObjectiveDigest, value.ObjectiveDigest);
        Assert.Equal(expected.PolicyRevision, value.PolicyRevision);
        if (expected.Authorization is null)
        {
            Assert.Null(value.Authorization);
            return;
        }

        var authorization = Assert.IsType<ModeSwitchAuthorization>(value.Authorization);
        Assert.Equal(expected.Authorization.AuthorizationId, authorization.AuthorizationId);
        Assert.Equal(expected.Authorization.AuthorityRevision, authorization.AuthorityRevision);
        Assert.Equal(expected.Authorization.ObjectiveRevision, authorization.ObjectiveRevision);
        Assert.Equal(expected.Authorization.ObjectiveDigest, authorization.ObjectiveDigest);
        Assert.Equal(expected.Authorization.PolicyRevision, authorization.PolicyRevision);
        Assert.Equal(expected.Authorization.Limits, authorization.Limits);
        Assert.Equal(expected.Authorization.GrantedAtUtc, authorization.GrantedAtUtc);
        Assert.True(expected.Authorization.AllowedModes.SequenceEqual(authorization.AllowedModes));
    }
}
