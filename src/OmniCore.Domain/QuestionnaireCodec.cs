namespace OmniCore.Domain;

using System.Text.Json;

/// <summary>
/// Codec JSON del cuestionario (ADR-0045 §1, §7). El schema y la respuesta viajan como
/// artifacts content-addressed referenciados por los eventos (<c>InteractionRequested</c> /
/// <c>InteractionResolved</c>); el journal no duplica texto libre voluminoso. Todo pasa por
/// redacción antes de persistir (ADR-0018). Determinista y sin I/O.
/// </summary>
public static class QuestionnaireCodec
{
    /// <summary>Serializa el schema a JSON para guardarlo como artifact (ADR-0045 §7).</summary>
    public static string EncodeSchema(QuestionnaireSchema schema)
    {
        var questions = new List<string>();
        foreach (var q in schema.Questions)
        {
            questions.Add(EncodeField(q));
        }

        var parts = new List<string> { JsonField("title", schema.Title) };
        if (schema.Description is not null) parts.Add(JsonField("description", schema.Description));
        parts.Add(JsonFieldRaw("questions", "[" + string.Join(",", questions.ToArray()) + "]"));
        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    private static string EncodeField(QuestionField q)
    {
        var parts = new List<string> {
            JsonField("id", q.Id),
            JsonField("prompt", q.Prompt),
        };
        if (q.HelpText is not null) parts.Add(JsonField("helpText", q.HelpText));
        parts.Add(JsonField("kind", q.Kind.ToString()));
        parts.Add(JsonFieldRaw("options", "[" + string.Join(",", q.Options
            .Select(o => EncodeOption(o)).ToArray()) + "]"));
        if (q.Other is not null) parts.Add(JsonFieldRaw("other", EncodeOther(q.Other)));
        parts.Add(JsonFieldBool("required", q.Required));
        if (q.MinSelections is not null) parts.Add(JsonFieldRaw("minSelections", q.MinSelections.Value.ToString()));
        if (q.MaxSelections is not null) parts.Add(JsonFieldRaw("maxSelections", q.MaxSelections.Value.ToString()));
        if (q.MaxTextLength is not null) parts.Add(JsonFieldRaw("maxTextLength", q.MaxTextLength.Value.ToString()));

        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    private static string EncodeOption(QuestionOption o)
    {
        var parts = new List<string> { JsonField("id", o.Id), JsonField("label", o.Label) };
        if (o.Description is not null) parts.Add(JsonField("description", o.Description));
        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    private static string EncodeOther(OtherInput o)
    {
        var parts = new List<string> { JsonField("optionId", o.OptionId), JsonField("label", o.Label) };
        if (o.Placeholder is not null) parts.Add(JsonField("placeholder", o.Placeholder));
        parts.Add(JsonFieldBool("textRequired", o.TextRequired));
        parts.Add(JsonField("maxTextLength", o.MaxTextLength.ToString()));
        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    /// <summary>
    /// Deserializa el schema de un artifact redactado (ADR-0045 §7). Devuelve null si el JSON
    /// no tiene la forma esperada (nunca lanza en producción de forma no controlada).
    /// </summary>
    public static QuestionnaireSchema? DecodeSchema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var title = GetString(root, "title") ?? "";
            var description = GetString(root, "description");
            var questions = new List<QuestionField>();
            if (!root.TryGetProperty("questions", out var qArray) || qArray.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in qArray.EnumerateArray())
            {
                var field = DecodeField(item);
                if (field is null)
                {
                    return null;
                }

                questions.Add(field!);
            }

            return new QuestionnaireSchema(title, description, questions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static QuestionField? DecodeField(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = GetString(item, "id") ?? "";
        var prompt = GetString(item, "prompt") ?? "";
        var helpText = GetString(item, "helpText");
        var kindText = GetString(item, "kind") ?? "";
        if (!Enum.TryParse<QuestionKind>(kindText, out var kind))
        {
            return null;
        }

        var options = new List<QuestionOption>();
        if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in opts.EnumerateArray())
            {
                options.Add(new QuestionOption(GetString(o, "id") ?? "", GetString(o, "label") ?? "",
                    GetString(o, "description")));
            }
        }

        OtherInput? other = null;
        if (item.TryGetProperty("other", out var otherEl) && otherEl.ValueKind == JsonValueKind.Object)
        {
            var textRequired = GetBool(otherEl, "textRequired", false);
            var maxLen = GetInt(otherEl, "maxTextLength");
            other = new OtherInput(GetString(otherEl, "optionId") ?? "", GetString(otherEl, "label") ?? "",
                GetString(otherEl, "placeholder"), textRequired, maxLen ?? 2000);
        }

        var required = GetBool(item, "required", false);
        var min = GetInt(item, "minSelections");
        var max = GetInt(item, "maxSelections");
        var maxText = GetInt(item, "maxTextLength");
        return new QuestionField(id, prompt, helpText, kind, options, other, required, min, max, maxText);
    }

    /// <summary>Serializa una respuesta (listado de QuestionAnswer) para el artifact.</summary>
    public static string EncodeAnswers(IReadOnlyList<QuestionAnswer> answers)
    {
        var items = new List<string>();
        foreach (var a in answers)
        {
            items.Add(EncodeAnswer(a));
        }

        return "[" + string.Join(",", items.ToArray()) + "]";
    }

    private static string EncodeAnswer(QuestionAnswer a)
    {
        var parts = new List<string> {
            JsonField("questionId", a.QuestionId),
            JsonFieldRaw("selectedOptionIds", "[" + string.Join(",",
                a.SelectedOptionIds.Select(s => "\"" + Esc(s) + "\"").ToArray()) + "]"),
        };
        if (a.Text is not null) parts.Add(JsonField("text", a.Text));
        if (a.OtherText is not null) parts.Add(JsonField("otherText", a.OtherText));
        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    /// <summary>Deserializa una respuesta desde el JSON del artifact/comando.</summary>
    public static IReadOnlyList<QuestionAnswer> DecodeAnswers(string json)
    {
        var result = new List<QuestionAnswer>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var selected = new List<string>();
                if (item.TryGetProperty("selectedOptionIds", out var selEl)
                    && selEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in selEl.EnumerateArray())
                    {
                        selected.Add(s.GetString() ?? "");
                    }
                }

                result.Add(new QuestionAnswer(
                    GetString(item, "questionId") ?? "",
                    selected,
                    GetString(item, "text"),
                    GetString(item, "otherText")));
            }
        }
        catch (JsonException)
        {
            // respuesta malformada → lista vacía (el Host la valida y rechaza)
        }

        return result;
    }

    private static string? GetString(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static int? GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return null;
    }

    private static bool GetBool(JsonElement el, string name, bool def)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return def;
    }

    private static string JsonField(string key, string value) =>
        "\"" + key + "\":\"" + Esc(value) + "\"";

    private static string JsonFieldBool(string key, bool value) =>
        "\"" + key + "\":" + (value ? "true" : "false");

    private static string JsonFieldRaw(string key, string raw) =>
        "\"" + key + "\":" + raw;

    internal static string Esc(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}

/// <summary>
/// Resultado de una invocación <c>user.ask</c> (ADR-0045 §1, §6). Discriminado por <c>Status</c>:
/// <c>answered</c> (respuesta tipada válida), <c>cancelled</c> (el humano canceló), <c>input_required</c>
/// (sin cliente interactivo: la interacción queda durable y la Lane deriva WaitingForInput, NUNCA se
/// inventa una respuesta ni se aplica Deny) y <c>invalid</c> (el esquema propuesto por el modelo no pasa
/// la validación del Host).
/// </summary>
public sealed class QuestionnaireAskOutcome
{
    public string Status { get; }

    public IReadOnlyList<QuestionAnswer>? Answers { get; }

    public bool Cancelled { get; }

    public IReadOnlyList<QuestionnaireError>? Errors { get; }

    private QuestionnaireAskOutcome(string status, IReadOnlyList<QuestionAnswer>? answers, bool cancelled,
        IReadOnlyList<QuestionnaireError>? errors)
    {
        Status = status;
        Answers = answers;
        Cancelled = cancelled;
        Errors = errors;
    }

    public bool IsAnswered => Status == "answered";

    public bool IsInputRequired => Status == "input_required";

    public bool IsCancel => Status == "cancelled";

    public bool IsInvalid => Status == "invalid";

    public static QuestionnaireAskOutcome Answered(IReadOnlyList<QuestionAnswer> answers) =>
        new("answered", answers, false, null);

    public static QuestionnaireAskOutcome CancelledOutcome() => new("cancelled", null, true, null);

    public static QuestionnaireAskOutcome InputRequired() => new("input_required", null, false, null);

    public static QuestionnaireAskOutcome Invalid(IReadOnlyList<QuestionnaireError> errors) =>
        new("invalid", null, false, errors);
}
