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
        var args = new Dictionary<string, string>();
        if (argName is not null && argValue is not null) args[argName] = argValue;
        return Resolve(key, args);
    }

    /// <summary>Resuelve una clave y todos sus argumentos.</summary>
    public string Resolve(string key, IReadOnlyDictionary<string, string>? args = null)
    {
        var text = Resources(key);
        if (args is not null)
        {
            foreach (var pair in args)
            {
                text = text.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal);
            }
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
        ["run.created"] = "Run iniciado: {objective}",
        ["run.mode_changed"] = "Modo del Run: {mode}",
        ["run.awaiting_input"] = "El Run espera tu respuesta",
        ["run.completed.completed"] = "Run completado",
        ["run.completed.completedwithissues"] = "Run completado con incidencias",
        ["run.completed.planned"] = "Plan aprobado (sin ejecutar)",
        ["run.failed"] = "El Run falló: {cause}",
        ["run.cancelled"] = "Run cancelado",
        ["tool.requested"] = "Herramienta: {tool}",
        ["tool.failed"] = "La herramienta no se ejecutó: {cause}",
        ["sim.finished"] = "Simulación: Run terminó en {run} (exit {exit})",
        ["sim.resumed"] = "Resume: toolcall reconciliada sin duplicar efecto ({count})",
        ["interaction.permission.title"] = "Permiso requerido",
        ["interaction.plan_approval.title"] = "Aprobar el plan",
        ["interaction.plan_approval.reject"] = "Seguir planificando",
        ["interaction.budget_exceeded.title"] = "Presupuesto agotado",
        ["doctor.heading"] = "omni doctor — diagnóstico de M2",
        ["doctor.config"] = "Configuración: {path}",
        ["doctor.models"] = "Modelos disponibles:",
        ["doctor.runtime"] = "Runtime cableado:",
        ["doctor.status.configured"] = "Estado: modelo configurado ✓",
        ["doctor.status.unconfigured"] = "Estado: sin modelo configurado (ejecuta omni ask para ver la guía)",
        ["scenario.invalid"] = "Escenario inválido: {detail}",
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
        ["run.created"] = "Run started: {objective}",
        ["run.mode_changed"] = "Run mode: {mode}",
        ["run.awaiting_input"] = "The Run is waiting for your reply",
        ["run.completed.completed"] = "Run completed",
        ["run.completed.completedwithissues"] = "Run completed with issues",
        ["run.completed.planned"] = "Plan approved (not executed)",
        ["run.failed"] = "The Run failed: {cause}",
        ["run.cancelled"] = "Run cancelled",
        ["tool.requested"] = "Tool: {tool}",
        ["tool.failed"] = "The tool did not run: {cause}",
        ["sim.finished"] = "Simulation: Run ended in {run} (exit {exit})",
        ["sim.resumed"] = "Resume: tool call reconciled without duplicating the effect ({count})",
        ["interaction.permission.title"] = "Permission required",
        ["interaction.plan_approval.title"] = "Approve the plan",
        ["interaction.plan_approval.reject"] = "Continue planning",
        ["interaction.budget_exceeded.title"] = "Budget exhausted",
        ["doctor.heading"] = "omni doctor — M2 diagnostics",
        ["doctor.config"] = "Configuration: {path}",
        ["doctor.models"] = "Available models:",
        ["doctor.runtime"] = "Wired runtime:",
        ["doctor.status.configured"] = "Status: model configured ✓",
        ["doctor.status.unconfigured"] = "Status: no model configured (run omni ask for guidance)",
        ["scenario.invalid"] = "Invalid scenario: {detail}",
    };
}