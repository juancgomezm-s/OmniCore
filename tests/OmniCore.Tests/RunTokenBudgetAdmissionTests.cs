using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Models;
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
    [Fact]
    public void Native_http_retry_keeps_final_response_usage_but_unknown_total_after_sqlite_reopen()
    {
        // In-memory HTTP handler exercises the production adapter; no authenticated request.
        using var fx = new Fixture(30000);
        using var handler = new RetryHandler();
        var provider = new OpenAiChatCompatibleProvider(new ProviderDescriptor("retry-fixture",
            ProviderFamily.OpenAiChatCompatible, "https://example.test/v1", AuthConfig.None(), false, false, true),
            new NoSecrets(), () => new HttpClient(handler, disposeHandler: false),
            new OpenAiProviderOptions { MaxRetries = 1, DelayAsync = (_, _) => ValueTask.CompletedTask });
        var first = fx.MakeTurn(attempts: 2, complete: provider.Complete, pricing: new ModelPricing(1m, 1m)).Ask("first", "system",
            fx.Session, fx.Run.RunId, fx.Run.RootLane, "", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.Cancelled, first.StopReason);
        Assert.Equal(2, handler.Calls);
        var completed = Assert.Single(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<ModelStepCompleted>());
        Assert.Equal(new TokenUsage(17, 4, 0, 0, 0), completed.Usage);
        Assert.Equal(new GenerationRequestAttemptEvidence(2, 2), completed.GenerationAttempts);
        Assert.Empty(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<ModelStepNotDispatched>());
        fx.Reopen();
        var history = fx.Store.ReadFrom(fx.Session, 1);
        Assert.Null(RunTokenBudgetReader.Read(history, fx.Codecs, fx.Run.RunId, 30000).Remaining);
        var measurement = SessionUsageReporter.ReadConversation(fx.Store, fx.Codecs, fx.Artifacts, fx.Session);
        Assert.Equal(OmniCore.Protocol.MetricAvailability.Unknown, measurement.Total.Availability);
        Assert.Equal(OmniCore.Protocol.MetricAvailability.Unknown, measurement.Cost.Availability);
        Assert.Equal(measurement, SessionUsageReporter.ReadConversation(fx.Store, fx.Codecs, fx.Artifacts, fx.Session));
        var spend = new CanonicalSpendReader(fx.Codecs, fx.Artifacts).ReadPrimary(history, fx.Session,
            fx.Run.RunId, completed.Day);
        Assert.True(spend.Incomplete);
        Assert.Equal(21m / 1_000_000m, spend.RunUsd); // final response estimate retained, not a provider bill
        var second = fx.MakeTurn(attempts: 2, complete: provider.Complete).Ask("second", "system",
            fx.Session, fx.Run.RunId, fx.Run.RootLane, "", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.Cancelled, second.StopReason);
        Assert.Equal(2, handler.Calls);
        Assert.Single(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<TurnStarted>());
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => throw new InvalidOperationException("Fixture has no credentials.");
    }

    [Fact]
    public void Native_transport_failure_after_send_remains_unsettled_after_reopen()
    {
        using var fx = new Fixture(30000);
        using var handler = new RetryHandler(failTransport: true);
        var provider = new OpenAiChatCompatibleProvider(new ProviderDescriptor("transport-fixture",
            ProviderFamily.OpenAiChatCompatible, "https://example.test/v1", AuthConfig.None(), false, false, true),
            new NoSecrets(), () => new HttpClient(handler, disposeHandler: false), new OpenAiProviderOptions { MaxRetries = 0 });
        var result = fx.MakeTurn(attempts: 1, complete: provider.Complete).Ask("first", "system",
            fx.Session, fx.Run.RunId, fx.Run.RootLane, "", TestContext.Current.CancellationToken);
        Assert.NotEqual(StopReason.EndTurn, result.StopReason);
        Assert.Equal(1, handler.Calls);
        fx.Reopen();
        var payloads = fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).ToArray();
        Assert.Single(payloads.OfType<ModelStepStarted>());
        Assert.Empty(payloads.OfType<ModelStepCompleted>());
        Assert.Empty(payloads.OfType<ModelStepNotDispatched>());
        Assert.Null(RunTokenBudgetReader.Read(fx.Store.ReadFrom(fx.Session, 1), fx.Codecs, fx.Run.RunId, 30000).Remaining);
        var retry = fx.MakeTurn(attempts: 1, complete: provider.Complete).Ask("second", "system",
            fx.Session, fx.Run.RunId, fx.Run.RootLane, "", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.Cancelled, retry.StopReason);
        Assert.Equal(1, handler.Calls);
        Assert.Single(fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<TurnStarted>());
    }

    private sealed class RetryHandler(bool failTransport = false) : HttpMessageHandler
    {
        public int Calls;
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (failTransport) throw new HttpRequestException("scripted transport failure after generation send");
            return System.Threading.Tasks.Task.FromResult(Calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("fixture retry") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"fixture complete\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":4}}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream") });
        }
    }

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
                null, "2026-10-07", null, TokenUsageFields.All,
                new GenerationRequestAttemptEvidence(1, 1)), fx.Run.RunId),
            Wrap(new MetaModelInvocationStarted("meta", fx.Run.RunId, "compact", "fixture", artifact), fx.Run.RunId),
            Wrap(new MetaModelInvocationCompleted("meta", fx.Run.RunId, "compact", "fixture", artifact,
                new TokenUsage(50, 70, 0, 0, 0), null, TokenUsageFields.Input | TokenUsageFields.Output,
                new GenerationRequestAttemptEvidence(1, 1)), fx.Run.RunId),
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
        public SqliteEventStore Store { get; private set; }
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
            bool uncertain = false, TokenUsageFields fields = TokenUsageFields.All,
            Func<ModelRequest, CancellationToken, ModelResponse>? complete = null, ModelPricing? pricing = null)
        {
            var catalog = new FakeCatalog();
            return new ExplorerTurn(complete ?? ((_, _) =>
            {
                Calls++;
                if (uncertain) throw new IOException("offline uncertain provider send");
                return new ModelResponse([new TextBlock("fixture complete")], StopReason.EndTurn,
                    new TokenUsage(400, 600, 0, 0, 0), null, new ProviderMetadata("offline", "fixture", null), fields);
            }), ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy([]), Root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
                new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null, maxOutputTokens: output),
                Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                modelContextCapacity: capacity, maximumGenerationRequestAttempts: attempts, pricing: pricing);
        }
        public void Reopen()
        {
            Store.Close();
            SqliteConnection.ClearPool((SqliteConnection)Store.Connection);
            Store.Connection.Dispose();
            Store = new SqliteEventStore(Path.Combine(Root, "journal.db"));
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
