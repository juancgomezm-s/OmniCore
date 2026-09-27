namespace OmniCore.Protocol;

/// <summary>
/// Codifica un escenario de simulación de M1 a su forma wire (ADR-0013 §3, ADR-0041 §1):
/// solo strings y estructuras planas, portable net8/net10. El Host lo reconstruye como
/// SimulationScenario y ejecuta. El CLI nunca conoce SimulationScenario directamente.
/// </summary>
public sealed class ScenarioWire
{
    public static string Encode(string name, string mode, string input, string planJson, string tasksJson,
        string permissionsJson, string expectedRun, string? faultAtTool, bool fault)
    {
        var parts = new string[] {
            JsonObj.Field("name", name),
            JsonObj.Field("mode", mode),
            JsonObj.Field("input", input),
            JsonObj.FieldRaw("plan", planJson),
            JsonObj.FieldRaw("tasks", tasksJson),
            JsonObj.FieldRaw("permissions", permissionsJson),
            JsonObj.Field("expectedRun", expectedRun),
            fault ? JsonObj.Field("faultTool", faultAtTool ?? "") : JsonObj.FieldBool("fault", false),
        };
        var kept = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                kept.Add(part);
            }
        }

        return "{" + string.Join(",", kept.ToArray()) + "}";
    }

    /// <summary>Plan como JSON de lista; el Host lo parsea con su propio mini-parser.</summary>
    public static string EncodePlan(string[] ids, string[] texts)
    {
        var items = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            items[i] = "{\"id\":\"" + JsonObj.Escape(ids[i]) + "\",\"text\":\""
                + JsonObj.Escape(texts[i]) + "\"}";
        }

        return "[" + string.Join(",", items) + "]";
    }

    /// <summary>Permisos como JSON de objeto tool → allow/ask/deny.</summary>
    public static string EncodePermissions(string[] tools, string[] decisions)
    {
        var items = new string[tools.Length];
        for (var i = 0; i < tools.Length; i++)
        {
            items[i] = "\"" + JsonObj.Escape(tools[i]) + "\":\"" + decisions[i] + "\"";
        }

        return "{" + string.Join(",", items) + "}";
    }
}