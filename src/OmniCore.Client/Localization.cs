namespace OmniCore.Client;

using OmniCore.Protocol;

/// <summary>
/// Resolver de texto localizado (ADR-0040): el servidor envía claves + argumentos; el cliente
/// las resuelve con sus recursos es/en (es por defecto). Fallback: en, y luego la clave literal.
/// Los prompts al modelo y los datos técnicos no se traducen.
/// </summary>
public sealed class Localization
{
    /// <summary>Idioma configurado (por defecto es).</summary>
    public string Locale { get; }

    public Localization(string locale) => Locale = locale;

    public static Localization Spanish() => new("es");

    public static Localization English() => new("en");

    /// <summary>Resuelve una clave con argumentos al texto del locale activo.</summary>
    public string Resolve(string key, string? argName, string? argValue)
    {
        var text = Resources(key);
        if (argName is not null && argValue is not null)
        {
            text = text.Replace("{" + argName + "}", argValue!);
        }

        return text;
    }

    private string Resources(string key)
    {
        var es = _es.TryGetValue(key, out var e) ? e : null;
        if (Locale == "es" && es is not null)
        {
            return es!;
        }

        var en = _en.TryGetValue(key, out var v) ? v : null;
        return en is not null ? en! : key;
    }

    private static readonly Dictionary<string, string> _es = new()
    {
        ["interaction.permission.allow_once"] = "Permitir una vez",
        ["interaction.permission.allow_run"] = "Permitir en este Run",
        ["interaction.permission.deny"] = "Denegar",
        ["interaction.plan_approval.approve_execute"] = "Aprobar y ejecutar",
        ["interaction.plan_approval.approve_only"] = "Aprobar sin ejecutar",
        ["interaction.plan_approval.continue_planning"] = "Seguir planificando",
        ["conversation.user"] = "Usuario",
        ["conversation.assistant"] = "Asistente",
        ["conversation.tool"] = "Herramienta",
        ["status.mode.plan"] = "plan",
        ["status.mode.act"] = "act",
        ["status.mode.orchestrate"] = "orquestar",
        ["status.quota.unknown"] = "—",
        ["status.interactions.pending"] = "! {count} pendientes",
    };

    private static readonly Dictionary<string, string> _en = new()
    {
        ["interaction.permission.allow_once"] = "Allow once",
        ["interaction.permission.allow_run"] = "Allow for this Run",
        ["interaction.permission.deny"] = "Deny",
        ["interaction.plan_approval.approve_execute"] = "Approve and execute",
        ["interaction.plan_approval.approve_only"] = "Approve without executing",
        ["interaction.plan_approval.continue_planning"] = "Continue planning",
        ["conversation.user"] = "User",
        ["conversation.assistant"] = "Assistant",
        ["conversation.tool"] = "Tool",
        ["status.mode.plan"] = "plan",
        ["status.mode.act"] = "act",
        ["status.mode.orchestrate"] = "orchestrate",
        ["status.quota.unknown"] = "—",
        ["status.interactions.pending"] = "! {count} pending",
    };
}