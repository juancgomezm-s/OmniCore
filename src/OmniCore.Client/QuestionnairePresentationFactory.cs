namespace OmniCore.Client;

/// <summary>Builds the same framework-neutral model for plain and TUI renderers from Host-published schema JSON.</summary>
public static class QuestionnairePresentationFactory
{
    public static QuestionnaireOverlayModel? FromJson(string json, string locale = "es")
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("questions", out var questions) || questions.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var models = new List<QuestionnaireQuestionModel>();
            foreach (var item in questions.EnumerateArray())
            {
                var kindText = String(item, "kind");
                if (!Enum.TryParse<QuestionnaireQuestionKind>(kindText, true, out var kind)) return null;
                var choices = item.TryGetProperty("options", out var options) && options.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? options.EnumerateArray().Select(option => new QuestionnaireChoiceModel(String(option, "id"),
                        String(option, "label"), NullableString(option, "description"))).ToArray()
                    : Array.Empty<QuestionnaireChoiceModel>();
                QuestionnaireOtherModel? other = null;
                if (item.TryGetProperty("other", out var otherElement) && otherElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                    other = new QuestionnaireOtherModel(String(otherElement, "optionId"), String(otherElement, "label"),
                        NullableString(otherElement, "placeholder"), Bool(otherElement, "textRequired"), Int(otherElement, "maxTextLength", 2000));
                models.Add(new QuestionnaireQuestionModel(String(item, "id"), String(item, "prompt"),
                    NullableString(item, "helpText"), kind, choices, other, Bool(item, "required"),
                    NullableInt(item, "minSelections"), NullableInt(item, "maxSelections"), NullableInt(item, "maxTextLength")));
            }
            return new QuestionnaireOverlayModel(String(root, "title"), NullableString(root, "description"), models,
                locale == "en" ? "Submit" : "Enviar", locale == "en" ? "Cancel" : "Cancelar");
        }
        catch (System.Text.Json.JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string String(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? "" : "";
    private static string? NullableString(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;
    private static bool Bool(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True;
    private static int Int(System.Text.Json.JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static int? NullableInt(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
}
