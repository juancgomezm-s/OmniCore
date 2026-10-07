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
/// Proposed offline boundary tests: a selected reasoning request must not be silently sent to a
/// route that explicitly declares no support, or declares a different finite set of effort labels.
/// Supported requests are passed through; legacy/Unknown declarations stay unknown and are not
/// inferred from model names. No provider protocol dialect, credentials, or external endpoint is used.
/// </summary>
public sealed class DeclaredReasoningRequestTests
{
    private static readonly ReasoningRequest High = new("high", null);

    [Fact]
    public void No_reasoning_request_is_allowed_even_when_reasoning_is_explicitly_unsupported()
    {
        new ReasoningCapability(false).ValidateRequest(null);
        new ReasoningCapability(true, []).ValidateRequest(null);
    }

    [Fact]
    public void Declared_labels_are_exact_not_case_folded_or_universally_ranked()
    {
        var capability = new ReasoningCapability(true, ["adaptive"]);
        capability.ValidateRequest(new ReasoningRequest("adaptive", null));
        Assert.Throws<InvalidOperationException>(() =>
            capability.ValidateRequest(new ReasoningRequest("Adaptive", null)));
        Assert.Throws<InvalidOperationException>(() => capability.ValidateRequest(High));
    }

    [Fact]
    public void Unreported_efforts_are_not_an_empty_allowlist_and_budget_is_not_a_label_mapping()
    {
        new ReasoningCapability(true).ValidateRequest(new ReasoningRequest("provider-specific", 2048));
        ReasoningCapability.Unknown.ValidateRequest(new ReasoningRequest("provider-specific", 2048));
        Assert.Throws<InvalidOperationException>(() => new ReasoningCapability(true, []).ValidateRequest(High));
    }

    [Fact]
    public void UltraCode_budget_capability_requires_both_explicit_token_values()
    {
        Assert.Throws<ArgumentException>(() => new ReasoningCapability(true, ["budget"],
            ultraCodeBudgetTokens: null, ultraCodeOutputReserveTokens: 1));
        Assert.Throws<ArgumentException>(() => new ReasoningCapability(true, ["budget"],
            ultraCodeBudgetTokens: 1024, ultraCodeOutputReserveTokens: null));
        _ = new ReasoningCapability(true, ["budget"], ultraCodeBudgetTokens: 1024,
            ultraCodeOutputReserveTokens: 1);
    }

    [Fact]
    public void Explicitly_unsupported_reasoning_is_rejected_before_provider_or_tool_authorization()
    {
        var result = Execute(new ReasoningCapability(false));

        Assert.Equal(StopReason.Error, result.Result.StopReason);
        Assert.Equal(0, result.ProviderCalls);
        Assert.Empty(result.Requests);
        Assert.Empty(result.Events.OfType<ModelStepCompleted>());
        Assert.Empty(result.Events.OfType<ToolCallAuthorized>());
    }

    [Fact]
    public void Effort_outside_the_declared_labels_is_rejected_before_provider_or_tool_authorization()
    {
        var result = Execute(new ReasoningCapability(true, ["low"]));

        Assert.Equal(StopReason.Error, result.Result.StopReason);
        Assert.Equal(0, result.ProviderCalls);
        Assert.Empty(result.Requests);
        Assert.Empty(result.Events.OfType<ModelStepCompleted>());
        Assert.Empty(result.Events.OfType<ToolCallAuthorized>());
    }

    [Fact]
    public void Supported_effort_is_allowed_and_preserved_on_the_neutral_model_request()
    {
        var result = Execute(new ReasoningCapability(true, ["high"]));

        Assert.Equal(StopReason.EndTurn, result.Result.StopReason);
        Assert.Equal(1, result.ProviderCalls);
        Assert.Equal(High, Assert.Single(result.Requests).Reasoning);
        Assert.Single(result.Events.OfType<ModelStepCompleted>());
    }

    [Fact]
    public void Legacy_unknown_capability_does_not_infer_support_or_unsupported_from_model_name()
    {
        // The suggestive legacy name is deliberately not a capability declaration.
        var result = Execute(ReasoningCapability.Unknown,
            modelId: "reasoning-ultracode-high");

        Assert.Equal(StopReason.EndTurn, result.Result.StopReason);
        Assert.Equal(1, result.ProviderCalls);
        Assert.Equal(High, Assert.Single(result.Requests).Reasoning);
        Assert.Single(result.Events.OfType<ModelStepCompleted>());
    }

    private static Observation Execute(ReasoningCapability capability, string modelId = "fixture-model")
    {
        using var fx = new Fixture(capability, modelId);
        var requests = new List<ModelRequest>();
        var calls = 0;
        var turn = fx.MakeTurn((request, _) =>
        {
            calls++;
            requests.Add(request);
            return new ModelResponse(new ContentBlock[] { new TextBlock("ok") }, StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture-model", null));
        });

        var result = turn.Ask("reasoning boundary", "system", fx.SessionId, fx.Run.RunId,
            fx.Run.RootLane, "", CancellationToken.None);
        var events = fx.Store.ReadFrom(fx.SessionId, 1).Select(fx.Codecs.Decode).ToArray();
        return new Observation(result, calls, requests, events);
    }

    private sealed record Observation(ExplorerTurn.TurnResult Result, int ProviderCalls,
        IReadOnlyList<ModelRequest> Requests, IReadOnlyList<DomainEventPayload> Events);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "omni-reasoning-request-" + Guid.NewGuid().ToString("N"));
        private readonly FakeCatalog _catalog = FakeCatalog.Default();
        private readonly IToolExecutor _executor;

        public Fixture(ReasoningCapability capability, string modelId)
        {
            Directory.CreateDirectory(_root);
            JournalPath = Path.Combine(_root, "journal.db");
            Store = new SqliteEventStore(JournalPath);
            Codecs = EventCodecs.Create();
            Artifacts = new FileArtifactStore(_root);
            SessionId = OmniCore.Domain.SessionId.New();
            Run = TestRun.Open(Store, SessionId, mode: RunMode.Plan);
            Model = new ModelDefinition(modelId, "fixture-provider", 8192, 7000, 2048, 1000,
                reasoningCapability: capability);
            Route = ModelRoute.DefaultForModel(modelId, Model.ProviderId, "http://127.0.0.1:9917",
                ProviderFamily.OpenAiChatCompatible, reasoningCapability: capability);
            Selection = new ModelSelection(new ModelIdValue(modelId), 8192, ToolMode.Direct,
                High, Route.Id, Route);
            _executor = ScriptedToolExecutor.WithWorkspace(_catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), _root);
        }

        public string JournalPath { get; }
        public SqliteEventStore Store { get; }
        public IEventCodecRegistry Codecs { get; }
        public FileArtifactStore Artifacts { get; }
        public OmniCore.Domain.SessionId SessionId { get; }
        public TestRun.Opened Run { get; }
        public ModelDefinition Model { get; }
        public ModelRoute Route { get; }
        public ModelSelection Selection { get; }

        public ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete)
        {
            var profile = new ModelProfileResolver().Resolve(Model, null, route: Route);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var fingerprint = RuntimeFingerprintFactory.Create(Model, profile, harness, Selection,
                "harness", "context", "policy", "counter");
            return new ExplorerTurn(complete, _executor, _catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                fingerprint, Selection, Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy());
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
