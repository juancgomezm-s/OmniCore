using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ActiveMcpSidebarTests
{
    private sealed class Fixture
    {
        internal readonly InMemoryEventStore Store = new();
        internal readonly EventCodecs Codecs = EventCodecs.Create();
        internal readonly OmniServer Server;
        internal readonly SessionId Session;
        internal readonly RunId Run;
        internal readonly EventStream Stream;
        internal Fixture()
        {
            Server = new(Store, Codecs, new InMemoryAuditSink());
            Assert.Equal("ok", Server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"fixture\",\"mode\":\"act\"}"), default).Status);
            Session = Server.LastSessionId()!; Run = Server.LastRunId()!;
            Stream = new(Store, Codecs, Session);
        }
        internal ToolCallId Start(string server, RunId? run = null, string source = "Mcp", bool legacy = false, bool start = true)
        {
            var call = ToolCallId.New();
            using var scope = ExecutionScope.Begin(new(run ?? Run));
            Stream.Append(new ToolCallRequested(call, "fixture", "mcp." + server + ".lookup", "{}"));
            Stream.Append(new ToolCallPrepared(call, "{}"));
            Stream.Append(new ToolCallAuthorized(call));
            if (start) Stream.Append(new ToolCallStarted(call, EffectClass.None, null)
            { Attribution = new("mcp." + server + ".lookup", source, null, "1", null, legacy ? null : server) });
            return call;
        }
        internal SessionSidebarSnapshot Read(bool recovery = false) => SessionSidebarReader.Read(Store, Codecs, Session, Run, recovery);
        internal void End(ToolCallId call, string state)
        {
            using var scope = ExecutionScope.Begin(new(Run));
            DomainEventPayload terminal = state switch
            {
                "success" => new ToolCallSucceeded(call, "{}"),
                "failure" => new ToolCallFailed(call, "fixture", EffectOutcome.None),
                "cancel" => new ToolCallCancelled(call, "fixture"),
                "denied" => new PermissionDenied(call, "fixture"),
                "rejected" => new ToolCallRejected(call, "fixture"),
                _ => new ToolCallEffectUnknown(call, EffectClass.None)
            };
            Stream.Append(terminal);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("unknown")]
    public void Connector_is_active_only_between_a_runtime_start_and_terminal_outcome(string terminal)
    {
        var fx = new Fixture();
        var call = fx.Start("github.enterprise");
        Assert.Equal(new[] { "github.enterprise" }, fx.Read().ActiveMcpServers);
        var wire = SidebarJson.Decode(SidebarJson.Encode(fx.Read()))!;
        Assert.Equal(fx.Read().ActiveMcpServers, wire.ActiveMcpServers);
        fx.End(call, terminal);
        Assert.Empty(fx.Read().ActiveMcpServers!);
    }

    [Fact]
    public void Parallel_calls_are_deduplicated_and_other_runs_or_fabricated_prefixes_do_not_appear()
    {
        var fx = new Fixture();
        var first = fx.Start("github"); var second = fx.Start("github");
        fx.Start("search"); fx.Start("foreign", RunId.New()); fx.Start("fake", source: "BuiltIn");
        Assert.Equal(new[] { "github", "search" }, fx.Read().ActiveMcpServers);
        fx.End(first, "success");
        Assert.Equal(new[] { "github", "search" }, fx.Read().ActiveMcpServers);
        fx.End(second, "success");
        Assert.Equal(new[] { "search" }, fx.Read().ActiveMcpServers);
        Assert.Empty(fx.Read(recovery: true).ActiveMcpServers!);
        using (ExecutionScope.Begin(new(fx.Run))) fx.Stream.Append(new RunCancelled(fx.Run));
        Assert.Empty(fx.Read().ActiveMcpServers!);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("denied")]
    public void Calls_stopped_before_execution_never_show_a_connector(string terminal)
    {
        var fx = new Fixture();
        var call = fx.Start("github", start: false);
        Assert.Empty(fx.Read().ActiveMcpServers!);
        fx.End(call, terminal);
        Assert.Empty(fx.Read().ActiveMcpServers!);
    }

    [Fact]
    public void Legacy_attribution_without_connector_identity_does_not_guess_from_the_tool_name()
    {
        var fx = new Fixture();
        fx.Start("guessed", legacy: true);
        Assert.Empty(fx.Read().ActiveMcpServers!);
    }

    [Fact]
    public void Started_v4_journals_without_connector_identity_remain_readable()
    {
        var codecs = EventCodecs.Create();
        var started = new ToolCallStarted(ToolCallId.New(), EffectClass.None, null)
        { Attribution = new("mcp.github.lookup", "Mcp", null, "1", null, "github") };
        var codec = codecs.CodecFor(started.Type());
        Assert.Equal(5, codecs.CurrentVersion(started.Type()));
        Assert.Equal("github", ((ToolCallStarted)codec.Decode(started.Type(), codec.Encode(started))).Attribution!.McpServerId);
        var old = System.Text.Json.Nodes.JsonNode.Parse(codec.Encode(started))!.AsObject();
        old["Attribution"]!.AsObject().Remove("McpServerId");
        var run = RunId.New();
        var envelope = DomainEvent.Create(SessionId.New(), started.Type(), 4, null, run, run,
            null, null, null, null, null, [], old.ToJsonString());
        Assert.Null(((ToolCallStarted)codecs.Decode(envelope)).Attribution!.McpServerId);
    }

    [Theory]
    [InlineData("es", "MCP: github")]
    [InlineData("en", "MCP: github")]
    [InlineData("fr", "MCP : github")]
    [InlineData("it", "MCP: github")]
    public void Connectors_are_directly_below_the_session_mode(string locale, string expected)
    {
        var fx = new Fixture(); fx.Start("github"); fx.Start("search");
        var model = Assert.IsType<ListWidgetModel>(SessionSidebarPresentation.Build(fx.Read(), null,
            "model", "act", locale, false).Single(widget => widget.Id == "core.session").Build(ClientState.Empty(), WidgetSize.Compact));
        Assert.Equal(expected, model.Rows[3].Text);
        Assert.Equal("  search", model.Rows[4].Text);
    }
}
