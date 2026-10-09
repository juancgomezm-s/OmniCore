using System.Text.Json;
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

public sealed class RunSummaryTests
{
    [Fact]
    public void Terminal_summary_is_durable_idempotent_scoped_and_does_not_rewrite_history()
    {
        using var fixture = new Fixture();
        fixture.Record("Usa PostgreSQL.", "Decidimos PostgreSQL.");
        fixture.Cancel();
        var before = fixture.Store.ReadFrom(fixture.Session, 1).ToArray();
        fixture.Service.EnsureRecorded(fixture.Session);
        var recorded = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).OfType<RunSummaryRecorded>());
        var envelope = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1), evt => fixture.Codecs.Decode(evt) is RunSummaryRecorded);
        Assert.Contains(recorded.SummaryArtifact, envelope.ArtifactRefs);
        Assert.Equal(before.Last().Sequence, recorded.ThroughEventSequence);
        Assert.Equal(before.Select(evt => evt.PayloadJson), fixture.Store.ReadFrom(fixture.Session, 1)
            .Take(before.Length).Select(evt => evt.PayloadJson));
        fixture.Reopen();
        var sequence = fixture.Store.CurrentSequence(fixture.Session);
        fixture.Service.EnsureRecorded(fixture.Session);
        Assert.Equal(sequence, fixture.Store.CurrentSequence(fixture.Session));
        var (summary, artifact) = Assert.Single(fixture.Service.Read(fixture.Session, RunId.New()));
        Assert.Equal(fixture.Run.RunId, summary.RunId);
        Assert.Equal("Cancelled", summary.Outcome);
        Assert.Equal("Decidimos PostgreSQL.", summary.FinalResponse);
        Assert.Contains("Usa PostgreSQL.", summary.UserMessages);
        Assert.True(fixture.Artifacts.Verify(artifact.Hash, artifact.Size));
        Assert.Empty(fixture.Service.Read(fixture.Session, fixture.Run.RunId));
        Assert.Empty(fixture.Service.Read(SessionId.New(), RunId.New()));
    }

    [Fact]
    public void Active_run_is_not_summarized_or_completed_by_the_summary_service()
    {
        using var fixture = new Fixture();
        fixture.Record("Pregunta.", "Respuesta.");
        var sequence = fixture.Store.CurrentSequence(fixture.Session);
        fixture.Service.EnsureRecorded(fixture.Session);
        Assert.Equal(sequence, fixture.Store.CurrentSequence(fixture.Session));
        Assert.Empty(fixture.Service.Read(fixture.Session, RunId.New()));
        Assert.Equal(RunState.Running, RunProjection.Replay(fixture.Session, fixture.Run.RunId, fixture.Codecs,
            fixture.Store.ReadFrom(fixture.Session, 1)).State);
    }

    [Fact]
    public void Summary_redacts_content_and_excludes_child_lane_transcripts()
    {
        using var fixture = new Fixture();
        fixture.Record("fixture-secret root input", "fixture-secret root answer");
        var stream = new EventStream(fixture.Store, fixture.Codecs, fixture.Session);
        var child = TaskId.New();
        var lane = LaneId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new TaskCreated(child, fixture.Run.RunId, "child objective", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null), fixture.Run.RootTask),
            new LaneCreated(lane, child, ProfileId.New()), new LaneStarted(lane),
        }, DurabilityClass.Standard);
        fixture.Record("child input must not leak", "child answer must not leak", lane, child);
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId, child, lane)))
        {
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(new ContextCheckpointRecorded(
                "child-checkpoint", fixture.Run.RunId, fixture.Store.CurrentSequence(fixture.Session),
                fixture.Artifacts.PutText("{\"summary\":\"child checkpoint must not leak\"}", "application/json",
                    ArtifactKind.ModelResponse, Sensitivity.Sensitive), "deterministic"));
        }
        fixture.Cancel();
        fixture.Service.EnsureRecorded(fixture.Session);
        var (summary, artifact) = Assert.Single(fixture.Service.Read(fixture.Session, RunId.New()));
        var text = fixture.Artifacts.GetText(artifact.Hash)!;
        Assert.DoesNotContain("fixture-secret", text);
        Assert.DoesNotContain("child input", text);
        Assert.DoesNotContain("child answer", text);
        Assert.DoesNotContain("child checkpoint", text);
        Assert.Equal("[redacted] root answer", summary.FinalResponse);
        Assert.Contains("[redacted] root input", summary.UserMessages);
        var historical = fixture.Turn((_, _) => throw new InvalidOperationException("No provider work during replay"))
            .LoadConversation(new EventStream(fixture.Store, fixture.Codecs, fixture.Session), RunId.New());
        Assert.DoesNotContain(historical.SelectMany(message => message.Content).OfType<TextBlock>(),
            block => block.Text.Contains("child", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_canonical_root_keeps_its_artifact_alive_during_gc()
    {
        using var fixture = new Fixture();
        fixture.Record("input", "answer");
        fixture.Cancel();
        fixture.Service.EnsureRecorded(fixture.Session);
        var (_, artifact) = Assert.Single(fixture.Service.Read(fixture.Session, RunId.New()));
        new ArtifactGc(fixture.Root).Sweep(fixture.Journal, TimeSpan.Zero, false,
            DateTimeOffset.UtcNow.AddDays(2), TestContext.Current.CancellationToken);
        Assert.True(fixture.Artifacts.Verify(artifact.Hash, artifact.Size));
        fixture.Reopen();
        Assert.Single(fixture.Service.Read(fixture.Session, RunId.New()));
    }

    [Fact]
    public void Summary_with_mismatched_session_is_rejected_instead_of_becoming_context()
    {
        using var fixture = new Fixture();
        fixture.Cancel();
        var through = fixture.Store.CurrentSequence(fixture.Session);
        var forged = new RunSummary(SessionId.New(), fixture.Run.RunId, through, "Cancelled", "objective",
            Array.Empty<string>(), "answer", Array.Empty<string>(), "", false);
        var artifact = fixture.Artifacts.PutText(JsonSerializer.Serialize(forged, RunSummaryJson.Default.RunSummary),
            "application/vnd.omnicore.run-summary+json", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(new RunSummaryRecorded(fixture.Run.RunId, through, artifact));
        Assert.Throws<InvalidDataException>(() => fixture.Service.Read(fixture.Session, RunId.New()));
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(8192, false)]
    public void Next_run_uses_summary_only_when_prior_history_exceeds_budget(int budget, bool expectSummary)
    {
        using var fixture = new Fixture();
        var answer = "Durable decision: PostgreSQL. " + string.Join(' ', Enumerable.Repeat("padding", 1000));
        fixture.Record("elige base", answer);
        fixture.Cancel();
        fixture.Reopen(); // old journals without a summary are repaired at the next turn
        var next = TestRun.Open(fixture.Store, fixture.Session, "continuar");
        ModelRequest? received = null;
        var turn = fixture.Turn((request, _) =>
        {
            received = request;
            return new ModelResponse(new ContentBlock[] { new TextBlock("continued") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
        }, budget);
        var result = turn.Ask("continúa con esa base", "system", fixture.Session, next.RunId, next.RootLane,
            "CURRENT WORKING STATE", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.NotNull(received);
        Assert.Contains("CURRENT WORKING STATE", received.Instructions);
        Assert.Contains(received.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
            text => text.Text == "continúa con esa base");
        if (expectSummary)
        {
            Assert.Contains("Historical Run summary", received.Instructions);
            Assert.Contains("Durable decision: PostgreSQL", received.Instructions);
            Assert.DoesNotContain(received.Messages.SelectMany(message => message.Content).OfType<TextBlock>(), text => text.Text == answer);
        }
        else
        {
            Assert.DoesNotContain("Historical Run summary", received.Instructions);
            Assert.Contains(received.Messages.SelectMany(message => message.Content).OfType<TextBlock>(), text => text.Text == answer);
        }
        var snapshot = fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).OfType<ModelStepStarted>().Last().ContextSnapshotRef;
        using var json = JsonDocument.Parse(fixture.Artifacts.GetText(snapshot!.Hash)!);
        Assert.True(json.RootElement.GetProperty("tokenCount").GetInt32() <= budget);
        Assert.Single(fixture.Service.Read(fixture.Session, next.RunId));
    }

    [Fact]
    public void Many_historical_summaries_must_not_evict_the_current_user_message()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 6; i++)
        {
            fixture.Record("previous goal " + i, "decision " + i + " " + string.Join(' ', Enumerable.Repeat("padding", 1000)));
            fixture.Cancel();
            fixture.Service.EnsureRecorded(fixture.Session);
            fixture.StartNext();
        }
        ModelRequest? received = null;
        var turn = fixture.Turn((request, _) =>
        {
            received = request;
            return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
        }, 500);
        var result = turn.Ask("CURRENT USER INTENT MUST REACH THE MODEL", "system", fixture.Session,
            fixture.Run.RunId, fixture.Run.RootLane, "CURRENT WORKING STATE", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.Contains(received!.Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
            text => text.Text == "CURRENT USER INTENT MUST REACH THE MODEL");
        Assert.Contains("CURRENT WORKING STATE", received.Instructions);
    }

    [Fact]
    public void Oversized_current_intent_fails_before_provider_dispatch_instead_of_silently_disappearing()
    {
        using var fixture = new Fixture();
        fixture.Record("previous goal", "previous answer");
        fixture.Cancel();
        fixture.StartNext();
        var calls = 0;
        var turn = fixture.Turn((_, _) =>
        {
            calls++;
            return new ModelResponse(new ContentBlock[] { new TextBlock("must not be dispatched") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
        }, 500);
        var result = turn.Ask(string.Join(' ', Enumerable.Repeat("current-intent", 1000)), "system", fixture.Session,
            fixture.Run.RunId, fixture.Run.RootLane, "CURRENT WORKING STATE", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.ContextOverflow, result.StopReason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Legacy_principal_lane_answer_is_preserved_in_the_structured_summary()
    {
        using var fixture = new Fixture();
        var turn = TurnId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId, fixture.Run.RootTask, fixture.Run.RootLane, turn)))
        {
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).AppendBatch(new DomainEventPayload[] {
                new TurnStarted(turn, fixture.Run.RootLane),
                new ModelCompleted(turn, fixture.Artifacts.PutText("legacy PostgreSQL decision", "text/plain",
                    ArtifactKind.ModelResponse, Sensitivity.Sensitive)), new TurnCompleted(turn),
            }, DurabilityClass.Standard);
        }
        fixture.Cancel();
        fixture.Service.EnsureRecorded(fixture.Session);
        var (summary, _) = Assert.Single(fixture.Service.Read(fixture.Session, RunId.New()));
        Assert.Equal("legacy PostgreSQL decision", summary.FinalResponse);
    }

    [Fact]
    public void Principal_checkpoint_receipt_is_preserved_without_copying_tool_or_reasoning_body()
    {
        using var fixture = new Fixture();
        fixture.Record("goal", "public answer");
        var checkpoint = fixture.Artifacts.PutText("{\"summary\":\"raw tool output and reasoning must not cross Runs\"}",
            "application/json", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        using (ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId, fixture.Run.RootTask, fixture.Run.RootLane)))
            new EventStream(fixture.Store, fixture.Codecs, fixture.Session).Append(new ContextCheckpointRecorded(
                "principal-checkpoint", fixture.Run.RunId, fixture.Store.CurrentSequence(fixture.Session), checkpoint, "deterministic"));
        fixture.Cancel();
        fixture.Service.EnsureRecorded(fixture.Session);
        var (summary, artifact) = Assert.Single(fixture.Service.Read(fixture.Session, RunId.New()));
        Assert.Contains(checkpoint.Hash.ToString(), summary.Checkpoint);
        Assert.DoesNotContain("raw tool output", fixture.Artifacts.GetText(artifact.Hash)!);
        Assert.DoesNotContain("reasoning must not cross", fixture.Service.Render(summary));
    }

    [Fact]
    public void Tampered_summary_artifact_is_not_accepted_as_historical_facts()
    {
        using var fixture = new Fixture();
        fixture.Cancel();
        fixture.Service.EnsureRecorded(fixture.Session);
        var (_, artifact) = Assert.Single(fixture.Service.Read(fixture.Session, RunId.New()));
        var hash = artifact.Hash.Value;
        var blob = Path.Combine(fixture.Root, "blobs", "sha256", hash[..2], hash.Substring(2, 2), hash);
        File.WriteAllText(blob, "tampered fixture data");
        Assert.Throws<InvalidDataException>(() => fixture.Service.Read(fixture.Session, RunId.New()));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-run-summary-" + Guid.NewGuid().ToString("N"));
        public string Journal => Path.Combine(Root, "journal.db");
        public SqliteEventStore Store { get; private set; }
        public IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        public SessionId Session { get; } = SessionId.New();
        public TestRun.Opened Run { get; private set; }
        public FileArtifactStore Artifacts { get; }
        public RunSummaryService Service => new(Store, Codecs, Artifacts,
            text => text.Replace("fixture-secret", "[redacted]", StringComparison.Ordinal));
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Store = new SqliteEventStore(Journal);
            Artifacts = new FileArtifactStore(Root);
            Run = TestRun.Open(Store, Session);
        }
        public void Record(string input, string answer, LaneId? lane = null, TaskId? task = null)
        {
            var targetLane = lane ?? Run.RootLane;
            var turn = TurnId.New();
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, task ?? Run.RootTask, targetLane, turn));
            new EventStream(Store, Codecs, Session).AppendBatch(new DomainEventPayload[] {
                new UserInputReceived(Run.RunId, JsonSerializer.Serialize(new[] { input }), null),
                new TurnStarted(turn, targetLane),
                new AssistantMessageRecorded(Run.RunId, targetLane, turn,
                    Artifacts.PutText(answer, "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Sensitive)),
                new TurnCompleted(turn),
            }, DurabilityClass.Standard);
        }
        public void Cancel() => new RunControlService(Store, Codecs).CancelRun(Session, Run.RunId);
        public void StartNext() => Run = TestRun.Open(Store, Session, "next goal");
        public ExplorerTurn Turn(Func<ModelRequest, CancellationToken, ModelResponse> provider, int budget = 8192)
        {
            var catalog = new FakeCatalog();
            return new ExplorerTurn(provider, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), Root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture"),
                new ModelSelection(new ModelIdValue("scripted"), budget, ToolMode.Direct, null),
                Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy());
        }
        public void Reopen() { Store.Close(); Store = new SqliteEventStore(Journal); }
        public void Dispose()
        {
            Store.Close();
            using var connection = new SqliteConnection("DataSource=" + Journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
