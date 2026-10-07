using Microsoft.Data.Sqlite;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Real private SQLite/CAS with offline providers; no authenticated usage claim.</summary>
public sealed class RunTokenBudgetAdmissionTests
{
    [Theory]
    [InlineData(9216L, false)]
    [InlineData(20000L, true)]
    public void New_turn_admission_replays_prior_consumption_instead_of_resetting_the_run_limit(long limit, bool secondAllowed)
    {
        using var fx = new Fixture(limit);
        Assert.Equal(StopReason.EndTurn, fx.MakeTurn().Ask("first", "system", fx.Session, fx.Run.RunId,
            fx.Run.RootLane, "", TestContext.Current.CancellationToken).StopReason);
        var second = fx.MakeTurn().Ask("second", "system", fx.Session, fx.Run.RunId,
            fx.Run.RootLane, "", TestContext.Current.CancellationToken);
        Assert.Equal(secondAllowed ? StopReason.EndTurn : StopReason.Cancelled, second.StopReason);
        Assert.Equal(secondAllowed ? 2 : 1, fx.Calls);
        var events = fx.Store.ReadFrom(fx.Session, 1);
        Assert.Equal(secondAllowed ? 2 : 1, events.Select(fx.Codecs.Decode).OfType<ModelStepCompleted>().Count());
        Assert.Equal(secondAllowed ? 2 : 1, events.Select(fx.Codecs.Decode).OfType<TurnStarted>().Count());
        var snapshot = RunTokenBudgetReader.Read(events, fx.Codecs, fx.Run.RunId, limit);
        Assert.Equal(limit - fx.Calls * 1000L, snapshot.Remaining);
        Assert.Equal(snapshot, RunTokenBudgetReader.Read(events.Concat(events).ToArray(), fx.Codecs, fx.Run.RunId, limit));
        if (!secondAllowed)
            Assert.Contains(events.Select(fx.Codecs.Decode).OfType<InteractionRequested>(),
                request => request.Kind == InteractionKind.BudgetExceeded);
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("attempts")]
    [InlineData("output")]
    public void Undeclared_generation_exposure_blocks_before_new_turn_and_provider(string missing)
    {
        using var fx = new Fixture(20000);
        var result = fx.MakeTurn(capacity: missing == "capacity" ? null : 8192,
            attempts: missing == "attempts" ? null : 1,
            output: missing == "output" ? null : 1024).Ask("first", "system", fx.Session, fx.Run.RunId,
                fx.Run.RootLane, "", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(0, fx.Calls);
        Assert.Empty(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<TurnStarted>());
        Assert.Empty(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<ModelStepStarted>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_reported_output_or_uncertain_send_does_not_become_zero_usage(bool uncertain)
    {
        using var fx = new Fixture(30000);
        var first = fx.MakeTurn(uncertain: uncertain, fields: TokenUsageFields.Input).Ask("first", "system",
            fx.Session, fx.Run.RunId, fx.Run.RootLane, "", TestContext.Current.CancellationToken);
        Assert.NotEqual(StopReason.EndTurn, first.StopReason);
        Assert.Equal(1, fx.Calls);
        var snapshot = RunTokenBudgetReader.Read(fx.Store.ReadFrom(fx.Session, 1), fx.Codecs, fx.Run.RunId, 30000);
        Assert.Null(snapshot.Remaining);
        Assert.NotNull(snapshot.Limitation);
    }

    [Fact]
    public void Reader_includes_context_service_usage_without_double_counting_reasoning_cache_or_other_runs()
    {
        using var fx = new Fixture(30000);
        var turn = TurnId.New();
        var otherRun = RunId.New();
        var artifact = fx.Artifacts.PutText("offline context fixture", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
        DomainEvent Wrap(DomainEventPayload payload, RunId origin) => DomainEvent.Create(fx.Session, payload.Type(),
            payload.SchemaVersion(), null, origin, origin, null, null, null, null, null, [],
            fx.Codecs.CodecFor(payload.Type()).Encode(payload));
        var events = new[]
        {
            Wrap(new ModelStepStarted(turn, 0, "fixture", 4096, "Direct", null, null, null), fx.Run.RunId),
            Wrap(new ModelStepCompleted(turn, 0, new TokenUsage(300, 100, 100, 100, 50), StopReason.EndTurn,
                null, "2026-10-07", null, TokenUsageFields.All), fx.Run.RunId),
            Wrap(new MetaModelInvocationStarted("meta", fx.Run.RunId, "compact", "fixture", artifact), fx.Run.RunId),
            Wrap(new MetaModelInvocationCompleted("meta", fx.Run.RunId, "compact", "fixture", artifact,
                new TokenUsage(50, 70, 0, 0, 0), null, TokenUsageFields.Input | TokenUsageFields.Output), fx.Run.RunId),
            Wrap(new MetaModelInvocationStarted("other", otherRun, "compact", "fixture", artifact), otherRun),
        };
        var result = RunTokenBudgetReader.Read(events, fx.Codecs, fx.Run.RunId, 1000);
        Assert.Equal(480, result.Remaining);
        Assert.Null(result.Limitation);
        Assert.Equal(result, RunTokenBudgetReader.Read(events.Concat(events).ToArray(), fx.Codecs, fx.Run.RunId, 1000));
        var withoutCompletion = RunTokenBudgetReader.Read(events.Where(evt => evt.Type.ToString()
            != "meta_model.invocation_completed").ToArray(), fx.Codecs, fx.Run.RunId, 1000);
        Assert.Null(withoutCompletion.Remaining);
        Assert.Equal("unsettled provider invocation", withoutCompletion.Limitation);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-token-admission-" + Guid.NewGuid().ToString("N"));
        public SqliteEventStore Store { get; }
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public SessionId Session { get; } = SessionId.New();
        public TestRun.Opened Run { get; }
        public FileArtifactStore Artifacts { get; }
        public int Calls;
        public Fixture(long limit)
        {
            Directory.CreateDirectory(Root);
            Store = new SqliteEventStore(Path.Combine(Root, "journal.db"));
            Artifacts = new FileArtifactStore(Path.Combine(Root, "cas"));
            Run = TestRun.Open(new EventStream(Store, Codecs, Session), Session,
                taskBudget: new TaskBudget(null, limit, null, null));
        }
        public ExplorerTurn MakeTurn(long? capacity = 8192, long? attempts = 1, long? output = 1024,
            bool uncertain = false, TokenUsageFields fields = TokenUsageFields.All)
        {
            var catalog = new FakeCatalog();
            return new ExplorerTurn((_, _) =>
            {
                Calls++;
                if (uncertain) throw new IOException("offline uncertain provider send");
                return new ModelResponse([new TextBlock("fixture complete")], StopReason.EndTurn,
                    new TokenUsage(400, 600, 0, 0, 0), null, new ProviderMetadata("offline", "fixture", null), fields);
            }, ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy([]), Root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
                new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null, maxOutputTokens: output),
                Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                modelContextCapacity: capacity, maximumGenerationRequestAttempts: attempts);
        }
        public void Dispose()
        {
            Store.Close();
            SqliteConnection.ClearPool((SqliteConnection)Store.Connection);
            Store.Connection.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
