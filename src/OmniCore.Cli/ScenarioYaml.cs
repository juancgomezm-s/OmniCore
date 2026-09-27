using OmniCore.Engine;
using OmniCore.Domain;

namespace OmniCore.Cli;

/// <summary>
/// Parser de escenarios de simulación de M1 (ADR-0041 §1). Soporta el subset YAML de los
/// escenarios deterministas: session/input/plan/tasks/permissions/expect. En M2, YamlDotNet
/// con schemas sustituye este parser (ADR-0039).
/// </summary>
public sealed class ScenarioYaml
{
    public string Name { get; }

    public string Raw { get; }

    public ScenarioYaml(string name, string raw)
    {
        Name = name;
        Raw = raw;
    }

    /// <summary>Parsea el YAML plano del escenario a un SimulationScenario.</summary>
    public static SimulationScenario Parse(string yamlText)
    {
        var lines = yamlText.Replace("\r\n", "\n").Split('\n');
        var mode = RunMode.Act;
        string input = string.Empty;
        var plan = new List<SimulatedPlanMutation>();
        var tasks = new List<SimulatedTask>();
        var permissions = new Dictionary<string, PermissionDecision>();
        var expectedRun = "Completed";
        string section = "meta";

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#"))
            {
                continue;
            }

            if (line.EndsWith(":") && !line.StartsWith("- "))
            {
                section = line.Substring(0, line.Length - 1).Trim();
                continue;
            }

            if (line.StartsWith("- "))
            {
                ParseListItem(section, line.Substring(2).Trim(), plan, tasks);
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = line.Substring(0, colon).Trim();
            var value = Unquote(line.Substring(colon + 1).Trim());
            if (section == "session" && key == "mode")
            {
                mode = value == "plan" ? RunMode.Plan : value == "orchestrate" ? RunMode.Orchestrate : RunMode.Act;
            }
            else if (section == "" && key == "input")
            {
                input = value;
            }
            else if (section == "expect" && key == "run")
            {
                expectedRun = value;
            }
            else if (section == "permissions" && key == "tool")
            {
                var tool = value;
                var decisionEnd = raw.IndexOf("decision:");
                if (decisionEnd >= 0)
                {
                    var decision = raw.Substring(decisionEnd + 9).Trim();
                    permissions[tool] = decision == "ask" ? PermissionDecision.Ask
                        : decision == "deny" ? PermissionDecision.Deny : PermissionDecision.Allow;
                }
            }
            else if (section == "" && key == "name")
            {
                // nombre del escenario: en la sección raíz antes de sub-secciones
            }
        }

        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        return new SimulationScenario(
            "yaml-scenario", mode, input.Length == 0 ? "objetivo de prueba" : input,
            plan.ToArray(), tasks.ToArray(), turns, permissions, expectedRun,
            new Dictionary<string, string>());
    }

    private static void ParseListItem(string section, string item, List<SimulatedPlanMutation> plan,
        List<SimulatedTask> tasks)
    {
        // "add: { id: P1, text: ..., dependsOn: [P1] }" → mutación; "id: T1, links: [...]" → task.
        if (section == "plan")
        {
            var id = Field(item, "id") ?? "P" + (plan.Count + 1);
            var text = Field(item, "text") ?? id;
            var deps = FieldList(item, "dependsOn");
            plan.Add(new SimulatedPlanMutation(id, text!, deps));
        }
        else if (section == "tasks")
        {
            var id = Field(item, "id") ?? "T" + (tasks.Count + 1);
            var links = ParseLinks(Field(item, "links"));
            var deps = FieldList(item, "dependsOn");
            tasks.Add(new SimulatedTask(id!, links, deps, id!));
        }
    }

    private static IReadOnlyList<SimulatedLink> ParseLinks(string? jsonLinks)
    {
        if (jsonLinks is null)
        {
            return new SimulatedLink[0];
        }

        // formato plano: [{item:P1, role:implements}]
        var result = new List<SimulatedLink>();
        var content = jsonLinks!.Replace("[", "").Replace("]", "").Replace("{", "").Replace("}", "");
        foreach (var seg in content.Split(','))
        {
            var segT = seg.Trim();
            var item = Field(segT, "item");
            var role = Field(segT, "role") ?? "implements";
            if (item is not null)
            {
                result.Add(new SimulatedLink(item!, role!));
            }
        }

        return result.ToArray();
    }

    private static string? Field(string text, string name)
    {
        var idx = text.IndexOf(name + ":", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var after = text.Substring(idx + name.Length + 1).Trim();
        var comma = after.IndexOf(',');
        var value = comma < 0 ? after : after.Substring(0, comma);
        var closeBracket = value.IndexOf('}');
        if (closeBracket >= 0)
        {
            value = value.Substring(0, closeBracket);
        }

        return Unquote(value.Trim());
    }

    private static IReadOnlyList<string> FieldList(string text, string name)
    {
        var idx = text.IndexOf(name + ":", StringComparison.Ordinal);
        if (idx < 0)
        {
            return new string[0];
        }

        var bracket = text.IndexOf('[', idx);
        if (bracket < 0)
        {
            return new string[0];
        }

        var end = text.IndexOf(']', bracket);
        if (end < 0)
        {
            return new string[0];
        }

        var content = text.Substring(bracket + 1, end - bracket - 1);
        return content.Split(',').Select(UnquoteComma).Where(s => s.Length > 0).ToArray();
    }

    private static string UnquoteComma(string s) => Unquote(s.Trim());

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
        {
            return text.Substring(1, text.Length - 2);
        }

        return text;
    }
}