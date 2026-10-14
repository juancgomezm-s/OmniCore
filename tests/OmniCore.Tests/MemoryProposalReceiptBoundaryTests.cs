namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

public sealed class MemoryProposalReceiptBoundaryTests
{
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
}
