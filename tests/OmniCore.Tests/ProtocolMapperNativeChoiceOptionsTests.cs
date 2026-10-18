using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ProtocolMapperNativeChoiceOptionsTests
{
    [Fact]
    public void Native_choices_add_canonical_option_objects_while_legacy_csv_and_questionnaire_projection_remain()
    {
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "Exercise native choices");
        var stream = new EventStream(store, codecs, session);
        var permissionId = InteractionId.New();
        var planApprovalId = InteractionId.New();
        var questionId = InteractionId.New();
        var schemaRef = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)),
            32, "application/json", ArtifactKind.Other, Sensitivity.Normal);

        stream.AppendBatch(new DomainEventPayload[]
        {
            new InteractionRequested(permissionId, InteractionKind.Permission, "{}",
                "[{\"id\":\"deny\"},{\"id\":\"allow_once\"}]", "deny", null,
                run.RootLane, null, null, 0, 1),
            new InteractionRequested(planApprovalId, InteractionKind.PlanApproval, "{}",
                "[{\"id\":\"approve_execute\"},{\"id\":\"approve_only\"},{\"id\":\"continue_planning\"},{\"id\":\"reject\"}]",
                "reject", null, run.RootLane, null, null, 0, 1),
            new InteractionRequested(questionId, InteractionKind.Question, "{}",
                "[{\"id\":\"legacy-question-option\"}]", "legacy-question-option", null,
                run.RootLane, null, null, 0, 1, schemaRef),
        }, DurabilityClass.Barrier);

        var mapped = new ProtocolMapper(codecs).Map(store.ReadFrom(session, 1));
        var permission = JsonObj.Parse(mapped.Single(item =>
            JsonObj.Parse(item.PayloadJson).GetValueOrDefault("interactionId") == permissionId.ToString()).PayloadJson);
        var planApproval = JsonObj.Parse(mapped.Single(item =>
            JsonObj.Parse(item.PayloadJson).GetValueOrDefault("interactionId") == planApprovalId.ToString()).PayloadJson);
        var questionnaire = JsonObj.Parse(mapped.Single(item =>
            JsonObj.Parse(item.PayloadJson).GetValueOrDefault("interactionId") == questionId.ToString()).PayloadJson);

        Assert.Equal("deny,allow_once", permission["options"]);
        Assert.Equal("[{\"id\":\"deny\"},{\"id\":\"allow_once\"}]", permission["optionsJson"]);
        Assert.Equal("approve_execute,approve_only,continue_planning,reject", planApproval["options"]);
        Assert.Equal("[{\"id\":\"approve_execute\"},{\"id\":\"approve_only\"},{\"id\":\"continue_planning\"},{\"id\":\"reject\"}]",
            planApproval["optionsJson"]);
        Assert.Equal("legacy-question-option", questionnaire["options"]);
        Assert.False(questionnaire.ContainsKey("optionsJson"));
        Assert.Equal(schemaRef.Hash.ToString(), questionnaire["questionnaireSchemaHash"]);
    }
}
