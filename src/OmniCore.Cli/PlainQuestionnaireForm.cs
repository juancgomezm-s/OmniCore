using OmniCore.Client;
using OmniCore.Protocol;
using ClientQuestionnaireResponse = OmniCore.Client.QuestionnaireResponseDto;
using WireQuestionnaireResponse = OmniCore.Protocol.QuestionnaireResponseDto;
using WireQuestionAnswer = OmniCore.Protocol.QuestionAnswerDto;

namespace OmniCore.Cli;

/// <summary>Formulario secuencial plain renderizado desde el modelo declarativo de Client.</summary>
internal static class PlainQuestionnaireForm
{
    public static WireQuestionnaireResponse? Read(QuestionnairePromptDto prompt)
    {
        var english = Environment.GetEnvironmentVariable("OMNI_LOCALE") == "en";
        var model = new QuestionnaireOverlayModel(prompt.Title, prompt.Description,
            prompt.Questions.Select(question => new QuestionnaireQuestionModel(question.Id, question.Prompt,
                question.HelpText, Enum.Parse<QuestionnaireQuestionKind>(question.Kind),
                question.Options.Select(option => new QuestionnaireChoiceModel(option.Id, option.Label,
                    option.Description)).ToArray(),
                question.Other is null ? null : new QuestionnaireOtherModel(question.Other.OptionId,
                    question.Other.Label, question.Other.Placeholder, question.Other.TextRequired,
                    question.Other.MaxTextLength), question.Required, question.MinSelections,
                question.MaxSelections, question.MaxTextLength)).ToArray(), english ? "Submit" : "Enviar",
            english ? "Cancel" : "Cancelar");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Console.WriteLine(model.Title);
            if (!string.IsNullOrWhiteSpace(model.Description)) Console.WriteLine(model.Description);
            Console.WriteLine((english ? "Type :cancel to cancel. " : "Escribe :cancel para cancelar. ")
                + "[" + model.SubmitLabel + "/" + model.CancelLabel + "]");
            var inputs = new Dictionary<string, QuestionnairePlainFormInput>(StringComparer.Ordinal);
            foreach (var question in model.Questions)
            {
                Console.WriteLine();
                Console.WriteLine(question.Prompt + (question.Required
                    ? (english ? " (required)" : " (requerido)") : ""));
                if (!string.IsNullOrWhiteSpace(question.HelpText)) Console.WriteLine("  " + question.HelpText);
                if (question.Kind != QuestionnaireQuestionKind.FreeText)
                {
                    foreach (var choice in question.Choices)
                        Console.WriteLine("  " + choice.Label + " [" + choice.Id + "]"
                            + (choice.Description is null ? "" : " — " + choice.Description));
                    if (question.Other is not null) Console.WriteLine("  " + question.Other.Label
                        + " [" + question.Other.OptionId + "]");
                }

                Console.Write(question.Kind == QuestionnaireQuestionKind.FreeText
                    ? "> " : english ? "Choice ids (comma-separated): " : "Ids (separados por coma): ");
                var selectionOrText = Console.ReadLine();
                if (selectionOrText is null || selectionOrText.Trim().Equals(":cancel", StringComparison.OrdinalIgnoreCase))
                    return new WireQuestionnaireResponse(Array.Empty<WireQuestionAnswer>(), true);

                if (question.Kind == QuestionnaireQuestionKind.FreeText)
                {
                    inputs[question.Id] = new QuestionnairePlainFormInput(text: selectionOrText);
                    continue;
                }

                string? otherText = null;
                if (question.Other is not null && selectionOrText.Split(',', StringSplitOptions.TrimEntries)
                    .Contains(question.Other.OptionId, StringComparer.Ordinal))
                {
                    Console.Write(question.Other.Label + (question.Other.Placeholder is null
                        ? ": " : " (" + question.Other.Placeholder + "): "));
                    otherText = Console.ReadLine();
                    if (otherText is null || otherText.Trim().Equals(":cancel", StringComparison.OrdinalIgnoreCase))
                        return new WireQuestionnaireResponse(Array.Empty<WireQuestionAnswer>(), true);
                }
                inputs[question.Id] = new QuestionnairePlainFormInput(selection: selectionOrText, otherText: otherText);
            }

            QuestionnaireParseResult parsed = QuestionnairePlainFormParser.Parse(model, inputs);
            if (parsed.Response is { } response)
                return ToWire(response);
            foreach (var error in parsed.Errors) Console.WriteLine((english ? "Error: " : "Error: ") + error);
            Console.WriteLine(english ? "Correct the form and try again." : "Corrige el formulario y vuelve a intentarlo.");
        }
        return null;
    }

    private static WireQuestionnaireResponse ToWire(ClientQuestionnaireResponse response) =>
        new WireQuestionnaireResponse(response.Answers.Select(answer => new WireQuestionAnswer(answer.QuestionId,
            answer.SelectedOptionIds, answer.Text, answer.OtherText)).ToArray(), response.Cancelled);
}
