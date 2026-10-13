namespace OmniCore.Client;

internal static class LinkResources
{
    internal static bool ContainsKey(string key) => key is "links.title" or "links.openFailed" or "links.help";
    internal static bool TryTranslate(string locale, string key, out string text)
    {
        text = string.Empty;
        return ContainsKey(key) && For(locale).TryGetValue(key, out text!);
    }

    internal static Dictionary<string, string> For(string locale) => locale switch
    {
        "en" => new() { ["links.title"] = "Open link", ["links.openFailed"] = "The link could not be opened in your default browser.", ["links.help"] = "Click or Enter open link · Arrows select · Ctrl+C copy" },
        "fr" => new() { ["links.title"] = "Ouvrir le lien", ["links.openFailed"] = "Impossible d’ouvrir le lien dans votre navigateur par défaut.", ["links.help"] = "Clic ou Entrée ouvrir le lien · Flèches sélectionner · Ctrl+C copier" },
        "it" => new() { ["links.title"] = "Apri collegamento", ["links.openFailed"] = "Impossibile aprire il collegamento nel browser predefinito.", ["links.help"] = "Clic o Invio aprire link · Frecce selezionare · Ctrl+C copiare" },
        _ => new() { ["links.title"] = "Abrir enlace", ["links.openFailed"] = "No se pudo abrir el enlace en el navegador predeterminado.", ["links.help"] = "Clic o Enter abrir enlace · Flechas seleccionar · Ctrl+C copiar" },
    };
}
