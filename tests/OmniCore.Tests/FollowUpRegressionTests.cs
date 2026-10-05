using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class FollowUpRegressionTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose", null, new QuestionField[] {
        new("choice", "Choice?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("yes", "Yes", null) }, null, true, null, null, null) });

    [Fact]
    public void Followups_survive_reopen_and_promote_one_per_turn_in_fifo_order_with_run_isolation()
    {
        var root = NewRoot();
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var initialCalls = 0;
            var suspended = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane,
                catalog, executor, questionnaires, (_, _) =>
                {
                    initialCalls++;
                    return AskQuestion();
                }).Ask("ask original", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.Equal(1, initialCalls);
            var originalTurn = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<TurnStarted>()).TurnId;

            var control = new RunControlService(store, codecs);
            const string secret = "sk-proj-1234567890";
            Assert.Equal(run.RunId, control.SendInput(session, "followup-A " + secret, RunMode.Plan, "Composer-A"));
            Assert.Equal(run.RunId, control.SendInput(session, "followup-B", RunMode.Plan, "Composer-B"));
            var queuedAB = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<FollowUpQueued>().ToArray();
            Assert.Equal(new[] { "Composer-A", "Composer-B" }, queuedAB.Select(item => item.Origin));
            Assert.DoesNotContain(secret, string.Join("\n", store.ReadFrom(session, 1).Select(evt => evt.PayloadJson)),
                StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", string.Join("\n", store.ReadFrom(session, 1).Select(evt => evt.PayloadJson)),
                StringComparison.Ordinal);
            Assert.DoesNotContain(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<UserInputReceived>(),
                item => item.InputPartsJson.Contains("followup-", StringComparison.Ordinal));
            Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(session, run.RunId, codecs,
                store.ReadFrom(session, 1)).State);

            // A different Run/Lane in the same session has an independent FollowUp mailbox.
            var otherRun = TestRun.Open(store, session, mode: RunMode.Plan);
            var otherTurn = TurnId.New();
            var otherStream = new EventStream(store, codecs, session);
            otherStream.Append(new TurnStarted(otherTurn, otherRun.RootLane));
            Assert.True(FollowUpQueue.TryQueue(store, codecs, session, otherRun.RunId,
                otherRun.RootLane, "other-run-only", "Other"));
            Assert.Equal(2, FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane).Count);
            Assert.Contains("other-run-only", Assert.Single(FollowUpQueue.Pending(store, codecs, session,
                otherRun.RunId, otherRun.RootLane)).InputPartsJson, StringComparison.Ordinal);

            var secondTask = TaskId.New();
            var secondLane = LaneId.New();
            var laneStream = new EventStream(store, codecs, session);
            laneStream.AppendBatch(new DomainEventPayload[] {
                new TaskCreated(secondTask, run.RunId, "other lane", Array.Empty<TaskDependency>(),
                    new TaskBudget(null, null, null, null)),
                new TaskReady(secondTask),
                new LaneCreated(secondLane, secondTask, ProfileId.New()),
                new LaneStarted(secondLane),
                new TaskStarted(secondTask, secondLane),
            }, DurabilityClass.Standard);
            laneStream.Append(new TurnStarted(TurnId.New(), secondLane));
            Assert.True(FollowUpQueue.TryQueue(store, codecs, session, run.RunId, secondLane,
                "other-lane-only", "Other-Lane"));
            Assert.Equal(2, FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane).Count);
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, run.RunId, secondLane));

            var interaction = Assert.Single(questionnaires.Pending(session)).InteractionId;
            var persistedQueuePayloads = store.ReadFrom(session, 1).Where(evt => evt.Type.ToString() == "followup.queued")
                .Select(evt => evt.PayloadJson).ToArray();
            store.Close();
            store = new SqliteEventStore(journal);
            Assert.Equal(persistedQueuePayloads, store.ReadFrom(session, 1)
                .Where(evt => evt.Type.ToString() == "followup.queued").Select(evt => evt.PayloadJson));
            artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stream = new EventStream(store, codecs, session);
            var accepted = questionnaires.Resolve(stream, interaction, new[] { new QuestionAnswer("choice",
                new[] { "yes" }, null, null) }, false, null,
                new UserInputReceived(run.RunId, "[]", null, "InteractionResponse(Questionnaire)"));
            Assert.True(accepted.Accepted);

            // Resolve and complete the original Turn without passing a new prompt. The queued
            // inputs do not steer it and are not sent to its provider invocation.
            ModelRequest? originalRequest = null;
            var resumed = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog,
                executor, questionnaires, (request, _) =>
                {
                    originalRequest = request;
                    return FinalResponse();
                }).Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.NotNull(originalRequest);
            Assert.DoesNotContain(originalRequest!.Messages.Where(message => message.Role == MessageRole.User)
                .SelectMany(message => message.Content).OfType<TextBlock>(),
                block => block.Text.Contains("followup-", StringComparison.Ordinal));
            var afterOriginal = store.ReadFrom(session, 1).Where(evt => evt.RunId == run.RunId)
                .Select(codecs.Decode).ToArray();
            Assert.Single(afterOriginal.OfType<TurnStarted>(), item => item.LaneId == run.RootLane);
            Assert.Equal(new[] { "Composer-A", "Composer-B" },
                afterOriginal.OfType<FollowUpQueued>().Where(item => item.RunId == run.RunId
                    && item.LaneId == run.RootLane).Select(item => item.Origin));

            var expected = new[] { "followup-A", "followup-B", "followup-C" };
            var newTurnIds = new List<TurnId>();
            for (var index = 0; index < expected.Length; index++)
            {
                ModelRequest? request = null;
                var freshInput = index == 0 ? expected[^1] : "";
                var freshOrigin = index == 0 ? "Composer-C" : null;
                var result = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog,
                    executor, questionnaires, (modelRequest, _) =>
                    {
                        request = modelRequest;
                        return FinalResponse();
                    }).Ask(freshInput, "system", session, run.RunId, run.RootLane, "", CancellationToken.None,
                        freshOrigin);
                Assert.Equal(StopReason.EndTurn, result.StopReason);
                Assert.NotNull(request);
                var userText = request!.Messages.Where(message => message.Role == MessageRole.User)
                    .SelectMany(message => message.Content).OfType<TextBlock>().Select(block => block.Text).ToArray();
                for (var markerIndex = 0; markerIndex < expected.Length; markerIndex++)
                    Assert.Equal(markerIndex <= index ? 1 : 0,
                        userText.Count(text => text.Contains(expected[markerIndex], StringComparison.Ordinal)));
                newTurnIds.Add(store.ReadFrom(session, 1).Where(evt => evt.RunId == run.RunId
                        && evt.LaneId == run.RootLane)
                    .Select(codecs.Decode).OfType<TurnStarted>()
                    .Last().TurnId);
                if (index == 0)
                {
                    store.Close();
                    store = new SqliteEventStore(journal);
                    artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
                    questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
                }
            }

            var finalEvents = store.ReadFrom(session, 1).Where(evt => evt.RunId == run.RunId
                    && (evt.LaneId == run.RootLane || evt.LaneId is null))
                .Select(codecs.Decode).ToArray();
            var promotions = finalEvents.OfType<FollowUpPromoted>().Where(item => item.RunId == run.RunId
                && item.LaneId == run.RootLane).ToArray();
            Assert.Equal(3, promotions.Length);
            Assert.Equal(queuedAB.Select(item => item.FollowUpId).Append(
                finalEvents.OfType<FollowUpQueued>().Single(item => item.Origin == "Composer-C").FollowUpId),
                promotions.Select(item => item.FollowUpId));
            Assert.Equal(newTurnIds, promotions.Select(item => item.TurnId));
            Assert.Empty(FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane));
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, otherRun.RunId, otherRun.RootLane));
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, run.RunId, secondLane));
            Assert.Equal(1, finalEvents.OfType<UserInputReceived>().Count(item =>
                item.InputPartsJson.Contains("followup-A", StringComparison.Ordinal)));
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Failed_promotion_batch_does_not_call_provider_or_consume_followup()
    {
        var root = NewRoot();
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var first = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog, executor,
                questionnaires, (_, _) => AskQuestion()).Ask("ask", "system", session, run.RunId, run.RootLane,
                    "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, first.StopReason);
            new RunControlService(store, codecs).SendInput(session, "kept-followup", RunMode.Plan, "Composer");
            var interaction = Assert.Single(questionnaires.Pending(session)).InteractionId;
            questionnaires.Resolve(new EventStream(store, codecs, session), interaction,
                new[] { new QuestionAnswer("choice", new[] { "yes" }, null, null) }, false, null,
                new UserInputReceived(run.RunId, "[]", null, "InteractionResponse(Questionnaire)"));
            Assert.Equal(StopReason.EndTurn, MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane,
                catalog, executor, questionnaires, (_, _) => FinalResponse()).Ask("", "system", session,
                    run.RunId, run.RootLane, "", CancellationToken.None).StopReason);

            var faultStore = new FailPromotionStore(store);
            var calls = 0;
            var failingTurn = MakeTurn(faultStore, codecs, artifacts, session, run.RunId, run.RootLane, catalog,
                executor, questionnaires, (_, _) =>
                {
                    calls++;
                    return FinalResponse();
                });
            var failed = failingTurn.Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.Error, failed.StopReason);
            Assert.Equal(0, calls);
            var persisted = store.ReadFrom(session, 1).Where(evt => evt.RunId == run.RunId)
                .Select(codecs.Decode).ToArray();
            Assert.Single(persisted.OfType<FollowUpQueued>());
            Assert.Empty(persisted.OfType<FollowUpPromoted>());
            Assert.Single(persisted.OfType<TurnStarted>());
            Assert.DoesNotContain(persisted.OfType<UserInputReceived>(), item =>
                item.InputPartsJson.Contains("kept-followup", StringComparison.Ordinal));
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane));
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Followup_for_cancelled_run_remains_inert_and_never_invokes_provider()
    {
        var root = NewRoot();
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            new EventStream(store, codecs, session).Append(new TurnStarted(TurnId.New(), run.RootLane));
            new RunControlService(store, codecs).SendInput(session, "inert-after-cancel", RunMode.Plan);
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane));
            new RunControlService(store, codecs).CancelRun(session, run.RunId);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var result = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog,
                executor, new QuestionnaireInteractionService(store, codecs, artifacts), (_, _) =>
                {
                    calls++;
                    return FinalResponse();
                }).Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(0, calls);
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane));
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<FollowUpPromoted>());
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Pending_permission_interaction_prevents_followup_or_any_provider_step()
    {
        var root = NewRoot();
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var turnId = TurnId.New();
            var interactionId = InteractionId.New();
            var stream = new EventStream(store, codecs, session);
            stream.Append(new TurnStarted(turnId, run.RootLane));
            stream.AppendBatch(new DomainEventPayload[] {
                new InteractionRequested(interactionId, InteractionKind.Permission, "{}", "[]", "", null,
                    run.RootLane, run.RootTask, null, 0, 0),
                new RunAwaitingInput(run.RunId, run.RootLane),
            }, DurabilityClass.Standard);
            new RunControlService(store, codecs).SendInput(session, "permission-wait-followup", RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var result = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog,
                executor, new QuestionnaireInteractionService(store, codecs, artifacts), (_, _) =>
                {
                    calls++;
                    return FinalResponse();
                }).Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, result.StopReason);
            Assert.Equal(interactionId, result.PendingInteractionId);
            Assert.Equal(0, calls);
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane));
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<FollowUpPromoted>());
        }
        finally { Cleanup(store, journal, root); }
    }

    [Fact]
    public void Cli_prompt_is_durably_queued_before_pending_questionnaire_early_return()
    {
        var root = NewRoot();
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var first = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog, executor,
                questionnaires, (_, _) =>
                {
                    calls++;
                    return AskQuestion();
                }).Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, first.StopReason);

            // OmniCliRuntime calls this seam before its non-interactive/pending-question returns.
            Assert.True(OmniCliRuntime.QueuePromptForOpenTurn(store, codecs, session, run.RunId, run.RootLane,
                "cli-followup", "CliPrompt"));
            Assert.Equal(1, calls); // no second provider invocation is made by the early-return path.
            Assert.Single(questionnaires.Pending(session));
            Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(session, run.RunId, codecs,
                store.ReadFrom(session, 1)).State);
            var followUp = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<FollowUpQueued>());
            Assert.Equal("CliPrompt", followUp.Origin);
            Assert.DoesNotContain(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<UserInputReceived>(), item =>
                item.InputPartsJson.Contains("cli-followup", StringComparison.Ordinal));
        }
        finally { Cleanup(store, journal, root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pending_plan_approval_retains_prompt_without_open_turn_or_provider_call(bool hasPriorTurn)
    {
        var root = NewRoot();
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var stream = new EventStream(store, codecs, session);
            if (hasPriorTurn)
            {
                var previousTurn = TurnId.New();
                stream.Append(new TurnStarted(previousTurn, run.RootLane));
                stream.Append(new TurnCompleted(previousTurn));
            }
            var approval = InteractionId.New();
            stream.Append(new InteractionRequested(approval, InteractionKind.PlanApproval, "{}", "[]", "", null,
                run.RootLane, run.RootTask, null, 0, 0));

            Assert.True(OmniCliRuntime.QueuePromptForOpenTurn(store, codecs, session, run.RunId,
                run.RootLane, "approval-blocked-input", "Composer"));
            var queued = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<FollowUpQueued>());
            Assert.Null(queued.TurnId); // no open Turn: don't invent a source Turn identity.
            var calls = 0;
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var result = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog,
                executor, new QuestionnaireInteractionService(store, codecs, artifacts), (_, _) =>
                {
                    calls++;
                    return FinalResponse();
                }).Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, result.StopReason);
            Assert.Equal(approval, result.PendingInteractionId);
            Assert.Equal(0, calls);
            Assert.Single(FollowUpQueue.Pending(store, codecs, session, run.RunId, run.RootLane));
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<FollowUpPromoted>());

            var originalFollowUp = queued.FollowUpId;
            new EventStream(store, codecs, session).Append(new InteractionResolved(approval, "approve_execute",
                InteractionCause.User));
            ModelRequest? promotedRequest = null;
            var promoted = MakeTurn(store, codecs, artifacts, session, run.RunId, run.RootLane, catalog,
                executor, new QuestionnaireInteractionService(store, codecs, artifacts), (request, _) =>
                {
                    calls++;
                    promotedRequest = request;
                    return FinalResponse();
                }).Ask("fresh-after-approval", "system", session, run.RunId, run.RootLane, "",
                    CancellationToken.None, "Composer-Fresh");
            Assert.Equal(StopReason.EndTurn, promoted.StopReason);
            Assert.Equal(1, calls);
            var visibleUser = promotedRequest!.Messages.Where(message => message.Role == MessageRole.User)
                .SelectMany(message => message.Content).OfType<TextBlock>().Select(block => block.Text).ToArray();
            Assert.Contains(visibleUser, text => text.Contains("approval-blocked-input", StringComparison.Ordinal));
            Assert.DoesNotContain(visibleUser, text => text.Contains("fresh-after-approval", StringComparison.Ordinal));
            Assert.Equal(originalFollowUp, Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<FollowUpPromoted>()).FollowUpId);
            Assert.Contains("fresh-after-approval", Assert.Single(FollowUpQueue.Pending(store, codecs,
                session, run.RunId, run.RootLane)).InputPartsJson, StringComparison.Ordinal);
        }
        finally { Cleanup(store, journal, root); }
    }

    private static ExplorerTurn MakeTurn(IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        SessionId session, RunId run, LaneId lane, FakeCatalog catalog, IToolExecutor executor,
        QuestionnaireInteractionService questionnaires,
        Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(complete, executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
            store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), questionnaires: questionnaires);

    private static ModelResponse AskQuestion() => new(new ContentBlock[] {
        new ToolCallBlock(ToolCallId.New(), "ask", "user.ask", QuestionnaireCodec.EncodeSchema(Schema))
    }, StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));

    private static ModelResponse FinalResponse() => new(new ContentBlock[] { new TextBlock("done") },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-followup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(SqliteEventStore? store, string journal, string root)
    {
        store?.Close();
        if (File.Exists(journal))
        {
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
        }
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FailPromotionStore(IEventStore inner) : IEventStore
    {
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken token) =>
            inner.Append(session, evt, durability, token);
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken token)
        {
            if (events.Any(evt => evt.Type.ToString() == "followup.promoted"))
            {
                Assert.Equal(DurabilityClass.Barrier, durability);
                Assert.Contains(events, evt => evt.Type.ToString() == "user_input.received");
                Assert.Contains(events, evt => evt.Type.ToString() == "turn.started");
                throw new IOException("injected atomic promotion failure");
            }
            inner.AppendBatch(session, events, durability, token);
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long sequence) => inner.ReadFrom(session, sequence);
    }
}
