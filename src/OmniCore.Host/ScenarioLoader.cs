namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Engine;
using YamlDotNet.Serialization;

/// <summary>
/// Carga un escenario de simulación desde YAML (ADR-0041 §1). Formato:
/// <code>
/// scenario: 1
/// name: multi-item-plan
/// session: { mode: act }            # act | plan | orchestrate
/// input: "objetivo del Run"
/// plan:
///   - add: { id: P1, text: "Inspeccionar", dependsOn: [] }
/// tasks:
///   - { id: T1, objective: "Explorar", links: [{ item: P1, role: implements }], dependsOn: [] }
/// turns:
///   root:
///     - tool: fake.write            # effect: applied | read; answer: approve | deny (si hay Ask)
///       effect: applied
///       answer: approve
///     - complete: done
/// permissions:
///   - { tool: fake.write, decision: ask }   # allow | ask | deny
/// fault: { atTool: fake.write }      # crash inyectado tras el Started
/// planApproval: approve_execute      # approve_execute | approve_only | reject (modo plan)
/// expect:
///   run: Completed
///   plan: { P1: Completed }
/// </code>
/// Un campo desconocido o mal tipado falla con <see cref="ScenarioFormatException"/>: un escenario
/// que no se entiende nunca se ejecuta a medias.
/// </summary>
public static class ScenarioLoader
{
    private static readonly HashSet<string> TopLevel = new(StringComparer.Ordinal)
    {
        "scenario", "name", "session", "input", "plan", "tasks", "turns", "permissions", "fault",
        "planApproval", "stallThresholdTurns", "expect",
    };

    public static SimulationScenario Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        Dictionary<object, object>? root;
        try
        {
            root = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ScenarioFormatException("YAML inválido: " + ex.Message, "scenario.yaml_invalid", ("detail", ex.Message));
        }

        if (root is null)
        {
            throw new ScenarioFormatException("el escenario está vacío", "scenario.empty");
        }

        foreach (var key in root.Keys.Select(k => k.ToString()!))
        {
            if (!TopLevel.Contains(key))
            {
                throw new ScenarioFormatException("campo desconocido: " + key, "scenario.unknown_field", ("field", key));
            }
        }

        var name = Str(root, "name") ?? "yaml-scenario";
        var mode = ParseMode(Str(Map(root, "session"), "mode"));
        var input = Str(root, "input") ?? throw new ScenarioFormatException("falta 'input'", "scenario.missing_input");

        var plan = List(root, "plan").Select(ParsePlanItem).ToArray();
        var tasks = List(root, "tasks").Select(ParseTask).ToArray();

        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        foreach (var kv in Map(root, "turns"))
        {
            turns[kv.Key.ToString()!] = AsList(kv.Value, "turns." + kv.Key).Select(ParseTurn).ToArray();
        }

        var permissions = new Dictionary<string, PermissionDecision>();
        foreach (var entry in List(root, "permissions"))
        {
            var map = AsMap(entry, "permissions[]");
            var tool = Str(map, "tool") ?? throw new ScenarioFormatException("permiso sin 'tool'", "scenario.permission_missing_tool");
            permissions[tool] = ParseDecision(Str(map, "decision"));
        }

        var fault = Str(Map(root, "fault"), "atTool");
        var expect = Map(root, "expect");
        var expectedPlan = new Dictionary<string, string>();
        foreach (var kv in Map(expect, "plan"))
        {
            expectedPlan[kv.Key.ToString()!] = kv.Value?.ToString() ?? "";
        }

        var planApproval = Str(root, "planApproval");
        if (planApproval is not null && planApproval is not ("approve_execute" or "approve_only" or "reject"))
        {
            throw new ScenarioFormatException("planApproval inválido: " + planApproval, "scenario.invalid_plan_approval", ("value", planApproval));
        }

        int? stall = null;
        if (Str(root, "stallThresholdTurns") is { } stallText)
        {
            stall = int.TryParse(stallText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1
                ? n
                : throw new ScenarioFormatException("stallThresholdTurns debe ser un entero ≥ 1", "scenario.invalid_stall_threshold");
        }

        return new SimulationScenario(name, mode, input, plan, tasks, turns, permissions,
            Str(expect, "run") ?? "Completed", expectedPlan, fault)
        {
            PlanApproval = planApproval,
            StallThresholdTurns = stall,
        };
    }

    private static SimulatedPlanMutation ParsePlanItem(object entry)
    {
        var add = Map(AsMap(entry, "plan[]"), "add");
        var id = Str(add, "id") ?? throw new ScenarioFormatException("item de plan sin 'id'", "scenario.plan_item_missing_id");
        return new SimulatedPlanMutation(id, Str(add, "text") ?? id, Strings(add, "dependsOn"));
    }

    private static SimulatedTask ParseTask(object entry)
    {
        var map = AsMap(entry, "tasks[]");
        var id = Str(map, "id") ?? throw new ScenarioFormatException("task sin 'id'");
        var links = List(map, "links").Select(link =>
        {
            var l = AsMap(link, "links[]");
            return new SimulatedLink(Str(l, "item") ?? throw new ScenarioFormatException("link sin 'item'", "scenario.link_missing_item"),
                Str(l, "role") ?? "implements");
        }).ToArray();
        return new SimulatedTask(id, links, Strings(map, "dependsOn"), Str(map, "objective") ?? id);
    }

    private static SimulatedTurnAction ParseTurn(object entry)
    {
        var map = AsMap(entry, "turns[]");
        if (Str(map, "complete") is not null)
        {
            return SimulatedTurnAction.DoneMarker();
        }

        var tool = Str(map, "tool") ?? throw new ScenarioFormatException("turno sin 'tool' ni 'complete'", "scenario.turn_missing_tool_or_complete");
        var answer = Str(map, "answer");
        if (answer is not null && answer is not ("approve" or "deny"))
        {
            throw new ScenarioFormatException("answer inválido: " + answer, "scenario.invalid_answer", ("value", answer));
        }

        return SimulatedTurnAction.ToolCall(tool, Str(map, "effect") ?? "read", answer);
    }

    private static RunMode ParseMode(string? mode) => mode switch
    {
        null or "act" => RunMode.Act,
        "plan" => RunMode.Plan,
        "orchestrate" => RunMode.Orchestrate,
        _ => throw new ScenarioFormatException("modo inválido: " + mode, "scenario.invalid_mode", ("value", mode!)),
    };

    private static PermissionDecision ParseDecision(string? decision) => decision switch
    {
        "allow" => PermissionDecision.Allow,
        "ask" => PermissionDecision.Ask,
        "deny" => PermissionDecision.Deny,
        _ => throw new ScenarioFormatException("decisión inválida: " + decision, "scenario.invalid_decision", ("value", decision!)),
    };

    private static string? Str(Dictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? value.ToString() : null;

    private static Dictionary<object, object> Map(Dictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? AsMap(value, key) : new Dictionary<object, object>();

    private static IReadOnlyList<object> List(Dictionary<object, object> map, string key) =>
        map.TryGetValue(key, out var value) && value is not null ? AsList(value, key) : Array.Empty<object>();

    private static IReadOnlyList<string> Strings(Dictionary<object, object> map, string key) =>
        List(map, key).Select(v => v.ToString()!).ToArray();

    private static Dictionary<object, object> AsMap(object value, string where) =>
        value as Dictionary<object, object> ?? throw new ScenarioFormatException(where + " debe ser un mapa", "scenario.expected_map", ("where", where));

    private static IReadOnlyList<object> AsList(object value, string where) =>
        value as List<object> ?? throw new ScenarioFormatException(where + " debe ser una lista", "scenario.expected_list", ("where", where));
}

/// <summary>El YAML de un escenario no respeta el formato (error tipado, spec §71).</summary>
public sealed class ScenarioFormatException : FormatException
{
    public LocalizedText UserMessage { get; }

    public ScenarioFormatException(string message) : base("escenario inválido: " + message)
    {
        UserMessage = LocalizedText.Of("scenario.invalid", "detail", message);
    }

    public ScenarioFormatException(string message, string resourceKey, params (string Name, string Value)[] args) : base("escenario inválido: " + message)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (name, value) in args)
        {
            dict[name] = value;
        }
        UserMessage = new LocalizedText(resourceKey, dict);
    }
}
