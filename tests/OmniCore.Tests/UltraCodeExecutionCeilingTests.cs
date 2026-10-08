using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Production Explorer/Host with private SQLite/CAS and scripted responses, no paid requests.</summary>
public sealed class UltraCodeExecutionCeilingTests
{
    [Fact]
    public void Suspension_and_reopen_keep_one_turn_and_do_not_reset_tool_ceiling()
    {
        using var fixture = new Fixture(maxTurns: 1, maxTools: 1);
        var schema = new QuestionnaireSchema("Choice", null,
            [new QuestionField("choice", "Which?", null, QuestionKind.SingleChoice,
                [new QuestionOption("yes", "Yes", null)], null, true, null, null, null)]);
        var ask = ToolCallId.New();
        var suspended = fixture.MakeTurn((_, _) => Response([
            new ToolCallBlock(ask, "ask", "user.ask", QuestionnaireCodec.EncodeSchema(schema))], StopReason.ToolUse))
            .Ask("ask", "system", fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.InputRequired, suspended.StopReason);
        Assert.NotNull(suspended.TurnId);
        fixture.Reopen();
        var server = new OmniServer(fixture.Store, fixture.Codecs, new InMemoryAuditSink(),
            Path.Combine(fixture.Root, "last.txt"), fixture.Artifacts);
        Assert.Equal("ok", server.RespondToQuestionnaire(suspended.PendingInteractionId!,
            [new QuestionAnswer("choice", ["yes"], null, null)], false).Status);
        var resumed = fixture.MakeTurn((_, _) => Response([
            new ToolCallBlock(ToolCallId.New(), "read", "fake.read", "{}")], StopReason.ToolUse))
            .Ask("", "system", fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.Cancelled, resumed.StopReason);
        Assert.Equal(suspended.TurnId, resumed.TurnId);
        Assert.Single(fixture.Payloads.OfType<TurnStarted>());
        Assert.Equal(2, fixture.Payloads.OfType<ModelStepCompleted>().Count());
        Assert.Equal(ask, Assert.Single(fixture.Payloads.OfType<ToolCallRequested>()).ToolCallId);
        Assert.DoesNotContain(fixture.Payloads.OfType<ToolCallRequested>(), call => call.ToolName == "fake.read");
    }

    [Fact]
    public void Spent_receipts_reduce_the_next_quote_without_resetting_at_each_model_step()
    {
        using var fixture = new Fixture(maxSpend: 0.01m);
        var calls = 0;
        var result = fixture.MakeTurn((_, _) =>
        {
            calls++;
            return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "read", "fake.read", "{}")],
                StopReason.ToolUse, new TokenUsage(1000, 0, 0, 0, 0), null, new ProviderMetadata("fixture", "model", null),
                ReportedUsageFields: TokenUsageFields.All);
        }, missing: "priced").Ask("read then continue", "system", fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(1, calls);
        Assert.Single(fixture.Payloads.OfType<ModelStepCompleted>());
        Assert.Single(fixture.Payloads.OfType<ToolCallSucceeded>());
        Assert.Equal(0.001m, fixture.Payloads.OfType<ModelStepCompleted>().Single().CostUsd);
        Assert.DoesNotContain("allow_plus", fixture.Payloads.OfType<InteractionRequested>().Single().OptionsJson);
    }

    [Fact]
    public void Elapsed_limit_cancels_an_inflight_provider_without_tools_or_success_receipt()
    {
        using var fixture = new Fixture(maxSeconds: 2);
        var calls = 0;
        var result = fixture.MakeTurn((_, token) =>
        {
            calls++;
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            token.ThrowIfCancellationRequested();
            return Response([], StopReason.EndTurn);
        }).Ask("wait", "system", fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(1, calls);
        Assert.Single(fixture.Payloads.OfType<TurnInterrupted>());
        Assert.Empty(fixture.Payloads.OfType<ModelStepCompleted>());
        Assert.Empty(fixture.Payloads.OfType<ToolCallRequested>());
    }

    [Fact]
    public void Model_steps_are_not_turns_and_new_turn_cannot_reset_the_run_limit_after_reopen()
    {
        using var fixture = new Fixture(maxTurns: 1);
        var calls = 0;
        var result = fixture.MakeTurn((_, _) => ++calls == 1
            ? Response([new ToolCallBlock(ToolCallId.New(), "read", "fake.read", "{}")], StopReason.ToolUse)
            : Response([new TextBlock("done")], StopReason.EndTurn)).Ask("first", "system",
                fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.NotNull(result.TurnId);
        Assert.Equal(2, calls);
        Assert.Single(fixture.Payloads.OfType<TurnStarted>());
        Assert.Equal(2, fixture.Payloads.OfType<ModelStepCompleted>().Count());
        fixture.Reopen();
        var denied = fixture.MakeTurn((_, _) => { calls++; return Response([new TextBlock("forbidden")], StopReason.EndTurn); })
            .Ask("second", "system", fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.Cancelled, denied.StopReason);
        Assert.Equal(2, calls);
        Assert.Single(fixture.Payloads.OfType<TurnStarted>());
        var gate = Assert.Single(fixture.Payloads.OfType<InteractionRequested>());
        Assert.Equal(InteractionKind.BudgetExceeded, gate.Kind);
        Assert.DoesNotContain("allow_plus", gate.OptionsJson);
    }

    [Fact]
    public void Tool_limit_stops_second_effect_even_when_both_calls_are_in_one_response()
    {
        using var fixture = new Fixture(maxTools: 1);
        var first = ToolCallId.New();
        var second = ToolCallId.New();
        var calls = 0;
        var result = fixture.MakeTurn((_, _) => { calls++; return Response([
            new ToolCallBlock(first, "one", "fake.read", "{}"),
            new ToolCallBlock(second, "two", "fake.read", "{}")], StopReason.ToolUse); })
            .Ask("two tools", "system", fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(1, calls);
        Assert.Equal(first, Assert.Single(fixture.Payloads.OfType<ToolCallRequested>()).ToolCallId);
        Assert.Equal(first, Assert.Single(fixture.Payloads.OfType<ToolCallSucceeded>()).ToolCallId);
        Assert.DoesNotContain(fixture.Payloads.OfType<ToolCallRequested>(), call => call.ToolCallId == second);
    }

    [Theory]
    [InlineData("price")]
    [InlineData("capacity")]
    [InlineData("output")]
    [InlineData("attempts")]
    [InlineData("quote")]
    public void Incomplete_or_excessive_quote_is_denied_before_provider_or_turn_start(string missing)
    {
        using var fixture = new Fixture(maxSpend: 0.001m);
        var calls = 0;
        var result = fixture.MakeTurn((_, _) => { calls++; return Response([], StopReason.EndTurn); },
            missing: missing).Ask("request", "system", fixture.Session, fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Equal(0, calls);
        Assert.Empty(fixture.Payloads.OfType<TurnStarted>());
        Assert.Empty(fixture.Payloads.OfType<ModelStepStarted>());
        Assert.Equal(InteractionKind.BudgetExceeded, Assert.Single(fixture.Payloads.OfType<InteractionRequested>()).Kind);
    }

    [Fact]
    public void Unreported_usage_roots_receipt_but_does_not_authorize_tools_or_another_step()
    {
        using var fixture = new Fixture();
        var result = fixture.MakeTurn((_, _) => new ModelResponse([
            new ToolCallBlock(ToolCallId.New(), "read", "fake.read", "{}")], StopReason.ToolUse,
            new TokenUsage(0, 0, 0, 0, 0), null, new ProviderMetadata("fixture", "model", null),
            ReportedUsageFields: TokenUsageFields.None)).Ask("read", "system", fixture.Session,
                fixture.Run, fixture.Lane, "", CancellationToken.None);
        Assert.Equal(StopReason.Cancelled, result.StopReason);
        Assert.Single(fixture.Payloads.OfType<ModelStepCompleted>());
        Assert.Empty(fixture.Payloads.OfType<ToolCallRequested>());
        Assert.Equal(1, SessionUsageReporter.ReadConversation(fixture.Store, fixture.Codecs,
            fixture.Artifacts, fixture.Session).IncompleteInvocations);
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_explorer_instances_cannot_both_admit_the_only_remaining_turn()
    {
        using var fixture = new Fixture(maxTurns: 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var first = System.Threading.Tasks.Task.Run(() => fixture.MakeTurn((_, _) =>
        {
            Interlocked.Increment(ref calls); entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            return Response([new TextBlock("done")], StopReason.EndTurn);
        }).Ask("first", "system", fixture.Session, fixture.Run, fixture.Lane, "", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var second = System.Threading.Tasks.Task.Run(() => fixture.MakeTurn((_, _) =>
        {
            Interlocked.Increment(ref calls); return Response([], StopReason.EndTurn);
        }).Ask("second", "system", fixture.Session, fixture.Run, fixture.Lane, "", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        release.Set();
        var results = await System.Threading.Tasks.Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.EndTurn, results[0].StopReason);
        Assert.Equal(StopReason.Cancelled, results[1].StopReason);
        Assert.Equal(1, calls);
        Assert.Single(fixture.Payloads.OfType<TurnStarted>());
    }

    private static ModelResponse Response(ContentBlock[] content, StopReason stop) => new(content, stop,
        new TokenUsage(2, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "model", null),
        ReportedUsageFields: TokenUsageFields.All);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-ultra-ceiling-" + Guid.NewGuid().ToString("N"));
        public SqliteEventStore Store { get; private set; }
        public IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        public FileArtifactStore Artifacts { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public LaneId Lane { get; }
        public IEnumerable<DomainEventPayload> Payloads => Store.ReadFrom(Session, 1).Select(Codecs.Decode);
        public Fixture(int maxTurns = 4, int maxTools = 8, decimal maxSpend = 1m, int maxSeconds = 120)
        {
            Directory.CreateDirectory(Root);
            Store = new SqliteEventStore(Path.Combine(Root, "journal.db"));
            Artifacts = new FileArtifactStore(Root);
            var server = new OmniServer(Store, Codecs, new InMemoryAuditSink(), Path.Combine(Root, "last.txt"), Artifacts);
            Assert.Equal("ok", server.SendUserAction(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"mode\":\"act\",\"text\":\"bounded task\"}"), CancellationToken.None).Status);
            Session = server.LastSessionId()!; Run = server.LastRunId()!; Lane = server.LastLaneId()!;
            var selection = "{\"cmd\":\"run.mode.select\",\"mode\":\"act\",\"effort\":\"ultracode\","
                + "\"adaptive\":true,\"allowedModes\":\"plan,act,orq\",\"maxAgents\":1,\"maxDepth\":1,"
                + "\"maxTurns\":" + maxTurns + ",\"maxToolCalls\":" + maxTools + ",\"maxElapsedSeconds\":" + maxSeconds + ","
                + "\"maxSpendUsd\":" + maxSpend.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            Assert.Equal("ok", server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), selection), CancellationToken.None).Status);
        }
        public ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, string? missing = null)
        {
            var catalog = FakeCatalog.Default().Add(new UserAskTool());
            return new ExplorerTurn(complete, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy([]), Root), catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null,
                    maxOutputTokens: missing == "output" ? null : 1024), Store, Codecs, Artifacts,
                new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: missing == "price" ? null : new ModelPricing(missing is "quote" or "priced" ? 1m : 0m, missing == "priced" ? 1m : 0m),
                modelContextCapacity: missing == "capacity" ? null : 8192,
                maximumGenerationRequestAttempts: missing == "attempts" ? null : 1,
                questionnaires: new QuestionnaireInteractionService(Store, Codecs, Artifacts));
        }
        public void Reopen()
        {
            Close(); Store = new SqliteEventStore(Path.Combine(Root, "journal.db"));
        }
        private void Close()
        {
            var connection = (SqliteConnection)Store.Connection;
            Store.Close(); SqliteConnection.ClearPool(connection); connection.Dispose();
        }
        public void Dispose() { Close(); Directory.Delete(Root, true); }
    }
}
