namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class WebResearchIntegrationTests
{
    [Fact]
    public void Missing_or_disabled_user_configuration_does_not_create_research_state_or_resolve_credentials()
    {
        using var fixture = new Fixture();
        Assert.Null(WebResearchHost.Create(fixture.Paths, fixture.WorkspaceData, fixture.Run, "Question"));
        fixture.Config("{\"enabled\":false,\"primaryProvider\":\"brave\"}");
        Assert.Null(WebResearchHost.Create(fixture.Paths, fixture.WorkspaceData, fixture.Run, "Question"));
        Assert.False(File.Exists(Path.Combine(fixture.WorkspaceData, "web-research.db")));
    }

    [Theory]
    [InlineData("point", 2, 3, 1, 4000, 45)]
    [InlineData("normal", 4, 6, 2, 10000, 120)]
    [InlineData("broad", 8, 12, 3, 20000, 300)]
    public void User_configuration_composes_initial_presets_without_a_live_provider(
        string preset, int queries, int reads, int rounds, int tokens, int seconds)
    {
        using var fixture = new Fixture();
        fixture.Config("{\"enabled\":true,\"preset\":\"" + preset + "\",\"allowedOrigins\":[\"https://source.test\"]}");
        var controller = WebResearchHost.Create(fixture.Paths, fixture.WorkspaceData, fixture.Run, "Question")!;

        Assert.Equal((queries, reads, rounds, (long)tokens, seconds),
            (controller.State.Limits.Queries, controller.State.Limits.Reads, controller.State.Limits.Rounds,
                controller.State.Limits.EvidenceTokens, controller.State.Limits.DurationSeconds));
        Assert.Empty(controller.SearchEndpoints);
        Assert.Equal("q1", Assert.Single(controller.State.Questions).Id);
    }

    [Fact]
    public void Common_core_catalog_preserves_text_search_and_declares_provider_safe_web_aliases()
    {
        using var fixture = new Fixture();
        var controller = fixture.Controller();
        var tools = HostTools.Explorer();
        tools.RegisterWebResearchTools(controller);
        var definitions = tools.Catalog().Definitions();
        Assert.IsType<SearchTextTool>(tools.Catalog().Find(new ToolId("filesystem.search")));
        var aliases = ToolNameWireMap.Create(definitions.Select(tool => tool.Name), 64);
        Assert.Equal("grep_files", aliases.ToWire("filesystem.search"));
        Assert.Equal("web_search", aliases.ToWire("search.web"));
        Assert.Equal("web_fetch", aliases.ToWire("web.read"));
        foreach (var name in new[] { "search.web", "web.read" })
        {
            var tool = tools.Catalog().Find(new ToolId(name))!;
            using var schema = JsonDocument.Parse(tool.Descriptor.InputSchema.ToString());
            Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
            Assert.Equal(ToolProtection.Protected, tool.Descriptor.Protection);
            Assert.Equal(ModelToolCapability.Network, ModelCapabilityBoundary.CoreTools[name]);
        }
        var search = tools.Catalog().Find(new ToolId("search.web"))!;
        var prepared = Assert.IsType<Prepared>(search.Prepare(Call("search.web"), new(fixture.Root, DateTimeOffset.UtcNow)));
        Assert.Equal("search.test", Assert.Single(prepared.Intent.Claims.Network).Host);
        var observe = ModelPolicyPresets.ObserveOnly();
        var policy = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "observe"), 1,
            observe.Category, observe.ToolPolicy, observe.MutationPolicy, false);
        Assert.False(new ModelCapabilityBoundary(policy).IsToolVisible("search.web"));
        Assert.True(new ModelCapabilityBoundary(policy).IsToolVisible("filesystem.search"));
    }

    [Fact]
    public void Other_network_tools_cannot_escape_managed_research_by_switching_tool_or_transport()
    {
        var boundary = new WebResearchIntentBoundary();
        var claims = new ResourceClaims([], [], [new("search.test", 443)], null, []);
        ToolIntent Intent(string id) => new(ToolCallId.New(), new ToolId(id), "{}", EffectClass.NonIdempotent, claims, ToolRisk.High, null);
        Assert.False(boundary.Evaluate(Intent("mcp.alternative.web.search")).Allowed);
        Assert.False(boundary.Evaluate(Intent("mcp.alternative.raw_fetch")).Allowed);
        Assert.True(boundary.Evaluate(Intent("search.web")).Allowed);
        Assert.True(boundary.Evaluate(Intent("web.read")).Allowed);
        Assert.True(boundary.Evaluate(new(ToolCallId.New(), new("filesystem.search"), "{}", EffectClass.None,
            ResourceClaims.Empty(), ToolRisk.Low, null)).Allowed);
    }

    [Fact]
    public void Core_permission_denial_precedes_provider_dispatch_and_does_not_spend_a_network_query()
    {
        using var fixture = new Fixture();
        var controller = fixture.Controller();
        var tools = HostTools.Explorer();
        tools.RegisterWebResearchTools(controller);
        var executor = ScriptedToolExecutor.WithWorkspace(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("search.web", PermissionDecision.Deny), fixture.Root);
        using var scope = ExecutionScope.Begin(new(new RunId(Guid.Parse(fixture.Run))));

        var outcome = executor.ExecuteToolWithoutJournal(Call("search.web"), false, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.Equal(0, controller.State.QueriesUsed);
    }

    [Fact]
    public void Authorized_core_tool_uses_the_same_run_controller_and_persists_its_attempt()
    {
        using var fixture = new Fixture();
        var controller = fixture.Controller();
        var tools = HostTools.Explorer();
        tools.RegisterWebResearchTools(controller);
        var executor = ScriptedToolExecutor.WithWorkspace(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("search.web", PermissionDecision.Allow), fixture.Root);
        using var scope = ExecutionScope.Begin(new(new RunId(Guid.Parse(fixture.Run))));

        var outcome = executor.ExecuteToolWithoutJournal(Call("search.web"), false, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Summary);
        Assert.Equal(1, fixture.Provider.Calls);
        Assert.Equal(1, controller.State.QueriesUsed);
        Assert.Single(controller.State.Attempts);
    }

    private static ValidatedToolCall Call(string name) => new(ToolCallId.New(), new(name), "web-fixture",
        "{\"query\":\"release\",\"targetedGap\":\"q1\"}");

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-web-core-" + Guid.NewGuid().ToString("N"));
        public string Run { get; } = Guid.NewGuid().ToString("D");
        public string WorkspaceData => Path.Combine(Root, "workspace-data");
        public DefaultPlatformPaths Paths { get; }
        public FakeProvider Provider { get; } = new();
        public Fixture() { Directory.CreateDirectory(Root); Paths = new(Root); }
        public void Config(string json)
        {
            Directory.CreateDirectory(Paths.ConfigDirectory);
            File.WriteAllText(Path.Combine(Paths.ConfigDirectory, "web-research.json"), json);
        }
        public WebResearchController Controller() => new(new SqliteWebResearchStore(Path.Combine(WorkspaceData, "research.db")),
            Run, [new("q1", "When was the release?")], WebResearchLimits.Normal, new NoReader(), ["https://source.test"], Provider);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FakeProvider : IWebSearchProvider
    {
        public int Calls { get; private set; }
        public string Id => "fixture";
        public Uri Endpoint { get; } = new("https://search.test/search");
        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
        { Calls++; return System.Threading.Tasks.Task.FromResult(new WebSearchResponse([], [], [])); }
    }

    private sealed class NoReader : IWebReadTransport
    {
        public Task<WebReadResponse> ReadAsync(WebReadRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A reader should not run in a catalog fixture.");
    }
}
