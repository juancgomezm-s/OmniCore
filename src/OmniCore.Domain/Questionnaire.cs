namespace OmniCore.Domain;

/// <summary>Tipo de pregunta de un cuestionario (ADR-0045 §2).</summary>
public enum QuestionKind
{
    SingleChoice,
    MultipleChoice,
    FreeText,
}

/// <summary>Código tipado de cada validación de cuestionario (spec §71; ADR-0045 §4).</summary>
public enum QuestionnaireErrorCode
{
    // Schema
    NoQuestions,
    TooManyQuestions,
    EmptyTitle,
    EmptyQuestionId,
    DuplicateQuestionId,
    EmptyPrompt,
    PromptTooLong,
    EmptyOptionLabel,
    EmptyOptionId,
    DuplicateOptionId,
    OptionIdCollidesWithOther,
    OptionsRequired,
    OptionsNotAllowed,
    LabelTooLong,
    TooManyOptions,
    InvalidSelectionBounds,
    InvalidOtherTextLength,
    // Respuesta
    MissingRequiredAnswer,
    UnknownQuestionId,
    DuplicateAnswer,
    UnknownOptionId,
    DuplicateOptionSelection,
    TooFewSelections,
    TooManySelections,
    OtherExclusiveInSingleChoice,
    OtherTextNotAllowed,
    OtherTextRequired,
    OtherTextTooLong,
    TextTooLong,
    RequiredTextEmpty,
}

/// <summary>Error de validación con el id de la pregunta cuando aplica (ADR-0045 §4).</summary>
public sealed class QuestionnaireError
{
    public QuestionnaireErrorCode Code { get; }

    public string? QuestionId { get; }

    public string Detail { get; }

    public QuestionnaireError(QuestionnaireErrorCode code, string? questionId, string detail)
    {
        Code = code;
        QuestionId = questionId;
        Detail = detail;
    }

    public override string ToString() => Code + (QuestionId is null ? "" : "@" + QuestionId) + ": " + Detail;
}

/// <summary>Resultado de validar un cuestionario o una respuesta (ADR-0045 §4).</summary>
public sealed class QuestionnaireValidation
{
    public bool Valid { get; }

    public IReadOnlyList<QuestionnaireError> Errors { get; }

    private QuestionnaireValidation(bool valid, IReadOnlyList<QuestionnaireError> errors)
    {
        Valid = valid;
        Errors = errors;
    }

    public static QuestionnaireValidation Ok() => new(true, Array.Empty<QuestionnaireError>());

    public static QuestionnaireValidation Fail(IReadOnlyList<QuestionnaireError> errors) =>
        new(false, errors);
}

/// <summary>Opción de una pregunta (ADR-0045 §2). Contenido del modelo: se muestra tal cual.</summary>
public sealed class QuestionOption
{
    public string Id { get; }

    public string Label { get; }

    public string? Description { get; }

    public QuestionOption(string id, string label, string? description)
    {
        Id = id;
        Label = label;
        Description = description;
    }
}

/// <summary>
/// Campo "Otro" con texto libre (ADR-0045 §2). No se detecta comparando texto localizado: el id
/// es estable y forma parte del schema.
/// </summary>
public sealed class OtherInput
{
    public string OptionId { get; }

    public string Label { get; }

    public string? Placeholder { get; }

    public bool TextRequired { get; }

    public int MaxTextLength { get; }

    public OtherInput(string optionId, string label, string? placeholder, bool textRequired, int maxTextLength)
    {
        OptionId = optionId;
        Label = label;
        Placeholder = placeholder;
        TextRequired = textRequired;
        MaxTextLength = maxTextLength;
    }
}

/// <summary>Pregunta de un cuestionario (ADR-0045 §2).</summary>
public sealed class QuestionField
{
    public string Id { get; }

    public string Prompt { get; }

    public string? HelpText { get; }

    public QuestionKind Kind { get; }

    public IReadOnlyList<QuestionOption> Options { get; }

    public OtherInput? Other { get; }

    public bool Required { get; }

    public int? MinSelections { get; }

    public int? MaxSelections { get; }

    public int? MaxTextLength { get; }

    public QuestionField(string id, string prompt, string? helpText, QuestionKind kind,
        IReadOnlyList<QuestionOption> options, OtherInput? other, bool required,
        int? minSelections, int? maxSelections, int? maxTextLength)
    {
        Id = id;
        Prompt = prompt;
        HelpText = helpText;
        Kind = kind;
        Options = options;
        Other = other;
        Required = required;
        MinSelections = minSelections;
        MaxSelections = maxSelections;
        MaxTextLength = maxTextLength;
    }

    internal int SelectableCount => Options.Count + (Other is null ? 0 : 1);
}

/// <summary>Respuesta a una pregunta (ADR-0045 §2).</summary>
public sealed class QuestionAnswer
{
    public string QuestionId { get; }

    public IReadOnlyList<string> SelectedOptionIds { get; }

    public string? Text { get; }

    public string? OtherText { get; }

    public QuestionAnswer(string questionId, IReadOnlyList<string> selectedOptionIds, string? text,
        string? otherText)
    {
        QuestionId = questionId;
        SelectedOptionIds = selectedOptionIds;
        Text = text;
        OtherText = otherText;
    }
}

/// <summary>Un cuestionario completo (ADR-0045 §1): una o varias preguntas en una interacción.</summary>
public sealed class QuestionnaireSchema
{
    public string Title { get; }

    public string? Description { get; }

    public IReadOnlyList<QuestionField> Questions { get; }

    public QuestionnaireSchema(string title, string? description, IReadOnlyList<QuestionField> questions)
    {
        Title = title;
        Description = description;
        Questions = questions;
    }
}

/// <summary>
/// Límites configurables por request (ADR-0045 §3). El Host los valida; el modelo recibe un
/// error de tool tipado si el cuestionario los excede. Cambiar un límite cambia el
/// comportamiento: cada valor configurable tiene su test (ADR-0007).
/// </summary>
public sealed class QuestionnaireLimits
{
    public int MaxQuestions { get; }

    public int MaxOptionsPerQuestion { get; }

    public int MaxPromptChars { get; }

    public int MaxLabelChars { get; }

    public int MaxTextChars { get; }

    public QuestionnaireLimits(int maxQuestions, int maxOptionsPerQuestion, int maxPromptChars,
        int maxLabelChars, int maxTextChars)
    {
        MaxQuestions = maxQuestions;
        MaxOptionsPerQuestion = maxOptionsPerQuestion;
        MaxPromptChars = maxPromptChars;
        MaxLabelChars = maxLabelChars;
        MaxTextChars = maxTextChars;
    }

    /// <summary>Límites iniciales de ADR-0045 §3.</summary>
    public static QuestionnaireLimits Default() => new(5, 8, 500, 120, 2000);
}

/// <summary>
/// Validación de cuestionarios en el Host (ADR-0045 §4). Las preguntas son datos propuestos
/// por el modelo; el modelo no decide si su respuesta es válida. Puro y determinista.
/// </summary>
public static class QuestionnaireValidator
{
    /// <summary>Valida el schema antes de publicar la interacción (ADR-0045 §4).</summary>
    public static QuestionnaireValidation ValidateSchema(QuestionnaireSchema schema, QuestionnaireLimits limits)
    {
        var errors = new List<QuestionnaireError>();

        if (schema.Title.Length == 0)
        {
            errors.Add(new(QuestionnaireErrorCode.EmptyTitle, null, "el título no puede estar vacío"));
        }

        if (schema.Questions.Count == 0)
        {
            errors.Add(new(QuestionnaireErrorCode.NoQuestions, null, "un cuestionario requiere al menos 1 pregunta"));
        }

        if (schema.Questions.Count > limits.MaxQuestions)
        {
            errors.Add(new(QuestionnaireErrorCode.TooManyQuestions, null,
                schema.Questions.Count + " > " + limits.MaxQuestions));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in schema.Questions)
        {
            if (question.Id.Length == 0)
            {
                errors.Add(new(QuestionnaireErrorCode.EmptyQuestionId, question.Id, "id vacío"));
            }
            else if (!seen.Add(question.Id))
            {
                errors.Add(new(QuestionnaireErrorCode.DuplicateQuestionId, question.Id, "id repetido"));
            }

            if (question.Prompt.Length == 0)
            {
                errors.Add(new(QuestionnaireErrorCode.EmptyPrompt, question.Id, "prompt vacío"));
            }

            if (question.Prompt.Length > limits.MaxPromptChars)
            {
                errors.Add(new(QuestionnaireErrorCode.PromptTooLong, question.Id,
                    question.Prompt.Length + " > " + limits.MaxPromptChars));
            }

            ValidateOptions(question, limits, errors);
            if (question.Kind == QuestionKind.FreeText)
            {
                if (question.Options.Count > 0)
                {
                    errors.Add(new(QuestionnaireErrorCode.OptionsNotAllowed, question.Id,
                        "FreeText no admite opciones"));
                }
            }
            else if (question.Options.Count == 0 && question.Other is null)
            {
                errors.Add(new(QuestionnaireErrorCode.OptionsRequired, question.Id,
                    question.Kind + " requiere opciones u Otro"));
            }

            ValidateBounds(question, errors);
        }

        return errors.Count == 0 ? QuestionnaireValidation.Ok() : QuestionnaireValidation.Fail(errors);
    }

    /// <summary>
    /// Valida una respuesta contra el schema vigente (ADR-0045 §4). Cancelar es válido: se
    /// devuelve como resultado estructurado, no como respuesta inventada (ADR-0045 §5).
    /// </summary>
    public static QuestionnaireValidation ValidateResponse(QuestionnaireSchema schema,
        IReadOnlyList<QuestionAnswer> answers, bool cancelled, QuestionnaireLimits limits)
    {
        if (cancelled)
        {
            // Cancelar equivale a Cancelar, no a escoger ni a Deny (ADR-0045 §5): es válido por
            // sí mismo; la semántica la procesa el Host al resolver la interacción.
            return QuestionnaireValidation.Ok();
        }

        var errors = new List<QuestionnaireError>();
        var byId = new Dictionary<string, QuestionField>(StringComparer.Ordinal);
        foreach (var question in schema.Questions)
        {
            byId[question.Id] = question;
        }

        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var answer in answers)
        {
            if (!byId.TryGetValue(answer.QuestionId, out var question))
            {
                errors.Add(new(QuestionnaireErrorCode.UnknownQuestionId, answer.QuestionId,
                    "la pregunta no pertenece a la solicitud vigente"));
                continue;
            }

            if (!answered.Add(answer.QuestionId))
            {
                errors.Add(new(QuestionnaireErrorCode.DuplicateAnswer, answer.QuestionId,
                    "la pregunta aparece dos veces en la respuesta"));
                continue;
            }

            ValidateAnswer(question, answer, limits, errors);
        }

        foreach (var question in schema.Questions)
        {
            if (question.Required && !answered.Contains(question.Id))
            {
                errors.Add(new(QuestionnaireErrorCode.MissingRequiredAnswer, question.Id,
                    "pregunta requerida sin responder"));
            }
        }

        return errors.Count == 0 ? QuestionnaireValidation.Ok() : QuestionnaireValidation.Fail(errors);
    }

    private static void ValidateOptions(QuestionField question, QuestionnaireLimits limits,
        List<QuestionnaireError> errors)
    {
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in question.Options)
        {
            if (option.Id.Length == 0)
            {
                errors.Add(new(QuestionnaireErrorCode.EmptyOptionId, question.Id, "id de opción vacío"));
            }
            else if (!seenOptions.Add(option.Id))
            {
                errors.Add(new(QuestionnaireErrorCode.DuplicateOptionId, question.Id,
                    "opción repetida: " + option.Id));
            }

            if (option.Label.Length == 0)
            {
                errors.Add(new(QuestionnaireErrorCode.EmptyOptionLabel, question.Id,
                    "etiqueta vacía: " + option.Id));
            }

            if (option.Label.Length > limits.MaxLabelChars)
            {
                errors.Add(new(QuestionnaireErrorCode.LabelTooLong, question.Id,
                    option.Id + ": " + option.Label.Length + " > " + limits.MaxLabelChars));
            }
        }

        if (question.Options.Count > limits.MaxOptionsPerQuestion)
        {
            errors.Add(new(QuestionnaireErrorCode.TooManyOptions, question.Id,
                question.Options.Count + " opciones > MaxOptionsPerQuestion=" + limits.MaxOptionsPerQuestion));
        }

        if (question.Other is not null)
        {
            if (question.Other!.Label.Length == 0)
            {
                errors.Add(new(QuestionnaireErrorCode.EmptyOptionLabel, question.Id, "etiqueta de Otro vacía"));
            }

            if (question.Other!.Label.Length > limits.MaxLabelChars)
            {
                errors.Add(new(QuestionnaireErrorCode.LabelTooLong, question.Id, "etiqueta de Otro demasiado larga"));
            }

            if (question.Other!.OptionId.Length == 0)
            {
                errors.Add(new(QuestionnaireErrorCode.EmptyOptionId, question.Id, "id de Otro vacío"));
            }
            else if (seenOptions.Contains(question.Other!.OptionId))
            {
                errors.Add(new(QuestionnaireErrorCode.OptionIdCollidesWithOther, question.Id,
                    "id de Otro colisiona con una opción: " + question.Other!.OptionId));
            }

            if (question.Other!.MaxTextLength <= 0 || question.Other!.MaxTextLength > limits.MaxTextChars)
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidOtherTextLength, question.Id,
                    "MaxTextLength de Otro inválido: " + question.Other!.MaxTextLength));
            }
        }
    }

    private static void ValidateBounds(QuestionField question, List<QuestionnaireError> errors)
    {
        if (question.Kind == QuestionKind.FreeText)
        {
            if (question.MinSelections is not null || question.MaxSelections is not null)
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidSelectionBounds, question.Id,
                    "FreeText no admite Min/MaxSelections"));
            }

            if (question.MaxTextLength is not null && question.MaxTextLength <= 0)
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidOtherTextLength, question.Id,
                    "MaxTextLength inválido: " + question.MaxTextLength));
            }

            return;
        }

        var selectable = question.SelectableCount;
        if (question.Kind == QuestionKind.SingleChoice)
        {
            if (question.MinSelections is not null && question.MinSelections != 1)
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidSelectionBounds, question.Id,
                    "SingleChoice exige MinSelections=1 si se declara"));
            }

            if (question.MaxSelections is not null && question.MaxSelections != 1)
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidSelectionBounds, question.Id,
                    "SingleChoice exige MaxSelections=1 si se declara"));
            }
        }
        else
        {
            if (question.MinSelections is not null && (question.MinSelections < 1
                || question.MinSelections > selectable))
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidSelectionBounds, question.Id,
                    "MinSelections incoherente: " + question.MinSelections + "/" + selectable));
            }

            if (question.MaxSelections is not null && (question.MaxSelections < 1
                || question.MaxSelections > selectable))
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidSelectionBounds, question.Id,
                    "MaxSelections incoherente: " + question.MaxSelections + "/" + selectable));
            }

            if (question.MinSelections is not null && question.MaxSelections is not null
                && question.MinSelections > question.MaxSelections)
            {
                errors.Add(new(QuestionnaireErrorCode.InvalidSelectionBounds, question.Id,
                    "MinSelections > MaxSelections"));
            }
        }
    }

    private static void ValidateAnswer(QuestionField question, QuestionAnswer answer, QuestionnaireLimits limits,
        List<QuestionnaireError> errors)
    {
        var validOptionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in question.Options)
        {
            validOptionIds.Add(option.Id);
        }

        var otherSelected = false;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var optionId in answer.SelectedOptionIds)
        {
            if (question.Other is not null && optionId.Equals(question.Other!.OptionId, StringComparison.Ordinal))
            {
                otherSelected = true;
                continue;
            }

            if (!validOptionIds.Contains(optionId))
            {
                errors.Add(new(QuestionnaireErrorCode.UnknownOptionId, question.Id,
                    "opción desconocida: " + optionId));
                continue;
            }

            if (!selected.Add(optionId))
            {
                errors.Add(new(QuestionnaireErrorCode.DuplicateOptionSelection, question.Id,
                    "opción repetida: " + optionId));
            }
        }

        var selections = answer.SelectedOptionIds.Count;
        if (question.Kind == QuestionKind.SingleChoice)
        {
            // En SingleChoice, Otro desmarca cualquier otra opción: es exclusivo (ADR-0045 §2).
            if (otherSelected && selections > 1)
            {
                errors.Add(new(QuestionnaireErrorCode.OtherExclusiveInSingleChoice, question.Id,
                    "Otro es exclusivo en selección única"));
            }

            if (question.Required && selections != 1)
            {
                errors.Add(new(QuestionnaireErrorCode.TooFewSelections, question.Id,
                    "SingleChoice requerida exige exactamente 1 selección, recibió " + selections));
            }
            else if (!question.Required && selections > 1)
            {
                errors.Add(new(QuestionnaireErrorCode.TooManySelections, question.Id,
                    "SingleChoice no requerida admite a lo sumo 1 selección, recibió " + selections));
            }
        }
        else if (question.Kind == QuestionKind.MultipleChoice)
        {
            if (question.MinSelections is not null && selections < question.MinSelections)
            {
                errors.Add(new(QuestionnaireErrorCode.TooFewSelections, question.Id,
                    selections + " < MinSelections=" + question.MinSelections));
            }

            if (question.MaxSelections is not null && selections > question.MaxSelections)
            {
                errors.Add(new(QuestionnaireErrorCode.TooManySelections, question.Id,
                    selections + " > MaxSelections=" + question.MaxSelections));
            }
        }

        // Otro + texto: el texto solo se acepta si se seleccionó Otro (ADR-0045 §4).
        if (answer.OtherText is not null)
        {
            if (!otherSelected)
            {
                errors.Add(new(QuestionnaireErrorCode.OtherTextNotAllowed, question.Id,
                    "OtherText sin seleccionar Otro"));
            }
            else
            {
                var max = question.Other?.MaxTextLength ?? limits.MaxTextChars;
                if (answer.OtherText!.Length > max)
                {
                    errors.Add(new(QuestionnaireErrorCode.OtherTextTooLong, question.Id,
                        answer.OtherText!.Length + " > " + max));
                }

                if (question.Other is not null && question.Other!.TextRequired
                    && answer.OtherText!.Length == 0)
                {
                    errors.Add(new(QuestionnaireErrorCode.OtherTextRequired, question.Id,
                        "Otro exige texto y llegó vacío"));
                }
            }
        }
        else if (otherSelected && question.Other is not null && question.Other!.TextRequired)
        {
            errors.Add(new(QuestionnaireErrorCode.OtherTextRequired, question.Id,
                "Otro seleccionado con TextRequired pero sin texto"));
        }

        if (question.Kind == QuestionKind.FreeText)
        {
            if (selections > 0)
            {
                errors.Add(new(QuestionnaireErrorCode.OptionsNotAllowed, question.Id,
                    "FreeText no admite selecciones"));
            }

            var max = question.MaxTextLength ?? limits.MaxTextChars;
            if (answer.Text is not null && answer.Text!.Length > max)
            {
                errors.Add(new(QuestionnaireErrorCode.TextTooLong, question.Id,
                    answer.Text!.Length + " > " + max));
            }

            if (question.Required && string.IsNullOrWhiteSpace(answer.Text))
            {
                errors.Add(new(QuestionnaireErrorCode.RequiredTextEmpty, question.Id,
                    "pregunta requerida sin texto"));
            }
        }
    }
}
