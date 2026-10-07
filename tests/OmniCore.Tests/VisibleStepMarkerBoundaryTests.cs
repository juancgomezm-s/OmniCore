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

/// <summary>
/// Proposed regression for visibleContent tool-call markers. A marker is a projection for one
/// completed model step, so it must resolve only to a canonical ToolCallRequested attributed to
/// that step in the same run/lane/turn. These are private offline SQLite/CAS fixtures; they do not
/// invoke a model provider or imply anything about real provider billing.
/// </summary>
public sealed class VisibleStepMarkerBoundaryTests
{
    [Theory]
    [InlineData("unknown-kind")]
    [InlineData("invalid-visibility")]
    [InlineData("bad-call-id")]
    [InlineData("duplicate-call")]
    public void Malformed_projection_fails_closed_with_constant_safe_error(string scenario)
    {
        using var fx = new Fixture();
        var call = ToolCallId.New();
        object[] blocks = scenario switch
        {
            "unknown-kind" => [new { kind = "unknown", text = "sensitive-fixture-marker" }],
            "invalid-visibility" => [new { kind = "reasoning", text = "sensitive-fixture-marker", visibility = "999" }],
            "bad-call-id" => [new { kind = "tool-call", callId = "sensitive-fixture-marker" }],
            _ => [new { kind = "tool-call", callId = call.ToString() }, new { kind = "tool-call", callId = call.ToString() }],
        };
        fx.Append(new TurnStarted(fx.TurnId, fx.Run.RootLane));
        fx.Append(fx.StepStarted(0));
        fx.Append(fx.StepCompleted(0, fx.VisibleResponseArtifact(blocks)));
        fx.Append(new ToolCallRequested(call, "provider-call", "fixture.tool", "{}"));
        var error = Assert.Throws<InvalidDataException>(() => fx.LoadConversation());
        Assert.Equal("Persisted visible reasoning is invalid.", error.Message);
        Assert.DoesNotContain("sensitive-fixture-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unexecuted_call_marker_does_not_manufacture_a_tool_request()
    {
        using var fx = new Fixture();
        fx.Append(new TurnStarted(fx.TurnId, fx.Run.RootLane));
        fx.Append(fx.StepStarted(0));
        fx.Append(fx.StepCompleted(0, fx.VisibleResponseArtifact(
            new { kind = "text", text = "before" },
            new { kind = "tool-call", callId = ToolCallId.New().ToString() },
            new { kind = "text", text = "after" })));
        var content = fx.LoadConversation().SelectMany(message => message.Content).ToArray();
        Assert.Collection(content,
            block => Assert.Equal("before", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal("after", Assert.IsType<TextBlock>(block).Text));
        Assert.Empty(content.OfType<ToolCallBlock>());
    }

    [Fact]
    public void Visible_marker_cannot_replay_a_call_from_an_earlier_model_step()
    {
        using var fx = new Fixture();
        var callA = ToolCallId.New();
        fx.Append(new TurnStarted(fx.TurnId, fx.Run.RootLane));
        fx.Append(fx.StepStarted(0));
        fx.Append(fx.StepCompleted(0, fx.LegacyResponseArtifact()));
        fx.Append(new ToolCallRequested(callA, "provider-call-a", "fixture.tool", "{\"step\":0}"));
        fx.Append(fx.StepStarted(1));
        fx.Append(fx.StepCompleted(1, fx.VisibleResponseArtifact(
            new { kind = "tool-call", callId = callA.ToString() })));

        var error = Assert.Throws<InvalidDataException>(() => fx.LoadConversation());
        Assert.Contains("Persisted visible reasoning is invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_marker_resolves_the_call_owned_by_its_model_step_once_and_in_order()
    {
        using var fx = new Fixture();
        var callA = ToolCallId.New();
        var callB = ToolCallId.New();
        fx.Append(new TurnStarted(fx.TurnId, fx.Run.RootLane));
        fx.Append(fx.StepStarted(0));
        fx.Append(fx.StepCompleted(0, fx.LegacyResponseArtifact()));
        fx.Append(new ToolCallRequested(callA, "provider-call-a", "fixture.tool", "{\"step\":0}"));
        fx.Append(fx.StepStarted(1));
        fx.Append(fx.StepCompleted(1, fx.VisibleResponseArtifact(
            new { kind = "text", text = "before" },
            new { kind = "tool-call", callId = callB.ToString() },
            new { kind = "text", text = "after" })));
        // Match the actual runtime: completion is durable before executing any tool.
        fx.Append(new ToolCallRequested(callB, "provider-call-b", "fixture.tool", "{\"step\":1}"));

        var assistant = fx.LoadConversation().Where(message => message.Role == MessageRole.Assistant)
            .SelectMany(message => message.Content).ToArray();

        Assert.Collection(assistant,
            block => Assert.Equal(callA, Assert.IsType<ToolCallBlock>(block).Id),
            block => Assert.Equal("before", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal(callB, Assert.IsType<ToolCallBlock>(block).Id),
            block => Assert.Equal("after", Assert.IsType<TextBlock>(block).Text));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "omni-visible-step-marker-" + Guid.NewGuid().ToString("N"));
        private readonly FakeCatalog _catalog;
        private readonly IToolExecutor _executor;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            JournalPath = Path.Combine(_root, "journal.db");
            Store = new SqliteEventStore(JournalPath);
            Codecs = EventCodecs.Create();
            Artifacts = new FileArtifactStore(_root);
            SessionId = OmniCore.Domain.SessionId.New();
            Run = TestRun.Open(Store, SessionId, mode: RunMode.Plan);
            Stream = new EventStream(Store, Codecs, SessionId);
            TurnId = OmniCore.Domain.TurnId.New();
            Route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible, reasoningCapability: new ReasoningCapability(
                    supported: true, effortLevels: null, replayPolicy: ReasoningReplayPolicy.PreserveAcrossSteps));
            Selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, Route.Id, Route);
            _catalog = new FakeCatalog().Add(new UserAskTool());
            _executor = ScriptedToolExecutor.WithWorkspace(_catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), _root);
        }

        public string JournalPath { get; }
        public SqliteEventStore Store { get; }
        public IEventCodecRegistry Codecs { get; }
        public FileArtifactStore Artifacts { get; }
        public OmniCore.Domain.SessionId SessionId { get; }
        public TestRun.Opened Run { get; }
        public EventStream Stream { get; }
        public OmniCore.Domain.TurnId TurnId { get; }
        public ModelRoute Route { get; }
        public ModelSelection Selection { get; }

        public ModelStepStarted StepStarted(int index) => new(TurnId, index, "scripted", 8192,
            "Direct", null, null, null, null, Route.Id);

        public ModelStepCompleted StepCompleted(int index, ArtifactRef response) => new(TurnId, index,
            new TokenUsage(10, 2, 0, 0, 0), StopReason.ToolUse, response, "2026-10-07", null,
            TokenUsageFields.Input | TokenUsageFields.Output);

        public ArtifactRef LegacyResponseArtifact() => Artifacts.PutText("{}", "application/json",
            ArtifactKind.ModelResponse, Sensitivity.Sensitive);

        public ArtifactRef VisibleResponseArtifact(params object[] blocks)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                visibleContent = new
                {
                    version = 1,
                    routeIdentityHash = Selection.RouteIdentityHash,
                    blocks,
                },
            });
            return Artifacts.PutText(json, "application/json", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        }

        public void Append(DomainEventPayload payload)
        {
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(
                Run.RunId, Run.RootTask, Run.RootLane, TurnId));
            Stream.Append(payload, DurabilityClass.Barrier);
        }

        public IReadOnlyList<ModelMessage> LoadConversation()
        {
            var model = new ModelDefinition("scripted", "fixture-provider", 8192, 7000, 2048, 1000,
                reasoningCapability: Selection.Route!.ReasoningCapability);
            var profile = new ModelProfileResolver().Resolve(model, null, route: Selection.Route);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var fingerprint = RuntimeFingerprintFactory.Create(model, profile, harness, Selection,
                "harness", "context", "policy", "counter");
            var turn = new ExplorerTurn((_, _) => throw new InvalidOperationException(
                    "Read-only replay must not invoke the scripted provider."),
                _executor, _catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                fingerprint, Selection, Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy());
            return turn.LoadConversation(new EventStream(Store, Codecs, SessionId), Run.RunId, TurnId, Run.RootLane);
        }

        public void Dispose()
        {
            Store.Close();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = JournalPath,
                Pooling = false,
            }.ToString());
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
