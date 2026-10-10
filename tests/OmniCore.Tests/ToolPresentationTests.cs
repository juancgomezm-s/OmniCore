using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>ADR-0033 §2: la presentación de una tool es dato declarativo, viaja por el wire y el cliente la interpola.</summary>
public sealed class ToolPresentationTests
{
    private static readonly EventCodecs Codecs = EventCodecs.Create();

    private static ClientState Reduce(IEnumerable<WireEnvelope> events, Localization text)
    {
        var projection = new ClientProjection(text);
        var state = ClientState.Empty();
        foreach (var evt in events) state = projection.Apply(state, evt);
        return state;
    }

    private static IReadOnlyList<WireEnvelope> Wire(params DomainEventPayload[] payloads)
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        TestRun.Open(store, session, "objetivo");
        new EventStream(store, Codecs, session).AppendBatch(payloads, DurabilityClass.Standard);
        return new ProtocolMapper(Codecs).Map(store.ReadFrom(session, 1)).ToArray();
    }

    [Fact]
    public void Every_core_presentation_has_complete_es_and_en_labels_and_a_category()
    {
        Assert.NotEmpty(ToolPresentation.CoreToolIds);
        foreach (var id in ToolPresentation.CoreToolIds)
        {
            var presentation = ToolPresentation.Of(id)!;
            foreach (var key in new[] { presentation.RunningKey, presentation.SucceededKey, presentation.FailedKey })
            {
                Assert.NotEqual(key, Localization.Spanish().Resolve(key, new Dictionary<string, string>()));
                Assert.NotEqual(key, Localization.English().Resolve(key, new Dictionary<string, string>()));
            }
        }
        Assert.Equal(ActivityCategory.Edit, ToolPresentation.Of("filesystem.patch")!.Category);
        Assert.Null(ToolPresentation.Of("vendor.unknown"));
    }

    [Fact]
    public void The_wire_carries_the_category_and_only_the_declared_summary_fields_redacted()
    {
        var call = ToolCallId.New();
        var wire = Wire(new ToolCallRequested(call, "pc", "filesystem.read",
            "{\"path\":\"src/auth.cs\",\"secret\":\"sk-must-not-travel-0123456789\"}"));

        var requested = JsonObj.Parse(wire.Single(w => JsonObj.Parse(w.PayloadJson)["type"] == "toolcall.requested").PayloadJson);

        Assert.Equal("Read", requested["category"]);
        Assert.Equal("src/auth.cs", requested["arg.path"]);
        Assert.DoesNotContain(requested.Keys, key => key.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain("sk-must-not-travel", string.Join(" ", requested.Values), StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_field_that_cannot_be_read_never_leaves_the_label_half_interpolated()
    {
        var wire = Wire(new ToolCallRequested(ToolCallId.New(), "pc", "filesystem.read", "{}"));

        var state = Reduce(wire, Localization.Spanish());

        var block = Assert.Single(state.Conversation.Blocks, b => b.Role == ConversationRole.Tool);
        Assert.Equal("Leyendo …", block.Text);
    }

    private static WireEnvelope Event(string type, params (string Name, string Value)[] fields) =>
        WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.Field("type", type)
            + string.Concat(fields.Select(f => "," + JsonObj.Field(f.Name, f.Value))) + "}");

    private static WireEnvelope Requested(string call, string tool, string category, string? path = null) =>
        path is null ? Event("toolcall.requested", ("toolCallId", call), ("tool", tool), ("category", category))
            : Event("toolcall.requested", ("toolCallId", call), ("tool", tool), ("category", category), ("arg.path", path));

    [Fact]
    public void An_activity_block_is_updated_in_place_from_running_to_succeeded_or_failed()
    {
        var running = Reduce(new[]
        {
            Requested("read-1", "filesystem.read", "Read", "src/auth.cs"),
            Requested("patch-1", "filesystem.patch", "Edit", "src/auth.cs"),
        }, Localization.Spanish());
        Assert.Equal(new[] { "Leyendo src/auth.cs", "Editando src/auth.cs" },
            running.Conversation.Blocks.Where(b => b.Role == ConversationRole.Tool).Select(b => b.Text));

        var finished = Reduce(new[]
        {
            Requested("read-1", "filesystem.read", "Read", "src/auth.cs"),
            Requested("patch-1", "filesystem.patch", "Edit", "src/auth.cs"),
            Event("toolcall.succeeded", ("toolCallId", "read-1")),
            Event("toolcall.rejected", ("toolCallId", "patch-1"), ("cause", "ruta de secretos")),
        }, Localization.Spanish());

        var tools = finished.Conversation.Blocks.Where(b => b.Role == ConversationRole.Tool).ToArray();
        Assert.Equal(2, tools.Length); // un solo bloque por ToolCall
        Assert.Equal("Leído src/auth.cs", tools[0].Text);
        Assert.Equal("No se pudo editar src/auth.cs: ruta de secretos", tools[1].Text);

        var english = Reduce(new[]
        {
            Requested("read-1", "filesystem.read", "Read", "src/auth.cs"),
            Event("toolcall.succeeded", ("toolCallId", "read-1")),
        }, Localization.English());
        Assert.Equal("Read src/auth.cs", Assert.Single(english.Conversation.Blocks, b => b.Role == ConversationRole.Tool).Text);
    }

    [Fact]
    public void A_tool_without_presentation_keeps_the_generic_label_and_a_separate_failure_block()
    {
        var state = Reduce(new[]
        {
            Event("toolcall.requested", ("toolCallId", "x-1"), ("tool", "vendor.unknown")),
            Event("toolcall.rejected", ("toolCallId", "x-1"), ("cause", "no permitido")),
        }, Localization.Spanish());

        var tools = state.Conversation.Blocks.Where(b => b.Role == ConversationRole.Tool).ToArray();
        Assert.Equal(2, tools.Length);
        Assert.Equal("Herramienta: vendor.unknown", tools[0].Text);
        Assert.Contains("no permitido", tools[1].Text, StringComparison.Ordinal);
    }
}
