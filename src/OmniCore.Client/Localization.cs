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

    /// <summary>
    /// Resuelve un texto que llega en el formato estable del servidor, <c>clave</c> o
    /// <c>clave(arg=valor,...)</c> (p. ej. el error de un ack). Si la clave no existe en los
    /// recursos, devuelve el texto tal cual: nunca inventa una frase.
    /// </summary>
    public string ResolveWire(string text)
    {
        var open = text.IndexOf('(');
        var key = open < 0 ? text : text[..open];
        if (!_es.ContainsKey(key) && !_en.ContainsKey(key))
        {
            return text;
        }

        var args = new Dictionary<string, string>();
        if (open >= 0 && text.EndsWith(')'))
        {
            foreach (var part in text[(open + 1)..^1].Split(','))
            {
                var eq = part.IndexOf('=');
                if (eq > 0)
                {
                    args[part[..eq]] = part[(eq + 1)..];
                }
            }
        }

        return Resolve(key, args);
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
        ["interaction.permission.allow_workspace"] = "Permitir en este workspace",
        ["permissions.heading"] = "Permisos del workspace:",
        ["permissions.empty"] = "No hay grants persistentes para este workspace/Run.",
        ["permissions.revoked"] = "Grant revocado.",
        ["permissions.not_found"] = "No existe ese grant en este workspace.",
        ["permissions.usage"] = "Uso: omni permissions list | revoke <id>",
        ["permissions.help"] = "  omni permissions list|revoke <id>   Grants del workspace actual",
        ["commands.context.help"] = "  omni /context                     Último snapshot persistido del contexto",
        ["commands.tools.help"] = "  omni /tools                       Catálogo efectivo y decisiones de policy",
        ["commands.explain.help"] = "  omni /explain <ruta>              Explica un archivo o el repositorio",
        ["interaction.plan_approval.approve_execute"] = "Aprobar y ejecutar",
        ["interaction.plan_approval.approve_only"] = "Aprobar sin ejecutar",
        ["interaction.plan_approval.continue_planning"] = "Seguir planificando",
        ["interaction.weak_sandbox.title"] = "Sandbox fuerte no disponible",
        ["interaction.weak_sandbox.warning"] = "El proceso podrá acceder a rutas fuera del workspace. ¿Permitir?",
        ["interaction.weak_sandbox.once"] = "Permitir solo esta vez",
        ["interaction.weak_sandbox.run"] = "Permitir durante este Run",
        ["interaction.weak_sandbox.deny"] = "Denegar",
        ["interaction.permission.allow_once"] = "Permitir una vez",
        ["interaction.permission.allow_run"] = "Permitir durante este Run",
        ["interaction.permission.allow_workspace"] = "Permitir en este workspace",
        ["interaction.permission.deny"] = "Denegar",
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
        ["interaction.acceptance.title"] = "¿Aceptas el resultado?",
        ["interaction.acceptance.accept"] = "Aceptar",
        ["interaction.acceptance.revise"] = "Pedir cambios",
        ["interaction.acceptance.choose"] = "Elige una opción: ",
        ["coder.policy.observeOnly"] = "Política del modelo: modelo sin clasificar en ObserveOnly; las escrituras están deshabilitadas.",
        ["coder.completion.invalid"] = "omni act: el turno no produjo una propuesta de finalización válida.",
        ["completion.gates.none"] = "Nota: no se configuró gate Build/Test; solo se ejecutaron los gates de Plan/Task.",
        ["interaction.plan_approval.reject"] = "Seguir planificando",
        ["interaction.budget_exceeded.title"] = "Presupuesto agotado",
        ["doctor.heading"] = "omni doctor — diagnóstico de M2",
        ["doctor.config"] = "Configuración: {path}",
        ["doctor.models"] = "Modelos disponibles:",
        ["doctor.runtime"] = "Runtime cableado:",
        ["doctor.status.configured"] = "Estado: modelo configurado ✓",
        ["doctor.status.unconfigured"] = "Estado: sin modelo configurado (ejecuta omni ask para ver la guía)",
        ["scenario.invalid"] = "Escenario inválido: {detail}",
        ["models.noneConfigured"] = "No hay ningún modelo configurado.",
        ["config.unknownKey"] = "clave desconocida: {path}",
        ["config.conflictingAuth"] = "auth y authRef no se pueden especificar a la vez: {path}",
        ["config.legacyAuthDeprecated"] = "el formato auth de providers.yaml está obsoleto; usa authRef.",
        ["config.wrongType"] = "tipo incorrecto: {path}",
        ["config.expectedArgv"] = "se esperaba un argv no vacío (el primer elemento es el ejecutable): {path}",
        ["config.missingRequired"] = "falta un campo obligatorio: {path}",
        ["config.outOfRange"] = "valor fuera de rango: {path}",
        ["config.invalidUrl"] = "URL inválida: {path}",
        ["config.expectedMapping"] = "se esperaba un mapa YAML: {path}",
        ["config.yamlSyntax"] = "error de sintaxis YAML en línea {line}, columna {column}: {path}",
        ["config.deserializeFailed"] = "no se pudo cargar el valor tipado: {path}",
        ["config.unknownProvider"] = "provider no definido: {path}",
        ["config.unknownModelAlias"] = "el alias de modelo debe existir en la configuración del usuario: {path}",
        ["config.forbiddenRepoKey"] = "esta clave no se permite en la configuración del repo: {path}",
        ["config.forbiddenRepoFile"] = "este archivo no se permite en .omnicore/: {path}",
        ["config.permissionCanOnlyRestrict"] = "el repo solo puede establecer deny o ask: {path}",
        ["scenario.yaml_invalid"] = "Escenario inválido: YAML inválido: {detail}",
        ["scenario.empty"] = "Escenario inválido: el escenario está vacío",
        ["scenario.unknown_field"] = "Escenario inválido: campo desconocido: {field}",
        ["scenario.missing_input"] = "Escenario inválido: falta 'input'",
        ["scenario.permission_missing_tool"] = "Escenario inválido: permiso sin 'tool'",
        ["scenario.invalid_plan_approval"] = "Escenario inválido: planApproval inválido: {value}",
        ["scenario.invalid_stall_threshold"] = "Escenario inválido: stallThresholdTurns debe ser un entero ≥ 1",
        ["scenario.plan_item_missing_id"] = "Escenario inválido: ítem de plan sin 'id'",
        ["scenario.link_missing_item"] = "Escenario inválido: link sin 'item'",
        ["scenario.turn_missing_tool_or_complete"] = "Escenario inválido: turno sin 'tool' ni 'complete'",
        ["scenario.invalid_answer"] = "Escenario inválido: answer inválido: {value}",
        ["scenario.invalid_mode"] = "Escenario inválido: modo inválido: {value}",
        ["scenario.invalid_decision"] = "Escenario inválido: decisión inválida: {value}",
        ["scenario.expected_map"] = "Escenario inválido: {where} debe ser un mapa",
        ["scenario.expected_list"] = "Escenario inválido: {where} debe ser una lista",
    };

    private static readonly Dictionary<string, string> _en = new()
    {
        ["interaction.permission.allow_once"] = "Allow once",
        ["interaction.permission.allow_run"] = "Allow for this Run",
        ["interaction.permission.deny"] = "Deny",
        ["interaction.permission.allow_workspace"] = "Allow for this workspace",
        ["permissions.heading"] = "Workspace permissions:",
        ["permissions.empty"] = "No persistent grants for this workspace/Run.",
        ["permissions.revoked"] = "Grant revoked.",
        ["permissions.not_found"] = "No grant with that id exists in this workspace.",
        ["permissions.usage"] = "Usage: omni permissions list | revoke <id>",
        ["permissions.help"] = "  omni permissions list|revoke <id>   Grants for the current workspace",
        ["commands.context.help"] = "  omni /context                     Last persisted context snapshot",
        ["commands.tools.help"] = "  omni /tools                       Effective catalog and policy decisions",
        ["commands.explain.help"] = "  omni /explain <path>              Explain a file or the repository",
        ["interaction.plan_approval.approve_execute"] = "Approve and execute",
        ["interaction.plan_approval.approve_only"] = "Approve without executing",
        ["interaction.plan_approval.continue_planning"] = "Continue planning",
        ["interaction.weak_sandbox.title"] = "Strong sandbox unavailable",
        ["interaction.weak_sandbox.warning"] = "The process may access paths outside the workspace. Allow it?",
        ["interaction.weak_sandbox.once"] = "Allow once",
        ["interaction.weak_sandbox.run"] = "Allow for this Run",
        ["interaction.weak_sandbox.deny"] = "Deny",
        ["interaction.permission.allow_once"] = "Allow once",
        ["interaction.permission.allow_run"] = "Allow for this Run",
        ["interaction.permission.allow_workspace"] = "Allow in this workspace",
        ["interaction.permission.deny"] = "Deny",
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
        ["interaction.acceptance.title"] = "Accept the result?",
        ["interaction.acceptance.accept"] = "Accept",
        ["interaction.acceptance.revise"] = "Request changes",
        ["interaction.acceptance.choose"] = "Choose an option: ",
        ["coder.policy.observeOnly"] = "Model policy: unclassified model uses ObserveOnly; file writes are disabled.",
        ["coder.completion.invalid"] = "omni act: the turn did not produce a valid completion proposal.",
        ["completion.gates.none"] = "Note: no Build/Test gate was configured; only Plan/Task completion gates ran.",
        ["interaction.plan_approval.reject"] = "Continue planning",
        ["interaction.budget_exceeded.title"] = "Budget exhausted",
        ["doctor.heading"] = "omni doctor — M2 diagnostics",
        ["doctor.config"] = "Configuration: {path}",
        ["doctor.models"] = "Available models:",
        ["doctor.runtime"] = "Wired runtime:",
        ["doctor.status.configured"] = "Status: model configured ✓",
        ["doctor.status.unconfigured"] = "Status: no model configured (run omni ask for guidance)",
        ["scenario.invalid"] = "Invalid scenario: {detail}",
        ["models.noneConfigured"] = "No model is configured.",
        ["config.unknownKey"] = "unknown key: {path}",
        ["config.conflictingAuth"] = "auth and authRef cannot both be specified: {path}",
        ["config.legacyAuthDeprecated"] = "the providers.yaml auth format is deprecated; use authRef.",
        ["config.wrongType"] = "wrong type: {path}",
        ["config.expectedArgv"] = "expected a non-empty argv (first item is executable): {path}",
        ["config.missingRequired"] = "required field is missing: {path}",
        ["config.outOfRange"] = "value is out of range: {path}",
        ["config.invalidUrl"] = "invalid URL: {path}",
        ["config.expectedMapping"] = "expected a YAML mapping: {path}",
        ["config.yamlSyntax"] = "YAML syntax error on line {line}, column {column}: {path}",
        ["config.deserializeFailed"] = "could not load typed value: {path}",
        ["config.unknownProvider"] = "provider is not defined: {path}",
        ["config.unknownModelAlias"] = "model alias must be defined in user configuration: {path}",
        ["config.forbiddenRepoKey"] = "this key is forbidden in repository configuration: {path}",
        ["config.forbiddenRepoFile"] = "this file is forbidden under .omnicore/: {path}",
        ["config.permissionCanOnlyRestrict"] = "repository config may only set deny or ask: {path}",
        ["scenario.yaml_invalid"] = "Invalid scenario: invalid YAML: {detail}",
        ["scenario.empty"] = "Invalid scenario: scenario is empty",
        ["scenario.unknown_field"] = "Invalid scenario: unknown field: {field}",
        ["scenario.missing_input"] = "Invalid scenario: missing 'input'",
        ["scenario.permission_missing_tool"] = "Invalid scenario: permission missing 'tool'",
        ["scenario.invalid_plan_approval"] = "Invalid scenario: invalid planApproval: {value}",
        ["scenario.invalid_stall_threshold"] = "Invalid scenario: stallThresholdTurns must be an integer ≥ 1",
        ["scenario.plan_item_missing_id"] = "Invalid scenario: plan item missing 'id'",
        ["scenario.link_missing_item"] = "Invalid scenario: link missing 'item'",
        ["scenario.turn_missing_tool_or_complete"] = "Invalid scenario: turn missing 'tool' or 'complete'",
        ["scenario.invalid_answer"] = "Invalid scenario: invalid answer: {value}",
        ["scenario.invalid_mode"] = "Invalid scenario: invalid mode: {value}",
        ["scenario.invalid_decision"] = "Invalid scenario: invalid decision: {value}",
        ["scenario.expected_map"] = "Invalid scenario: {where} must be a map",
        ["scenario.expected_list"] = "Invalid scenario: {where} must be a list",
    };
}