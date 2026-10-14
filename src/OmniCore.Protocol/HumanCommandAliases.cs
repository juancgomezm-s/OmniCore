namespace OmniCore.Protocol;

/// <summary>
/// Human spellings for built-in commands only. Canonical wire identifiers never change.
/// All aliases are reserved regardless of the selected locale, so switching language cannot
/// silently select a different extension. Arguments, paths and prompt text are not translated.
/// </summary>
public static class HumanCommandAliases
{
    public sealed record Entry(string Canonical, string Spanish, string French, string Italian);

    public static IReadOnlyList<Entry> Entries { get; } = Array.AsReadOnly(new[]
    {
        new Entry("help", "ayuda", "aide", "aiuto"),
        new Entry("exit", "salir", "quitter", "esci"),
        new Entry("preferences", "preferencias", "préférences", "preferenze"),
        new Entry("tools", "herramientas", "outils", "strumenti"),
        new Entry("models", "modelos", "modèles", "modelli"),
        new Entry("model", "modelo", "modèle", "modello"),
        new Entry("tasks", "tareas", "tâches", "attività"),
        new Entry("events", "eventos", "événements", "eventi"),
        new Entry("context", "contexto", "contexte", "contesto"),
        new Entry("commands", "comandos", "commandes", "comandi"),
        new Entry("permissions", "permisos", "autorisations", "permessi"),
        new Entry("skills", "habilidades", "compétences", "competenze"),
        new Entry("memory", "memoria", "mémoire", "memoria"),
        new Entry("extensions", "extensiones", "extensions", "estensioni"),
        new Entry("hooks", "ganchos", "hooks", "hooks"),
        new Entry("openrouter", "openrouter", "openrouter", "openrouter"),
        new Entry("cancel", "cancelar", "annuler", "annulla"),
        new Entry("interrupt", "interrumpir", "interrompre", "interrompi"),
        new Entry("agents", "agentes", "agents", "agenti"),
        new Entry("delegate", "delegar", "déléguer", "delega"),
        new Entry("agent", "agente", "agent", "agente"),
        new Entry("join", "esperar", "attendre", "attendi"),
        new Entry("mode", "modo", "mode", "modalità"),
        new Entry("runmode", "modo-ejecucion", "mode-exécution", "modalità-esecuzione"),
        new Entry("reasoning", "razonamiento", "raisonnement", "ragionamento"),
        new Entry("animation", "animacion", "animation", "animazione"),
        new Entry("sidebar", "panel", "panneau", "pannello"),
        new Entry("fuente", "fuente", "source", "sorgente"),
        new Entry("login", "conectar", "connexion", "accedi"),
        new Entry("logout", "desconectar", "déconnexion", "disconnetti"),
        new Entry("doctor", "diagnostico", "diagnostic", "diagnostica"),
        new Entry("trust", "confianza", "confiance", "fiducia"),
        new Entry("resolve", "resolver", "résoudre", "risolvi"),
        new Entry("session", "sesion", "session", "sessione"),
        new Entry("worktree", "arbol-trabajo", "arbre-travail", "albero-lavoro"),
        new Entry("ask", "preguntar", "demander", "chiedi"),
        new Entry("act", "actuar", "agir", "agisci"),
        new Entry("plan", "plan", "plan", "piano"),
        new Entry("explain", "explicar", "expliquer", "spiega"),
        new Entry("orq-auth", "autorizar-orq", "autoriser-orq", "autorizza-orq"),
        new Entry("explore", "explorar", "explorer", "esplora"),
        new Entry("resume", "reanudar", "reprendre", "riprendi"),
        new Entry("fanout", "repartir", "répartir", "distribuisci"),
        new Entry("verify-journal", "verificar-journal", "vérifier-journal", "verifica-journal"),
        new Entry("audit", "auditoria", "audit", "revisione"),
        new Entry("gc", "limpiar-datos", "nettoyer-données", "pulisci-dati"),
        new Entry("tui", "interfaz", "interface", "interfaccia"),
    });

    private static readonly Dictionary<string, string> Names = BuildNames();

    public static string CanonicalName(string name) => Names.TryGetValue(Fold(name), out var canonical) ? canonical : name;

    // Host-managed canonical short names keep their established source precedence;
    // their new translated spellings are reserved, never extension aliases.
    public static bool IsReserved(string name) => Fold(name) is not ("explain" or "orq-auth") && Names.ContainsKey(Fold(name));

    public static string DisplayName(string canonical, string locale)
    {
        var entry = Entries.FirstOrDefault(entry => entry.Canonical == canonical);
        return entry is null ? canonical : UiLocaleCodes.NormalizeOrDefault(locale) switch
        { "es" => entry.Spanish, "fr" => entry.French, "it" => entry.Italian, _ => canonical };
    }

    public static IEnumerable<string> CompletionNames(IEnumerable<string> canonicalNames, string locale) =>
        canonicalNames.SelectMany(name => new[] { name, DisplayName(name, locale) }).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rewrites only the leading slash command; quoted/free arguments remain byte-for-byte intact.</summary>
    public static string NormalizeSlashHead(string input)
    {
        if (input.Length < 2 || input[0] != '/') return input;
        var end = 1;
        while (end < input.Length && !char.IsWhiteSpace(input[end])) end++;
        return "/" + CanonicalName(input[1..end]) + input[end..];
    }

    /// <summary>Normalizes known grammar words without re-tokenizing the prompt, quotes, paths or JSON.</summary>
    public static string NormalizeSlashSyntax(string input)
    {
        input = NormalizeSlashHead(input);
        if (!input.StartsWith('/')) return input;
        var headEnd = 1;
        while (headEnd < input.Length && !char.IsWhiteSpace(input[headEnd])) headEnd++;
        var spans = new List<(int Start, int Length)>();
        var words = new List<string>();
        var cursor = headEnd;
        while (cursor < input.Length && words.Count < 2)
        {
            while (cursor < input.Length && char.IsWhiteSpace(input[cursor])) cursor++;
            if (cursor >= input.Length || input[cursor] is '\'' or '"') break;
            var start = cursor;
            while (cursor < input.Length && !char.IsWhiteSpace(input[cursor])) cursor++;
            spans.Add((start, cursor - start)); words.Add(input[start..cursor]);
        }
        var normalized = NormalizeArguments(input[1..headEnd], words);
        for (var i = spans.Count - 1; i >= 0; i--)
            if (words[i] != normalized[i]) input = input.Remove(spans[i].Start, spans[i].Length).Insert(spans[i].Start, normalized[i]);
        return input;
    }

    public static CommandInvocation Normalize(CommandInvocation invocation)
    {
        var name = CanonicalName(invocation.Name);
        return invocation with { Name = name, Arguments = NormalizeArguments(name, invocation.Arguments) };
    }

    public static string[] NormalizeCli(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] == "--" || arguments[0].StartsWith('/')) return arguments;
        var name = CanonicalName(arguments[0]);
        return new[] { name }.Concat(NormalizeArguments(name, arguments.Skip(1).ToArray())).ToArray();
    }

    private static IReadOnlyList<string> NormalizeArguments(string command, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0] == "--") return arguments;
        // Only grammar positions with a finite vocabulary are eligible. Everything after them is data.
        var allowed = command switch
        {
            "skills" => new[] { "load", "release" },
            "memory" => new[] { "session" },
            "permissions" => new[] { "list", "revoke" },
            "extensions" => new[] { "trust", "revoke" },
            "model" => new[] { "list", "select", "policy", "qualify", "refresh" },
            "worktree" => new[] { "inspect", "preview", "retention", "cleanup", "discard", "undo" },
            "sidebar" => new[] { "settings", "focus", "user", "workspace" },
            "session" or "audit" => new[] { "purge" },
            "mode" or "runmode" => new[] { "plan", "act", "orq" },
            "reasoning" => new[] { "default", "run", "boost", "reset", "off", "on" },
            "animation" => new[] { "on", "off", "reset" },
            _ => Array.Empty<string>(),
        };
        if (allowed.Length == 0) return arguments;
        var result = arguments.ToArray();
        result[0] = Subcommand(result[0], allowed);
        if (result.Length > 1 && command == "memory" && result[0] == "session")
            result[1] = Subcommand(result[1], new[] { "add", "forget" });
        if (result.Length > 1 && command == "model" && result[0] == "policy")
            result[1] = Subcommand(result[1], new[] { "show", "set", "delete", "history" });
        if (result.Length > 1 && command == "reasoning" && result[0] is "default" or "run" or "boost")
            result[1] = Subcommand(result[1], new[] { "reset", "on", "off", "low", "medium", "high" });
        return result;
    }

    private static string Subcommand(string word, string[] allowed)
    {
        var normalized = Fold(word);
        foreach (var candidate in allowed)
        {
            if (normalized == candidate || Subcommands.TryGetValue(candidate, out var names) && names.Contains(normalized))
                return candidate;
        }
        return word;
    }

    private static readonly Dictionary<string, string[]> Subcommands = new()
    {
        ["load"] = ["cargar", "charger", "carica"], ["release"] = ["liberar", "liberer", "rilascia"],
        ["session"] = ["sesion", "sessione"], ["add"] = ["agregar", "ajouter", "aggiungi"],
        ["forget"] = ["olvidar", "oublier", "dimentica"], ["list"] = ["listar", "lister", "elenca"],
        ["revoke"] = ["revocar", "revoquer", "revoca"], ["trust"] = ["confiar", "approuver", "autorizza"],
        ["select"] = ["seleccionar", "selectionner", "seleziona"], ["policy"] = ["politica", "politique"],
        ["qualify"] = ["cualificar", "qualifier", "qualifica"], ["refresh"] = ["actualizar", "actualiser", "aggiorna"],
        ["show"] = ["mostrar", "afficher", "mostra"], ["set"] = ["establecer", "definir", "imposta"],
        ["delete"] = ["eliminar", "supprimer", "elimina"], ["history"] = ["historial", "historique", "cronologia"],
        ["inspect"] = ["inspeccionar", "inspecter", "ispeziona"], ["preview"] = ["previsualizar", "apercu", "anteprima"],
        ["retention"] = ["retencion", "conservation", "conservazione"], ["cleanup"] = ["limpiar", "nettoyer", "pulisci"],
        ["discard"] = ["descartar", "abandonner", "scarta"], ["undo"] = ["deshacer", "retablir", "ripristina"],
        ["settings"] = ["ajustes", "reglages", "impostazioni"], ["focus"] = ["enfocar", "cibler", "focalizza"],
        ["user"] = ["usuario", "utilisateur", "utente"], ["workspace"] = ["espacio", "espace", "spazio"],
        ["purge"] = ["purgar", "purger", "svuota"],
        ["plan"] = ["planificar", "planifier", "pianifica"], ["act"] = ["actuar", "agir", "agisci"],
        ["orq"] = ["orquestar", "orchestrer", "orchestra"], ["default"] = ["predeterminado", "defaut", "predefinito"],
        ["run"] = ["ejecucion", "execution", "esecuzione"], ["boost"] = ["refuerzo", "renfort", "potenziamento"],
        ["reset"] = ["restablecer", "reinitialiser", "ripristina"], ["on"] = ["activar", "activer", "attiva"],
        ["off"] = ["desactivar", "desactiver", "disattiva"], ["low"] = ["bajo", "faible", "basso"],
        ["medium"] = ["medio", "moyen"], ["high"] = ["alto", "eleve"],
    };

    private static Dictionary<string, string> BuildNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
            foreach (var name in new[] { entry.Canonical, entry.Spanish, entry.French, entry.Italian })
            {
                var folded = Fold(name);
                if (names.TryGetValue(folded, out var previous) && previous != entry.Canonical)
                    throw new InvalidOperationException("Ambiguous built-in command alias: " + name);
                names[folded] = entry.Canonical;
            }
        return names;
    }

    private static string Fold(string value) => string.Concat(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
            != System.Globalization.UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
}
