using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ExplorerPlanApprovalTests
{
    [Fact]
    public void Plan_proposal_creates_durable_approval_and_no_client_answer_is_invented()
    {
        var setup = PreparePlanApproval();
        try
        {
            var events = setup.Server.AcquireStore().ReadFrom(setup.Session, 1);
            var interaction = events.Select(evt => setup.Server.AcquireCodecs().Decode(evt))
                .OfType<InteractionRequested>().Single(item => item.Kind == InteractionKind.PlanApproval);
            Assert.Contains("approve_execute", interaction.OptionsJson);
            Assert.Contains("approve_only", interaction.OptionsJson);
            Assert.Contains("reject", interaction.OptionsJson);
            Assert.Equal("reject", interaction.DefaultOptionId);
            Assert.DoesNotContain(events, evt => evt.Type.ToString() == "interaction.resolved");
            Assert.Equal(RunState.AwaitingInput, RunProjection.Replay(setup.Session, setup.Run,
                setup.Server.AcquireCodecs(), events).State);
            var requestEvent = Assert.Single(events, evt => setup.Server.AcquireCodecs().Decode(evt) is InteractionRequested request
                && request.Kind == InteractionKind.PlanApproval);
            Assert.Contains(events, evt => evt.Sequence > requestEvent.Sequence
                && setup.Server.AcquireCodecs().Decode(evt) is RunAwaitingInput awaiting
                && awaiting.RunId.Equals(setup.Run));
        }
        finally { Delete(setup.ArtifactsPath); }
    }

    [Fact]
    public void Approve_execute_uses_the_existing_effect_and_switches_the_same_run_to_act()
    {
        var setup = PreparePlanApproval();
        try
        {
            Assert.Equal("ok", setup.Server.RespondToInteraction(setup.Interaction, "approve_execute").Status);
            var events = setup.Server.AcquireStore().ReadFrom(setup.Session, 1);
            var projection = RunProjection.Replay(setup.Session, setup.Run, setup.Server.AcquireCodecs(), events);
            Assert.Equal(RunMode.Act, projection.Mode);
            Assert.Equal(setup.Run, projection.Id);
            Assert.Contains(events, evt => setup.Server.AcquireCodecs().Decode(evt) is RunModeChanged change
                && change.RunId.Equals(setup.Run) && change.Cause == "PlanApproved");

            var effective = EffectiveModelPolicy.Resolve(ModelPolicyKey.For("plan-approval", "plan-approval"), null,
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 16, GuidanceLevel.Full, 3,
                    PlanControl.RuntimeDriven, 8));
            var boundary = new ModelCapabilityBoundary(effective);
            var actTools = OmniHost.CreateActTools();
            var executor = OmniHost.CreateActExecutor(actTools.Catalog(), Path.GetFullPath("."), boundary,
                null, setup.Run);
            var actTurn = new ExplorerTurn((request, token) => new ModelResponse(
                    new ContentBlock[] { new TextBlock("ACT continued in the same run") }, StopReason.EndTurn,
                    new TokenUsage(5, 7, 0, 0, 0), null, new ProviderMetadata("scripted", "", null)),
                executor, actTools.Catalog(),
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("plan-approval", "h", "t", "c", "o", "M2"),
                new ModelSelection(new ModelIdValue("plan-approval"), 4096, ToolMode.Direct, null),
                setup.Server.AcquireStore(), setup.Server.AcquireCodecs(), new FileArtifactStore(setup.ArtifactsPath),
                new InMemoryAuditSink(), new RedactionPolicy(), boundary: boundary);
            var continued = actTurn.Ask("Execute the approved plan", "ACT", setup.Session, setup.Run,
                setup.Server.LastLaneId()!, "runtime plan", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, continued.StopReason);
            Assert.Contains("same run", continued.FinalText!);
            // Cada Turn nuevo abre su contabilidad de mutaciones (ADR-0044 §5).
            Assert.Equal(1, boundary.ReadRegistry().Ledger.TurnNumber);
            var continuedEvents = setup.Server.AcquireStore().ReadFrom(setup.Session, 1);
            Assert.Contains(continuedEvents, evt => evt.RunId?.Equals(setup.Run) == true
                && evt.Type.ToString() == "turn.started");
        }
        finally { Delete(setup.ArtifactsPath); }
    }

    [Fact]
    public void Approve_only_records_planned_outcome_and_reject_records_the_selected_option()
    {
        var approved = PreparePlanApproval();
        try
        {
            var approval = approved.Server.RespondToInteraction(approved.Interaction, "approve_only");
            Assert.True(approval.Status == "ok", approval.Error);
            var events = approved.Server.AcquireStore().ReadFrom(approved.Session, 1);
            Assert.Contains(events, evt => approved.Server.AcquireCodecs().Decode(evt) is RunCompleted completed
                && completed.RunId.Equals(approved.Run) && completed.Outcome == RunOutcome.Planned);
        }
        finally { Delete(approved.ArtifactsPath); }

        var rejected = PreparePlanApproval();
        try
        {
            Assert.Equal("ok", rejected.Server.RespondToInteraction(rejected.Interaction, "reject").Status);
            var events = rejected.Server.AcquireStore().ReadFrom(rejected.Session, 1);
            Assert.Contains(events, evt => rejected.Server.AcquireCodecs().Decode(evt) is InteractionResolved resolved
                && resolved.InteractionId.Equals(rejected.Interaction) && resolved.OptionId == "reject");
        }
        finally { Delete(rejected.ArtifactsPath); }
    }

    private static (OmniServer Server, InteractionId Interaction, SessionId Session, RunId Run,
        string ArtifactsPath) PreparePlanApproval()
    {
        var server = OmniHost.CreateInMemoryServer();
        var objective = "explain repository";
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{"
            + JsonObj.Field("cmd", "explore.start") + ","
            + JsonObj.Field("objective", objective) + "}"), CancellationToken.None).Status);
        var tools = OmniHost.CreateExplorerTools();
        var executor = ScriptedToolExecutor.WithCoreTools(tools.Catalog(),
            ScriptedPermissionPolicy.WithTool("plan.propose", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Plan));
        var artifactsPath = Path.Combine(Path.GetTempPath(), "omnicore-plan-approval-" + Guid.NewGuid().ToString("N"));
        var turn = new ExplorerTurn((request, token) => FakeResponses.PlanThenEnd(request), executor,
            tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("plan-approval", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("plan-approval"), 4096, ToolMode.Direct, null),
            server.AcquireStore(), server.AcquireCodecs(), new FileArtifactStore(artifactsPath),
            new InMemoryAuditSink(), new RedactionPolicy());
        turn.Ask(objective, "Explain. {context}", server.LastSessionId()!, server.LastRunId()!,
            server.LastLaneId()!, "runtime plan", CancellationToken.None);
        var interaction = server.RequestPlanApprovalIfNeeded();
        Assert.NotNull(interaction);
        return (server, interaction!, server.LastSessionId()!, server.LastRunId()!, artifactsPath);
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
    }
}
