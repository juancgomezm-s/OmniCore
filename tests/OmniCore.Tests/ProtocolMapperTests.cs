using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>
/// EPIC-010: el Host traduce los eventos de dominio a eventos del protocolo (ProtocolMapper) y el
/// cliente los reduce a conversación, overlays y status line con textos localizados, sin conocer
/// los tipos del Engine.
/// </summary>
public sealed class ProtocolMapperTests
{
    private static readonly EventCodecs Codecs = EventCodecs.Create();

    private static (InMemoryEventStore Store, SessionId Session, TestRun.Opened Run, EventStream Stream) Journal()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "Arregla el \"login\"");
        return (store, session, run, new EventStream(store, Codecs, session));
    }

    private static ClientState Reduce(IEnumerable<WireEnvelope> events, Localization? text = null)
    {
        var projection = new ClientProjection(text ?? Localization.Spanish());
        var state = ClientState.Empty();
        foreach (var evt in events)
        {
            state = projection.Apply(state, evt);
        }

        return state;
    }

    [Fact]
    public void Mapped_events_carry_type_sequence_session_and_run_but_not_engine_internals()
    {
        var (store, session, run, _) = Journal();

        var wire = new ProtocolMapper(Codecs).Map(store.ReadFrom(session, 1));

        var created = JsonObj.Parse(wire.First(w => JsonObj.Parse(w.PayloadJson)["type"] == "run.created").PayloadJson);
        Assert.Equal("1", created["seq"]);
        Assert.Equal(session.ToString(), created["sessionId"]);
        Assert.Equal(run.RunId.ToString(), created["runId"]);
        Assert.Equal("Arregla el \"login\"", created["objective"]);
        Assert.Equal("act", created["mode"]);
        // Lo que no tiene entrada en la tabla (p. ej. task.created) no se expone.
        Assert.DoesNotContain(wire, w => JsonObj.Parse(w.PayloadJson)["type"] == "task.created");
    }

    [Fact]
    public void The_client_renders_the_real_conversation_from_the_journal()
    {
        var (store, session, run, stream) = Journal();
        stream.Append(new UserInputReceived(run.RunId, "[\"revisa auth.cs\"]", null));
        var call = ToolCallId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new ToolCallRequested(call, "pc", "filesystem.read", "{}"),
            new ToolCallRejected(call, "ruta de secretos"),
        }, DurabilityClass.Standard);

        var state = Reduce(new ProtocolMapper(Codecs).Map(store.ReadFrom(session, 1)));

        var blocks = state.Conversation.Blocks;
        Assert.Contains(blocks, b => b.Role == ConversationRole.System && b.Text.Contains("Arregla el \"login\""));
        Assert.Contains(blocks, b => b.Role == ConversationRole.User && b.Text == "revisa auth.cs");
        Assert.Contains(blocks, b => b.Role == ConversationRole.Tool && b.ToolName == "filesystem.read");
        Assert.Contains(blocks, b => b.Role == ConversationRole.Tool && b.Text.Contains("ruta de secretos"));
    }

    [Fact]
    public void Concurrent_child_conversation_is_lane_attributed_and_not_merged_into_the_root()
    {
        var (store, session, run, stream) = Journal();
        var childTask = TaskId.New();
        var childLane = LaneId.New();
        var childExecution = ExecutionId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new TaskCreated(childTask, run.RunId, "child task", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null), run.RootTask),
            new TaskReady(childTask),
            new LaneCreated(childLane, childTask, ProfileId.New()),
            new LaneStarted(childLane),
            new TaskStarted(childTask, childLane),
            new UserInputReceived(run.RunId, "[\"root prompt\"]", null),
            new UserInputReceived(run.RunId, "[\"private child prompt\"]", null),
            new UserInputReceived(run.RunId, "[\"unattributed legacy prompt\"]", null),
            new InteractionRequested(InteractionId.New(), InteractionKind.Permission, "{}", "[\"allow\"]",
                "allow", null, childLane, childTask, null, 0, 1),
        }, DurabilityClass.Barrier, new ExecutionScopeState?[] {
            new(run.RunId), new(run.RunId), new(run.RunId, childTask, childLane, ExecutionId: childExecution),
            new(run.RunId, childTask, childLane, ExecutionId: childExecution),
            new(run.RunId, childTask, childLane, ExecutionId: childExecution),
            new(run.RunId, run.RootTask, run.RootLane),
            new(run.RunId, childTask, childLane, ExecutionId: childExecution),
            null,
            new(run.RunId, childTask, childLane, ExecutionId: childExecution),
        });
        var journal = store.ReadFrom(session, 1);
        var mapped = new ProtocolMapper(Codecs).Map(journal, journal);
        var rootInput = mapped.Single(item => JsonObj.Parse(item.PayloadJson).GetValueOrDefault("text") == "root prompt");
        var childInput = mapped.Single(item => JsonObj.Parse(item.PayloadJson).GetValueOrDefault("text") == "private child prompt");
        var rootFields = JsonObj.Parse(rootInput.PayloadJson);
        var childFields = JsonObj.Parse(childInput.PayloadJson);
        Assert.Equal("primary", rootFields["conversationScope"]);
        Assert.Equal("child", childFields["conversationScope"]);
        Assert.Equal(childTask.ToString(), childFields["taskId"]);
        Assert.Equal(childLane.ToString(), childFields["laneId"]);
        Assert.Equal(childExecution.ToString(), childFields["executionId"]);

        var state = Reduce(mapped);
        Assert.Contains(state.Conversation.Blocks, block => block.Text == "root prompt");
        Assert.DoesNotContain(state.Conversation.Blocks, block => block.Text == "private child prompt");
        Assert.DoesNotContain(state.Conversation.Blocks, block => block.Text == "unattributed legacy prompt");
        var overlay = Assert.Single(state.Overlays);
        Assert.Equal(run.RunId.ToString(), overlay.RunId);
        Assert.Equal(childTask.ToString(), overlay.TaskId);
        Assert.Equal(childLane.ToString(), overlay.LaneId);
        Assert.Equal(childExecution.ToString(), overlay.ExecutionId);
        Assert.All(mapped.Select(item => JsonObj.Parse(item.PayloadJson)), fields =>
            Assert.Equal(fields.Keys.Count, fields.Keys.Distinct(StringComparer.Ordinal).Count()));
    }

    [Fact]
    public void Failure_events_carry_their_typed_error_code_to_the_wire()
    {
        var (store, session, run, stream) = Journal();
        var failed = ToolCallId.New();
        var rejected = ToolCallId.New();
        var legacy = ToolCallId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new ToolCallRequested(failed, "pc", "filesystem.patch", "{}"),
            new ToolCallPrepared(failed, "{}"),
            new PermissionEvaluated(failed, PermissionDecision.Allow, "[]", null),
            new ToolCallAuthorized(failed),
            new ToolCallStarted(failed, EffectClass.Reconcilable, null),
            new ToolCallFailed(failed, "STALE_WRITE: versión obsoleta", EffectOutcome.Applied,
                ToolErrorCode.StaleWrite),
            new ToolCallRequested(rejected, "pc", "nope.tool", "{}"),
            new ToolCallRejected(rejected, "tool no encontrada", ToolErrorCode.UnknownTool),
            // Evento v1 (sin campo): el wire preserva la ausencia con "", no inventa un código.
            new ToolCallRequested(legacy, "pc", "legacy.tool", "{}"),
            new ToolCallRejected(legacy, "rechazo de un journal v1"),
        }, DurabilityClass.Standard);

        var wire = new ProtocolMapper(Codecs).Map(store.ReadFrom(session, 1));

        var failedWire = JsonObj.Parse(wire.Single(w =>
        {
            var f = JsonObj.Parse(w.PayloadJson);
            return f["type"] == "toolcall.failed" && f["toolCallId"] == failed.ToString();
        }).PayloadJson);
        Assert.Equal("STALE_WRITE", failedWire["errorCode"]);
        Assert.Equal("Applied", failedWire["effect"]);
        var rejectedWire = JsonObj.Parse(wire.Single(w =>
        {
            var f = JsonObj.Parse(w.PayloadJson);
            return f["type"] == "toolcall.rejected" && f["toolCallId"] == rejected.ToString();
        }).PayloadJson);
        Assert.Equal("UNKNOWN_TOOL", rejectedWire["errorCode"]);
        var legacyWire = JsonObj.Parse(wire.Single(w =>
        {
            var f = JsonObj.Parse(w.PayloadJson);
            return f["type"] == "toolcall.rejected" && f["toolCallId"] == legacy.ToString();
        }).PayloadJson);
        Assert.Equal("", legacyWire["errorCode"]);
    }

    [Fact]
    public void A_pending_interaction_is_an_overlay_until_it_is_resolved()
    {
        var (store, session, run, stream) = Journal();
        var interaction = InteractionId.New();
        stream.Append(new InteractionRequested(interaction, InteractionKind.PlanApproval, "{}",
            "[{\"id\":\"approve_execute\"},{\"id\":\"approve_only\"},{\"id\":\"reject\"}]", "reject", null,
            run.RootLane, null, null, 0, 1));
        var mapper = new ProtocolMapper(Codecs);

        var pending = Reduce(mapper.Map(store.ReadFrom(session, 1)));
        var overlay = Assert.Single(pending.Overlays);
        Assert.Equal(interaction.ToString(), overlay.Id);
        Assert.Equal("Aprobar el plan", overlay.Title);
        Assert.Equal(new[] { "Aprobar y ejecutar", "Aprobar sin ejecutar", "Seguir planificando" }, overlay.Options);
        Assert.Equal(new[] { "approve_execute", "approve_only", "reject" }, overlay.OptionIds);
        Assert.Equal(1, pending.StatusLine.PendingInteractions);

        stream.Append(new InteractionResolved(interaction, "approve_execute", InteractionCause.User));
        stream.Append(new RunModeChanged(run.RunId, RunMode.Plan, RunMode.Act, "PlanApproved"));
        var resolved = Reduce(mapper.Map(store.ReadFrom(session, 1)));
        Assert.Empty(resolved.Overlays);
        Assert.Equal(0, resolved.StatusLine.PendingInteractions);
        Assert.Equal("act", resolved.StatusLine.Mode);
    }

    [Fact]
    public void Texts_follow_the_client_locale()
    {
        var (store, session, _, _) = Journal();
        var wire = new ProtocolMapper(Codecs).Map(store.ReadFrom(session, 1));

        var spanish = Reduce(wire, Localization.Spanish());
        var english = Reduce(wire, Localization.English());

        Assert.Contains(spanish.Conversation.Blocks, b => b.Text.StartsWith("Run iniciado", StringComparison.Ordinal));
        Assert.Contains(english.Conversation.Blocks, b => b.Text.StartsWith("Run started", StringComparison.Ordinal));
    }

    [Fact]
    public void Secrets_in_user_text_are_redacted_before_reaching_the_wire()
    {
        var (store, session, run, stream) = Journal();
        stream.Append(new UserInputReceived(run.RunId, "[\"mi clave es sk-ant-api03-abcdefghijklmnop\"]", null));

        var wire = new ProtocolMapper(Codecs).Map(store.ReadFrom(session, 1));

        Assert.DoesNotContain(wire, w => w.PayloadJson.Contains("sk-ant-api03-abcdefghijklmnop", StringComparison.Ordinal));
    }

    [Fact]
    public void The_server_subscription_returns_the_mapped_journal_of_the_current_session()
    {
        var server = OmniHost.CreateInMemoryServer();
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(),
            "{" + JsonObj.Field("cmd", "sim") + "," + JsonObj.Field("scenario", "multi-item-plan") + "}"),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", ack.Status);

        var all = server.SubscribeSince(0);
        var types = all.Select(w => JsonObj.Parse(w.PayloadJson)["type"]).ToArray();
        Assert.Contains("run.created", types);
        Assert.Contains("toolcall.requested", types);
        Assert.Contains("run.completed", types);
        Assert.Equal("sim.events", types[^1]); // la notificación del servidor va al final

        // Desde una secuencia posterior solo llega lo nuevo (más las notificaciones).
        var seqs = all.Select(w => JsonObj.Parse(w.PayloadJson)).Where(f => f.ContainsKey("seq"))
            .Select(f => long.Parse(f["seq"], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var later = server.SubscribeSince(seqs.Max());
        Assert.Single(later, w => JsonObj.Parse(w.PayloadJson).ContainsKey("seq"));

        var state = Reduce(all);
        Assert.Contains(state.Conversation.Blocks, b => b.Text == "Run completado");
    }
}
