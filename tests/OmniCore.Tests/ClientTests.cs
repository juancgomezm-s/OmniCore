using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task Projection_turns_sim_event_into_system_block()
    {
        var projection = new ClientProjection();
        var state = ClientState.Empty();
        var envelope = WireEnvelope.Event(Ids.NewV7(),
            "{" + JsonObj.Field("type", "sim.events") + "," + JsonObj.Field("run", "Completed")
            + "," + JsonObj.Field("exitCode", "0") + "}");

        var next = projection.Apply(state, envelope);

        Assert.Single(next.Conversation.Blocks);
        var block = next.Conversation.Blocks[0];
        Assert.Equal(ConversationRole.System, block.Role);
        Assert.Contains("Completed", block.Text);
    }

    [Fact]
    public async Task Projection_ignores_non_event_messages()
    {
        var projection = new ClientProjection();
        var state = ClientState.Empty();
        var envelope = WireEnvelope.Hello();

        var next = projection.Apply(state, envelope);

        Assert.Empty(next.Conversation.Blocks);
    }

    [Fact]
    public async Task Projection_local_draft_updates_composer()
    {
        var projection = new ClientProjection();
        var state = ClientState.Empty();

        var next = projection.ApplyLocal(state, LocalAction.Draft("hola omni"));

        Assert.Equal("hola omni", next.Composer.Draft);
    }
}