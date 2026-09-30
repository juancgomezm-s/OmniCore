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

    [Fact]
    public void Questionnaire_parser_uses_published_choice_ids_and_builds_typed_answers()
    {
        var questionnaire = new QuestionnaireOverlayModel("Preferencias", null,
            new QuestionnaireQuestionModel[]
            {
                new("approach", "Enfoque", null, QuestionnaireQuestionKind.SingleChoice,
                    new QuestionnaireChoiceModel[] { new("compat", "Compatibilidad") },
                    new QuestionnaireOtherModel("other", "Otro", null, true, 40), true),
                new("checks", "Validaciones", null, QuestionnaireQuestionKind.MultipleChoice,
                    new QuestionnaireChoiceModel[] { new("unit", "Unitarias"), new("lint", "Lint") },
                    null, false),
                new("notes", "Notas", null, QuestionnaireQuestionKind.FreeText,
                    Array.Empty<QuestionnaireChoiceModel>(), null, false, maxTextLength: 100),
            }, "Enviar", "Cancelar");
        var inputs = new Dictionary<string, QuestionnairePlainFormInput>
        {
            ["approach"] = new("other", otherText: "Mi enfoque"),
            ["checks"] = new("unit lint"),
            ["notes"] = new(text: "Conservar el comportamiento existente"),
        };

        var result = QuestionnairePlainFormParser.Parse(questionnaire, inputs);

        Assert.True(result.IsValid);
        var response = result.Response ?? throw new InvalidOperationException("Expected parsed questionnaire response.");
        Assert.False(response.Cancelled);
        Assert.Equal(3, response.Answers.Count);
        Assert.Equal(new[] { "other" }, response.Answers[0].SelectedOptionIds);
        Assert.Equal("Mi enfoque", response.Answers[0].OtherText);
        Assert.Equal(new[] { "unit", "lint" }, response.Answers[1].SelectedOptionIds);
        Assert.Equal("Conservar el comportamiento existente", response.Answers[2].Text);
    }

    [Fact]
    public void Questionnaire_parser_rejects_unknown_and_duplicate_choices()
    {
        var questionnaire = new QuestionnaireOverlayModel("Q", null,
            new QuestionnaireQuestionModel[]
            {
                new("q", "Pregunta", null, QuestionnaireQuestionKind.MultipleChoice,
                    new QuestionnaireChoiceModel[] { new("yes", "Sí") }, null, false),
            }, "Enviar", "Cancelar");

        var result = QuestionnairePlainFormParser.Parse(questionnaire,
            new Dictionary<string, QuestionnairePlainFormInput>
            {
                ["q"] = new("yes,yes,No"),
            });

        Assert.False(result.IsValid);
        Assert.Null(result.Response);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void Questionnaire_parser_returns_structured_cancellation_without_answers()
    {
        var questionnaire = new QuestionnaireOverlayModel("Q", null,
            new QuestionnaireQuestionModel[]
            {
                new("required", "Obligatoria", null, QuestionnaireQuestionKind.FreeText,
                    Array.Empty<QuestionnaireChoiceModel>(), null, true),
            }, "Enviar", "Cancelar");

        var result = QuestionnairePlainFormParser.Parse(questionnaire,
            new Dictionary<string, QuestionnairePlainFormInput>(), cancelled: true);

        Assert.True(result.IsValid);
        Assert.True(result.Response!.Cancelled);
        Assert.Empty(result.Response.Answers);
    }
}