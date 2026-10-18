namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

public sealed class MemoryProposalReceiptBoundaryTests
{
    [Fact]
    public void Nested_json_file_content_keeps_its_exact_format_when_redaction_changes_nothing()
    {
        const string content = "{\n  \"mode\": \"PatchAndCreate\",\n  \"note\": \"literal > is preserved\"\n}";
        const string arguments = "{\"path\":\"src/FeatureApp/FeatureModule.json\",\"content\":\"{\\n  \\\"mode\\\": \\\"PatchAndCreate\\\",\\n  \\\"note\\\": \\\"literal > is preserved\\\"\\n}\"}";
        var session = SessionId.New();
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var stream = new EventStream(store, codecs, session);
        var request = new ToolCallRequested(ToolCallId.New(), "provider-call", "filesystem.write", arguments);

        _ = stream.AppendToolCallRequestAdmitted(request, _ => { });

        var persisted = Assert.IsType<ToolCallRequested>(codecs.Decode(store.ReadFrom(session, 1).Single()));
        using var document = System.Text.Json.JsonDocument.Parse(persisted.ArgumentsJson);
        Assert.Equal(content, document.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public void Request_receipt_accepts_equivalent_json_escaping_but_rejects_changed_values()
    {
        var session = SessionId.New();
        var store = new InMemoryEventStore();
        var codecs = new RewritingToolCallEventCodecs(EventCodecs.Create(), request => new ToolCallRequested(
            request.ToolCallId, request.ProviderCallId, request.ToolName,
            request.ArgumentsJson.Replace(">", "\\u003E", StringComparison.Ordinal)));
        var stream = new EventStream(store, codecs, session);
        var id = ToolCallId.New();
        const string arguments = "{\"path\":\"src/FeatureApp/FeatureModule.cs\",\"content\":\"public static class FeatureModule { public static bool IsInitialized { get; private set; } public static void Initialize() => IsInitialized = true; }\\n\"}";
        var request = new ToolCallRequested(id, "provider-call", "filesystem.write", arguments);

        var receipt = stream.AppendToolCallRequestAdmitted(request, _ => { });

        Assert.True(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("filesystem.write"),
            "provider-call", arguments)));
        Assert.False(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("filesystem.write"),
            "provider-call", arguments.Replace("FeatureModule", "OtherModule", StringComparison.Ordinal))));
        Assert.False(receipt.MatchesCall(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.write"),
            "provider-call", arguments)));
        Assert.False(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("filesystem.patch"),
            "provider-call", arguments)));
        Assert.False(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("filesystem.write"),
            "different-provider-call", arguments)));
        Assert.False(receipt.MatchesScope(RunId.New(), null, null, null, null));

        var changedSession = SessionId.New();
        var changedStore = new InMemoryEventStore();
        var changedCodecs = new RewritingToolCallEventCodecs(EventCodecs.Create(), request => new ToolCallRequested(
            request.ToolCallId, request.ProviderCallId, request.ToolName,
            request.ArgumentsJson.Replace("FeatureModule", "OtherModule", StringComparison.Ordinal)));
        var changedStream = new EventStream(changedStore, changedCodecs, changedSession);
        Assert.Throws<InvalidDataException>(() => changedStream.AppendToolCallRequestAdmitted(
            new ToolCallRequested(id, "provider-call", "filesystem.write", arguments), _ => { }));

        AssertRejectedRewrite(arguments, value =>
        {
            var separator = value.IndexOf(",\"content\":", StringComparison.Ordinal);
            return "{" + value[(separator + 1)..^1] + "," + value[1..separator] + "}";
        });
        AssertRejectedRewrite("{\"count\":1}",
            value => value.Replace(":1}", ":1.0}", StringComparison.Ordinal));

        var redactedSession = SessionId.New();
        var redactedStore = new InMemoryEventStore();
        var redactedStream = new EventStream(redactedStore, EventCodecs.Create(), redactedSession);
        var redactedArguments = "{\"path\":\"src/FeatureApp/FeatureModule.cs\",\"content\":\"Bearer fixture-secret-value\"}";
        Assert.Throws<InvalidDataException>(() => redactedStream.AppendToolCallRequestAdmitted(
            new ToolCallRequested(ToolCallId.New(), "provider-call", "filesystem.write", redactedArguments), _ => { }));
        Assert.DoesNotContain("fixture-secret-value", redactedStore.ReadFrom(redactedSession, 1).Single().PayloadJson,
            StringComparison.Ordinal);

        void AssertRejectedRewrite(string originalArguments, Func<string, string> rewrite)
        {
            var rejectedSession = SessionId.New();
            var rejectedStore = new InMemoryEventStore();
            var codecs = new RewritingToolCallEventCodecs(EventCodecs.Create(), request => new ToolCallRequested(
                request.ToolCallId, request.ProviderCallId, request.ToolName,
                rewrite(request.ArgumentsJson)));
            var rejectedStream = new EventStream(rejectedStore, codecs, rejectedSession);
            Assert.Throws<InvalidDataException>(() => rejectedStream.AppendToolCallRequestAdmitted(
                new ToolCallRequested(ToolCallId.New(), "provider-call", "filesystem.write", originalArguments),
                _ => { }));
        }
    }

    [Fact]
    public void Matching_arguments_exception_is_restricted_to_the_screened_memory_proposal_marker()
    {
        var session = SessionId.New();
        var store = new InMemoryEventStore();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var id = ToolCallId.New();
        var rawArguments = "{\"content\":\"private proposal\",\"kind\":\"Fact\"}";
        var ordinary = new ToolCallRequested(id, "provider-call", "process.exec", "{}");

        Assert.Throws<ArgumentException>(() => stream.AppendToolCallRequestAdmitted(ordinary, _ => { },
            "{\"forged\":true}"));
        Assert.Empty(store.ReadFrom(session, 1));

        var memoryRequest = new ToolCallRequested(id, "provider-call", "memory.propose",
            "{\"screenedByMemoryPolicy\":true}");
        var receipt = stream.AppendToolCallRequestAdmitted(memoryRequest, _ => { }, rawArguments);
        Assert.True(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("memory.propose"),
            "provider-call", rawArguments)));
        Assert.False(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("memory.propose"),
            "provider-call", "{\"content\":\"different\",\"kind\":\"Fact\"}")));

        var normal = new ToolCallRequested(ToolCallId.New(), "ordinary-call", "process.exec", "{}");
        var normalReceipt = stream.AppendToolCallRequestAdmitted(normal, _ => { });
        Assert.False(normalReceipt.MatchesCall(new ValidatedToolCall(normal.ToolCallId,
            new ToolId("process.exec"), "ordinary-call", "{\"argv\":[]}")));
    }

    private sealed class RewritingToolCallEventCodecs(IEventCodecRegistry inner,
        Func<ToolCallRequested, ToolCallRequested> rewrite) : IEventCodecRegistry
    {
        public IDomainEventCodec CodecFor(EventType type) => inner.CodecFor(type);

        public int CurrentVersion(EventType type) => inner.CurrentVersion(type);

        public DomainEventPayload Decode(DomainEvent evt)
        {
            var payload = inner.Decode(evt);
            return payload is ToolCallRequested request ? rewrite(request) : payload;
        }
    }
}
