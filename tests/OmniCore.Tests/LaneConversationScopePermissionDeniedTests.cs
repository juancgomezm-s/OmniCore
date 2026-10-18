using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class LaneConversationScopePermissionDeniedTests
{
    [Fact]
    public void Permission_denial_is_visible_to_owning_lane_but_not_a_sibling_lane()
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var rootTask = TaskId.New();
        var rootLane = LaneId.New();
        var childTask = TaskId.New();
        var childLane = LaneId.New();
        var rootTurn = TurnId.New();
        var childTurn = TurnId.New();
        var rootCall = ToolCallId.New();
        var childCall = ToolCallId.New();

        var events = new List<DomainEvent>
        {
            Envelope(codecs, new RunCreated(run, session, "fixture", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null), rootTask,
                DateTimeOffset.UtcNow), session, run, rootTask),
            Envelope(codecs, new LaneCreated(rootLane, rootTask, ProfileId.New()), session, run, rootTask, rootLane),
            Envelope(codecs, new TaskCreated(childTask, run, "child", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null), rootTask), session, run, childTask),
            Envelope(codecs, new LaneCreated(childLane, childTask, ProfileId.New()), session, run, childTask, childLane),
            Envelope(codecs, new TurnStarted(rootTurn, rootLane), session, run, rootTask, rootLane, rootTurn),
            Envelope(codecs, new TurnStarted(childTurn, childLane), session, run, childTask, childLane, childTurn),
            Envelope(codecs, new ToolCallRequested(rootCall, "provider-root", "filesystem.write", "{}"),
                session, run, rootTask, rootLane, rootTurn, rootCall),
            Envelope(codecs, new ToolCallRequested(childCall, "provider-child", "filesystem.write", "{}"),
                session, run, childTask, childLane, childTurn, childCall),
            // Denial envelopes intentionally carry only the Run and call identity. Ownership
            // must be recovered from that Run's ToolCallRequested event, not widened to siblings.
            Envelope(codecs, new PermissionDenied(rootCall, "Deny"), session, run),
            Envelope(codecs, new PermissionDenied(childCall, "Deny"), session, run),
        };

        var rootScope = new LaneConversationScope(events, codecs, run, rootLane);
        var childScope = new LaneConversationScope(events, codecs, run, childLane);
        var rootDenied = Assert.Single(events, evt => codecs.Decode(evt) is PermissionDenied denied
            && denied.ToolCallId == rootCall);
        var childDenied = Assert.Single(events, evt => codecs.Decode(evt) is PermissionDenied denied
            && denied.ToolCallId == childCall);

        Assert.True(rootScope.CanRead(rootDenied));
        Assert.False(rootScope.CanRead(childDenied));
        Assert.True(childScope.CanRead(childDenied));
        Assert.False(childScope.CanRead(rootDenied));
    }

    private static DomainEvent Envelope(IEventCodecRegistry codecs, DomainEventPayload payload, SessionId session, RunId? run = null,
        TaskId? task = null, LaneId? lane = null, TurnId? turn = null, ToolCallId? toolCall = null)
    {
        return DomainEvent.Create(session, payload.Type(), payload.SchemaVersion(), null, run, run, task, lane,
            turn, null, toolCall, Array.Empty<ArtifactRef>(), codecs.CodecFor(payload.Type()).Encode(payload));
    }
}
