namespace OmniCore.Client;

/// <summary>Model picker group labels; model and company identities stay literal.</summary>
public static class ModelPickerResources
{
    public static bool TryTranslate(string locale, string key, out string text)
    {
        string[]? values = key switch
        {
            "modelPicker.free" => ["Gratuitos", "Free models", "Modèles gratuits", "Modelli gratuiti"],
            "modelPicker.more" => ["Otros modelos", "Other models", "Autres modèles", "Altri modelli"],
            _ => null
        };
        text = values?[locale switch { "es" => 0, "fr" => 2, "it" => 3, _ => 1 }] ?? "";
        return values is not null;
    }
}
