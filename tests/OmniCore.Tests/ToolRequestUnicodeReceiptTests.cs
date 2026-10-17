using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class ToolRequestUnicodeReceiptTests
{
    [Theory]
    [InlineData("{ \"path\": \"continuidad.txt\", \"content\": \"línea conservada\\nestado: pendiente\\n\" }")]
    [InlineData("{\n  \"path\": \"documento.txt\",\n  \"content\": \"<div>Español & Français</div>\"\n}")]
    [InlineData("{\"path\":\"archivo.txt\",\"content\":\"{ \\\"texto\\\": \\\"áéíóú\\\" }\"}")]
    public void Harmless_argument_representation_is_preserved_by_the_durable_receipt(string arguments)
    {
        var session = SessionId.New();
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var stream = new EventStream(store, codecs, session);
        var id = ToolCallId.New();
        var request = new ToolCallRequested(id, "provider-call", "filesystem.write", arguments);
        var receipt = stream.AppendToolCallRequestAdmitted(request, _ => { });
        var persisted = Assert.IsType<ToolCallRequested>(codecs.Decode(Assert.Single(store.ReadFrom(session, 1))));
        Assert.Equal(arguments, persisted.ArgumentsJson);
        Assert.Equal(request, persisted);
        Assert.True(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("filesystem.write"), "provider-call", arguments)));
        Assert.False(receipt.MatchesCall(new ValidatedToolCall(id, new ToolId("filesystem.write"), "provider-call", "{}")));
    }

    [Fact]
    public void Actual_sensitive_argument_redaction_still_prevents_minting_an_executable_receipt()
    {
        var session = SessionId.New();
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var stream = new EventStream(store, codecs, session);
        var request = new ToolCallRequested(ToolCallId.New(), "provider-call", "filesystem.write",
            "{\"path\":\"contact.txt\",\"content\":\"Bearer fixture-sensitive-token-123456\"}");
        Assert.Throws<InvalidDataException>(() => stream.AppendToolCallRequestAdmitted(request, _ => { }));
        var persisted = Assert.IsType<ToolCallRequested>(codecs.Decode(Assert.Single(store.ReadFrom(session, 1))));
        Assert.DoesNotContain("fixture-sensitive-token-123456", persisted.ArgumentsJson);
    }
}
