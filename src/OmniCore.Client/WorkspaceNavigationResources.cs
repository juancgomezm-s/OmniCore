namespace OmniCore.Client;

public static class WorkspaceNavigationResources
{
    private static readonly Dictionary<string, string[]> Values = new(StringComparer.Ordinal)
    {
        ["busy"] = [
            "Termina o cancela la operación y retira los adjuntos antes de cambiar de carpeta. La conversación actual se conserva en su workspace.",
            "Finish or cancel the operation and remove attachments before changing folders. The current conversation stays in its workspace.",
            "Terminez ou annulez l’opération et retirez les pièces jointes avant de changer de dossier. La conversation reste dans son espace de travail.",
            "Termina o annulla l’operazione e rimuovi gli allegati prima di cambiare cartella. La conversazione resta nel suo workspace."],
        ["missing"] = [
            "No se puede abrir esa carpeta. Usa !cd seguido de una ruta existente; puedes escribirla entre comillas. Se conserva el borrador.",
            "Cannot open that folder. Use !cd followed by an existing path; quotes are supported. Your draft is kept.",
            "Impossible d’ouvrir ce dossier. Utilisez !cd suivi d’un chemin existant, éventuellement entre guillemets. Le brouillon est conservé.",
            "Impossibile aprire la cartella. Usa !cd seguito da un percorso esistente, anche tra virgolette. La bozza viene conservata."]
    };

    public static string Text(string locale, string key) => Values[key][locale switch { "en" => 1, "fr" => 2, "it" => 3, _ => 0 }];
}
