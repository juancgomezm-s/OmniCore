using OmniCore.Domain;

namespace OmniCore.Tests;

/// <summary>
/// Tests de validación de cuestionarios (ADR-0045 §4): una o varias preguntas, opción única,
/// múltiple, texto libre y "Otro"; y rechazo de respuestas inválidas. El frame TUI que renderiza
/// estos DTOs es M4 (ADR-0045 §6); aquí solo se valida el contrato puro.
/// </summary>
public sealed class QuestionnaireTests
{
    private static QuestionOption Opt(string id, string label = "opción") => new(id, label, null);

    private static QuestionField Single(string id, bool required = true, OtherInput? other = null)
        => new(id, "elige una", null, QuestionKind.SingleChoice,
            new[] { Opt("a", "A"), Opt("b", "B") }, other, required, null, null, null);

    private static QuestionField Multi(string id, int? min = null, int? max = null, bool required = true)
        => new(id, "elige varias", null, QuestionKind.MultipleChoice,
            new[] { Opt("x", "X"), Opt("y", "Y"), Opt("z", "Z") }, null, required, min, max, null);

    private static QuestionField Text(string id, bool required = true, int? maxTextLength = null)
        => new(id, "escribe", null, QuestionKind.FreeText, Array.Empty<QuestionOption>(), null,
            required, null, null, maxTextLength);

    private static QuestionnaireSchema Schema(params QuestionField[] questions)
        => new("Cuestionario", null, questions);

    private static QuestionAnswer Answer(string questionId, string[]? selected = null, string? text = null,
        string? otherText = null) => new(questionId, selected ?? Array.Empty<string>(), text, otherText);

    private static QuestionnaireValidation Respond(QuestionnaireSchema schema,
        params QuestionAnswer[] answers)
        => QuestionnaireValidator.ValidateResponse(schema, answers, cancelled: false,
            QuestionnaireLimits.Default());

    private static bool Has(QuestionnaireValidation v, QuestionnaireErrorCode code, string? questionId)
    {
        foreach (var e in v.Errors)
        {
            if (e.Code == code && e.QuestionId == questionId)
            {
                return true;
            }
        }

        return false;
    }

    // ---- Schema: una o varias preguntas ----

    [Fact]
    public void Schema_with_several_questions_is_valid()
    {
        var schema = Schema(Single("q1"), Multi("q2", min: 1, max: 2), Text("q3"));

        var validation = QuestionnaireValidator.ValidateSchema(schema, QuestionnaireLimits.Default());

        Assert.True(validation.Valid);
        Assert.Empty(validation.Errors);
    }

    [Fact]
    public void Schema_rejects_structural_errors()
    {
        var schema = new QuestionnaireSchema("", null, new QuestionField[]
        {
            Single("q"), // id duplicado abajo
            Single("q"),
            new QuestionField("", "", null, QuestionKind.SingleChoice,
                new[] { Opt("a") }, null, true, null, null, null), // id vacío + prompt vacío
            Text("free", maxTextLength: 0), // MaxTextLength inválido
            Single("bad", other: new OtherInput("a", "Otro", null, true, 100)), // colisión con opción
            new QuestionField("bounds", "bounds", null, QuestionKind.SingleChoice,
                new[] { Opt("a") }, null, true, null, 2, null), // SingleChoice con Max=2
            new QuestionField("nofree", "nofree", null, QuestionKind.FreeText,
                new[] { Opt("a") }, null, true, null, null, null), // FreeText con opciones
        });

        var validation = QuestionnaireValidator.ValidateSchema(schema, QuestionnaireLimits.Default());

        Assert.False(validation.Valid);
        Assert.True(Has(validation, QuestionnaireErrorCode.EmptyTitle, null));
        Assert.True(Has(validation, QuestionnaireErrorCode.DuplicateQuestionId, "q"));
        Assert.True(Has(validation, QuestionnaireErrorCode.EmptyQuestionId, ""));
        Assert.True(Has(validation, QuestionnaireErrorCode.EmptyPrompt, ""));
        Assert.True(Has(validation, QuestionnaireErrorCode.InvalidOtherTextLength, "free"));
        Assert.True(Has(validation, QuestionnaireErrorCode.OptionIdCollidesWithOther, "bad"));
        Assert.True(Has(validation, QuestionnaireErrorCode.InvalidSelectionBounds, "bounds"));
        Assert.True(Has(validation, QuestionnaireErrorCode.OptionsNotAllowed, "nofree"));
    }

    [Fact]
    public void Choice_without_options_or_other_is_rejected()
    {
        var schema = Schema(new QuestionField("solo", "solo", null, QuestionKind.SingleChoice,
            Array.Empty<QuestionOption>(), null, true, null, null, null));

        var validation = QuestionnaireValidator.ValidateSchema(schema, QuestionnaireLimits.Default());

        Assert.False(validation.Valid);
        Assert.True(Has(validation, QuestionnaireErrorCode.OptionsRequired, "solo"));
    }

    [Fact]
    public void Limits_are_configurable_and_change_validation()
    {
        // Con límites estrictos el mismo schema falla; con Default pasa: el valor configurable
        // cambia el comportamiento (lección ADR-0007).
        var twoQuestions = Schema(Single("q1"), Single("q2"));
        var strict = new QuestionnaireLimits(1, 8, 500, 120, 2000);

        Assert.False(QuestionnaireValidator.ValidateSchema(twoQuestions, strict).Valid);
        Assert.True(QuestionnaireValidator.ValidateSchema(twoQuestions,
            QuestionnaireLimits.Default()).Valid);

        var threeOptions = Schema(new QuestionField("q", "q", null, QuestionKind.MultipleChoice,
            new[] { Opt("a"), Opt("b"), Opt("c") }, null, true, null, null, null));
        var strictOptions = new QuestionnaireLimits(5, 2, 500, 120, 2000);

        Assert.False(QuestionnaireValidator.ValidateSchema(threeOptions, strictOptions).Valid);
        Assert.True(QuestionnaireValidator.ValidateSchema(threeOptions,
            QuestionnaireLimits.Default()).Valid);
    }

    // ---- SingleChoice ----

    [Fact]
    public void Single_choice_accepts_exactly_one_selection()
    {
        var schema = Schema(Single("q1"));

        Assert.True(Respond(schema, Answer("q1", new[] { "a" })).Valid);
        // No requerida admite cero o una.
        var optional = Schema(Single("q1", required: false));
        Assert.True(Respond(optional).Valid);
        Assert.True(Respond(optional, Answer("q1", new[] { "b" })).Valid);
        // Requerida sin exactamente 1 → inválida. Ausente y presente-pero-vacía son errores distintos.
        var missing = Respond(schema);
        Assert.False(missing.Valid);
        Assert.True(Has(missing, QuestionnaireErrorCode.MissingRequiredAnswer, "q1"));
        var empty = Respond(schema, Answer("q1"));
        Assert.False(empty.Valid);
        Assert.True(Has(empty, QuestionnaireErrorCode.TooFewSelections, "q1"));
    }

    [Fact]
    public void Single_choice_rejects_two_options()
    {
        var schema = Schema(Single("q1", required: false));

        var validation = Respond(schema, Answer("q1", new[] { "a", "b" }));

        Assert.False(validation.Valid);
        Assert.True(Has(validation, QuestionnaireErrorCode.TooManySelections, "q1"));
    }

    // ---- "Otro" ----

    [Fact]
    public void Other_alone_is_valid_and_carries_text()
    {
        var other = new OtherInput("otro", "Otro", "describe", textRequired: true, 50);
        var schema = Schema(Single("q1", other: other));

        var validation = Respond(schema, Answer("q1", new[] { "otro" }, otherText: "contexto propio"));

        Assert.True(validation.Valid);
        // Otro con TextRequired exige texto no vacío.
        var empty = Respond(schema, Answer("q1", new[] { "otro" }, otherText: ""));
        Assert.False(empty.Valid);
        Assert.True(Has(empty, QuestionnaireErrorCode.OtherTextRequired, "q1"));
        var noText = Respond(schema, Answer("q1", new[] { "otro" }));
        Assert.False(noText.Valid);
        Assert.True(Has(noText, QuestionnaireErrorCode.OtherTextRequired, "q1"));
    }

    [Fact]
    public void Other_is_exclusive_in_single_choice()
    {
        var other = new OtherInput("otro", "Otro", null, false, 50);
        var schema = Schema(Single("q1", other: other));

        var validation = Respond(schema, Answer("q1", new[] { "a", "otro" }));

        Assert.False(validation.Valid);
        Assert.True(Has(validation, QuestionnaireErrorCode.OtherExclusiveInSingleChoice, "q1"));
    }

    [Fact]
    public void Other_text_is_rejected_when_other_not_selected()
    {
        var other = new OtherInput("otro", "Otro", null, false, 50);
        var schema = Schema(Single("q1", other: other));

        var validation = Respond(schema, Answer("q1", new[] { "a" }, otherText: "texto colgado"));

        Assert.False(validation.Valid);
        Assert.True(Has(validation, QuestionnaireErrorCode.OtherTextNotAllowed, "q1"));
    }

    [Fact]
    public void Other_text_respects_its_own_max_length()
    {
        var other = new OtherInput("otro", "Otro", null, false, 5);
        var schema = Schema(Single("q1", other: other));

        var validation = Respond(schema, Answer("q1", new[] { "otro" }, otherText: "demasiado largo"));

        Assert.False(validation.Valid);
        Assert.True(Has(validation, QuestionnaireErrorCode.OtherTextTooLong, "q1"));
    }

    // ---- MultipleChoice ----

    [Fact]
    public void Multiple_choice_respects_min_and_max()
    {
        var schema = Schema(Multi("q1", min: 1, max: 2));

        Assert.True(Respond(schema, Answer("q1", new[] { "x", "y" })).Valid);

        var tooFew = Respond(schema, Answer("q1"));
        Assert.False(tooFew.Valid);
        Assert.True(Has(tooFew, QuestionnaireErrorCode.TooFewSelections, "q1"));

        var tooMany = Respond(schema, Answer("q1", new[] { "x", "y", "z" }));
        Assert.False(tooMany.Valid);
        Assert.True(Has(tooMany, QuestionnaireErrorCode.TooManySelections, "q1"));
    }

    // ---- FreeText ----

    [Fact]
    public void Free_text_accepts_text_and_rejects_selections()
    {
        var schema = Schema(Text("q1"));

        Assert.True(Respond(schema, Answer("q1", text: "notas del usuario")).Valid);

        var withOptions = Respond(schema, Answer("q1", new[] { "x" }, text: "mezcla"));
        Assert.False(withOptions.Valid);
        Assert.True(Has(withOptions, QuestionnaireErrorCode.OptionsNotAllowed, "q1"));

        var empty = Respond(schema, Answer("q1"));
        Assert.False(empty.Valid);
        Assert.True(Has(empty, QuestionnaireErrorCode.RequiredTextEmpty, "q1"));
    }

    [Fact]
    public void Free_text_enforces_max_length()
    {
        var schema = Schema(Text("q1", maxTextLength: 3));

        var validation = Respond(schema, Answer("q1", text: "cuatro"));

        Assert.False(validation.Valid);
        Assert.True(Has(validation, QuestionnaireErrorCode.TextTooLong, "q1"));
    }

    // ---- Respuestas inválidas ----

    [Fact]
    public void Response_rejects_unknown_and_duplicated_entries()
    {
        var schema = Schema(Single("q1"));

        var unknownQuestion = Respond(schema, Answer("no-existe", new[] { "a" }));
        Assert.False(unknownQuestion.Valid);
        Assert.True(Has(unknownQuestion, QuestionnaireErrorCode.UnknownQuestionId, "no-existe"));

        var duplicated = Respond(schema, Answer("q1", new[] { "a" }), Answer("q1", new[] { "b" }));
        Assert.False(duplicated.Valid);
        Assert.True(Has(duplicated, QuestionnaireErrorCode.DuplicateAnswer, "q1"));

        var unknownOption = Respond(schema, Answer("q1", new[] { "zzz" }));
        Assert.False(unknownOption.Valid);
        Assert.True(Has(unknownOption, QuestionnaireErrorCode.UnknownOptionId, "q1"));

        var repeatedOption = Respond(schema, Answer("q1", new[] { "a", "a" }));
        Assert.False(repeatedOption.Valid);
        Assert.True(Has(repeatedOption, QuestionnaireErrorCode.DuplicateOptionSelection, "q1"));

        var missingRequired = Respond(schema);
        Assert.False(missingRequired.Valid);
        Assert.True(Has(missingRequired, QuestionnaireErrorCode.MissingRequiredAnswer, "q1"));
    }

    // ---- Cancelar ----

    [Fact]
    public void Cancelled_response_is_valid_by_itself()
    {
        // Cancelar es válido sin respuestas (ADR-0045 §5): es un resultado estructurado, no
        // escoger ni Deny; la semántica la resuelve el Host.
        var schema = Schema(Single("q1"));

        var validation = QuestionnaireValidator.ValidateResponse(schema,
            Array.Empty<QuestionAnswer>(), cancelled: true, QuestionnaireLimits.Default());

        Assert.True(validation.Valid);
        Assert.Empty(validation.Errors);
    }
}
