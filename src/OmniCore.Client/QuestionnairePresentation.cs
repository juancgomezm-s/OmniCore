namespace OmniCore.Client;

/// <summary>Question type exposed to renderers without a dependency on OmniCore.Domain.</summary>
public enum QuestionnaireQuestionKind
{
    SingleChoice,
    MultipleChoice,
    FreeText,
}

/// <summary>A server-published selectable choice.</summary>
public sealed class QuestionnaireChoiceModel
{
    public string Id { get; }

    public string Label { get; }

    public string? Description { get; }

    public QuestionnaireChoiceModel(string id, string label, string? description = null)
    {
        Id = id;
        Label = label;
        Description = description;
    }
}

/// <summary>The schema-defined "other" choice and its associated free-text input.</summary>
public sealed class QuestionnaireOtherModel
{
    public string OptionId { get; }

    public string Label { get; }

    public string? Placeholder { get; }

    public bool TextRequired { get; }

    public int MaxTextLength { get; }

    public QuestionnaireOtherModel(string optionId, string label, string? placeholder,
        bool textRequired, int maxTextLength)
    {
        OptionId = optionId;
        Label = label;
        Placeholder = placeholder;
        TextRequired = textRequired;
        MaxTextLength = maxTextLength;
    }
}

/// <summary>Declarative presentation of one server-published questionnaire question.</summary>
public sealed class QuestionnaireQuestionModel
{
    public string Id { get; }

    public string Prompt { get; }

    public string? HelpText { get; }

    public QuestionnaireQuestionKind Kind { get; }

    public IReadOnlyList<QuestionnaireChoiceModel> Choices { get; }

    public QuestionnaireOtherModel? Other { get; }

    public bool Required { get; }

    public int? MinSelections { get; }

    public int? MaxSelections { get; }

    public int? MaxTextLength { get; }

    public QuestionnaireQuestionModel(string id, string prompt, string? helpText,
        QuestionnaireQuestionKind kind, IReadOnlyList<QuestionnaireChoiceModel> choices,
        QuestionnaireOtherModel? other, bool required, int? minSelections = null,
        int? maxSelections = null, int? maxTextLength = null)
    {
        Id = id;
        Prompt = prompt;
        HelpText = helpText;
        Kind = kind;
        Choices = choices;
        Other = other;
        Required = required;
        MinSelections = minSelections;
        MaxSelections = maxSelections;
        MaxTextLength = maxTextLength;
    }
}

/// <summary>
/// UI-framework-neutral questionnaire presentation built from the server-published schema DTO.
/// The ordered choice ids are authoritative; renderers must submit ids, not localized labels.
/// </summary>
public sealed class QuestionnaireOverlayModel
{
    public string Title { get; }

    public string? Description { get; }

    public IReadOnlyList<QuestionnaireQuestionModel> Questions { get; }

    public string SubmitLabel { get; }

    public string CancelLabel { get; }

    public QuestionnaireOverlayModel(string title, string? description,
        IReadOnlyList<QuestionnaireQuestionModel> questions, string submitLabel, string cancelLabel)
    {
        Title = title;
        Description = description;
        Questions = questions;
        SubmitLabel = submitLabel;
        CancelLabel = cancelLabel;
    }
}

/// <summary>Typed answer DTO produced by questionnaire renderers.</summary>
public sealed class QuestionnaireAnswerDto
{
    public string QuestionId { get; }

    public IReadOnlyList<string> SelectedOptionIds { get; }

    public string? Text { get; }

    public string? OtherText { get; }

    public QuestionnaireAnswerDto(string questionId, IReadOnlyList<string> selectedOptionIds,
        string? text, string? otherText)
    {
        QuestionId = questionId;
        SelectedOptionIds = selectedOptionIds;
        Text = text;
        OtherText = otherText;
    }
}

/// <summary>Typed questionnaire response. Cancellation is not represented as a fabricated answer.</summary>
public sealed class QuestionnaireResponseDto
{
    public IReadOnlyList<QuestionnaireAnswerDto> Answers { get; }

    public bool Cancelled { get; }

    public QuestionnaireResponseDto(IReadOnlyList<QuestionnaireAnswerDto> answers, bool cancelled)
    {
        Answers = answers;
        Cancelled = cancelled;
    }
}

/// <summary>Raw fields collected by a plain-form renderer for one question.</summary>
public sealed class QuestionnairePlainFormInput
{
    /// <summary>Comma- or whitespace-separated server-published choice ids.</summary>
    public string? Selection { get; }

    public string? Text { get; }

    public string? OtherText { get; }

    public QuestionnairePlainFormInput(string? selection = null, string? text = null,
        string? otherText = null)
    {
        Selection = selection;
        Text = text;
        OtherText = otherText;
    }
}

/// <summary>Result of parsing a plain questionnaire form.</summary>
public sealed class QuestionnaireParseResult
{
    public QuestionnaireResponseDto? Response { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Response is not null;

    internal QuestionnaireParseResult(QuestionnaireResponseDto? response, IReadOnlyList<string> errors)
    {
        Response = response;
        Errors = errors;
    }
}

/// <summary>
/// Pure plain-form parser. It only accepts choice ids present in the published presentation model;
/// labels are never parsed. Host validation remains authoritative.
/// </summary>
public static class QuestionnairePlainFormParser
{
    public static QuestionnaireParseResult Parse(QuestionnaireOverlayModel questionnaire,
        IReadOnlyDictionary<string, QuestionnairePlainFormInput> inputs, bool cancelled = false)
    {
        if (cancelled)
        {
            return new QuestionnaireParseResult(
                new QuestionnaireResponseDto(Array.Empty<QuestionnaireAnswerDto>(), true),
                Array.Empty<string>());
        }

        var errors = new List<string>();
        var knownQuestions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questionnaire.Questions)
        {
            knownQuestions.Add(question.Id);
        }

        foreach (var input in inputs)
        {
            if (!knownQuestions.Contains(input.Key))
            {
                errors.Add("Unknown question id: " + input.Key);
            }
        }

        var answers = new List<QuestionnaireAnswerDto>();
        foreach (var question in questionnaire.Questions)
        {
            inputs.TryGetValue(question.Id, out var input);
            var text = input?.Text;
            var otherText = input?.OtherText;

            if (question.Kind == QuestionnaireQuestionKind.FreeText)
            {
                if (input?.Selection is not null && input.Selection.Trim().Length > 0)
                {
                    errors.Add("Selections are not allowed for free-text question " + question.Id);
                }

                if (otherText is not null)
                {
                    errors.Add("Other text is not allowed for free-text question " + question.Id);
                }

                if (question.Required && string.IsNullOrWhiteSpace(text))
                {
                    errors.Add("Text is required for question " + question.Id);
                }

                if (text is not null && question.MaxTextLength is int textLimit && text.Length > textLimit)
                {
                    errors.Add("Text exceeds the maximum length for question " + question.Id);
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    answers.Add(new QuestionnaireAnswerDto(question.Id, Array.Empty<string>(), text, null));
                }

                continue;
            }

            var selectedIds = ParseSelection(question, input?.Selection, errors);
            var otherSelected = question.Other is not null
                && selectedIds.Contains(question.Other.OptionId, StringComparer.Ordinal);

            if (question.Kind == QuestionnaireQuestionKind.SingleChoice && selectedIds.Count > 1)
            {
                errors.Add("Only one choice may be selected for question " + question.Id);
            }

            if (question.Required && selectedIds.Count == 0)
            {
                errors.Add("A choice is required for question " + question.Id);
            }

            if (question.MinSelections is int minimum && selectedIds.Count < minimum)
            {
                errors.Add("Too few choices selected for question " + question.Id);
            }

            if (question.MaxSelections is int maximum && selectedIds.Count > maximum)
            {
                errors.Add("Too many choices selected for question " + question.Id);
            }

            if (otherText is not null && !otherSelected)
            {
                errors.Add("Other text requires selecting the other choice for question " + question.Id);
            }

            if (otherSelected && question.Other is not null)
            {
                if (question.Other.TextRequired && string.IsNullOrWhiteSpace(otherText))
                {
                    errors.Add("Other text is required for question " + question.Id);
                }

                if (otherText is not null && otherText.Length > question.Other.MaxTextLength)
                {
                    errors.Add("Other text exceeds the maximum length for question " + question.Id);
                }
            }

            if (selectedIds.Count > 0)
            {
                answers.Add(new QuestionnaireAnswerDto(question.Id, selectedIds, null,
                    otherSelected ? otherText : null));
            }
        }

        return errors.Count == 0
            ? new QuestionnaireParseResult(new QuestionnaireResponseDto(answers, false), errors)
            : new QuestionnaireParseResult(null, errors);
    }

    private static List<string> ParseSelection(QuestionnaireQuestionModel question, string? raw,
        List<string> errors)
    {
        var selected = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return selected;
        }

        var allowed = new HashSet<string>(question.Choices.Select(choice => choice.Id), StringComparer.Ordinal);
        if (question.Other is not null)
        {
            allowed.Add(question.Other.OptionId);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = raw.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (!allowed.Contains(token))
            {
                errors.Add("Unknown choice id for question " + question.Id + ": " + token);
            }
            else if (!seen.Add(token))
            {
                errors.Add("Duplicate choice id for question " + question.Id + ": " + token);
            }
            else
            {
                selected.Add(token);
            }
        }

        return selected;
    }
}
