namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Tool Core `plan.propose` (ADR-0016 §3): el modelo propone mutaciones del plan; la tool
/// valida la SINTAXIS de la mutación y la declara como efecto Reconcilable con la mutación
/// canónica en el summary. La APLICACIÓN la decide PlanService: el engine (que posee las
/// proyecciones plan/tasks/lanes) invoca PlanService.Apply cuando un `plan.propose` termina
/// con éxito y emite los eventos aceptados. El modelo nunca edita el plan directamente
/// (INV-017); tampoco el `ExecuteAsync` materializa eventos (los emite el engine).
/// </summary>
public sealed class PlanProposeTool : ITool
{
    private readonly ToolDescriptor _descriptor;

    public PlanProposeTool(PlanService planService)
    {
        ArgumentNullException.ThrowIfNull(planService);
        _descriptor = new ToolDescriptor(
            new ToolId("plan.propose"),
            "Proposes a Plan mutation for the runtime to validate and apply (read-only for the model).",
            new InputSchema("{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"},"
                + "\"itemId\":{\"type\":\"string\"},\"reason\":{\"type\":\"string\"}}}"),
            new string[] { "core" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
            ResourceClaims.Empty(), ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // 1. Sintaxis: los argumentos deben ser una mutación válida (kind conocido).
        var args = ArgsJson2.Parse(intent.Intent.NormalizedArgumentsJson);
        var kind = args.TryGetValue("kind", out var k) ? k : null;
        if (kind is null || ParseKind(kind!) is null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "plan.propose: 'kind' requerido y conocido (start|complete|block|unblock|fail|skip|cancel|revise)"));
        }

        // 2. Semántica básica: los kinds con item requieren itemId.
        var itemId = args.TryGetValue("itemId", out var id) ? id : null;
        var reason = args.TryGetValue("reason", out var r) ? r : null;
        if (RequiresItem(kind!) && (itemId is null || itemId!.Length == 0))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "plan.propose: " + kind! + " requiere 'itemId'"));
        }

        // 3. Declaración: devuelve la mutación canónica serializada como efecto aplicado.
        //    El engine invoca PlanService.Apply con esta mutación (INV-017 sigue intacto).
        var mutationJson = Mutations.CanonicalJson(kind!, itemId, reason);
        return System.Threading.Tasks.Task.FromResult(new ToolResult(
            "plan.propose: mutación declarada", mutationJson, null, mutationJson.Length, false,
            EffectOutcome.Applied));
    }

    public static PlanMutationKind? ParseKind(string kind)
    {
        switch (kind)
        {
            case "start": return PlanMutationKind.Start;
            case "complete": return PlanMutationKind.Complete;
            case "block": return PlanMutationKind.Block;
            case "unblock": return PlanMutationKind.Unblock;
            case "fail": return PlanMutationKind.Fail;
            case "skip": return PlanMutationKind.Skip;
            case "cancel": return PlanMutationKind.Cancel;
            case "revise": return PlanMutationKind.Revise;
            default: return null;
        }
    }

    private static bool RequiresItem(string kind) =>
        kind != "add" && kind != "reorder";
}

/// <summary>Serialización canónica de las mutaciones que declara plan.propose.</summary>
public sealed class Mutations
{
    public static string CanonicalJson(string kind, string? itemId, string? reason)
    {
        var parts = new List<string>();
        parts.Add("\"kind\":\"" + kind + "\"");
        if (itemId is not null && itemId!.Length > 0)
        {
            parts.Add("\"itemId\":\"" + itemId! + "\"");
        }

        if (reason is not null && reason!.Length > 0)
        {
            parts.Add("\"reason\":" + Json.Esc(reason!));
        }

        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    public static bool RequiresItem(PlanMutationKind kind)
    {
        switch (kind)
        {
            case PlanMutationKind.Start:
            case PlanMutationKind.Complete:
            case PlanMutationKind.Block:
            case PlanMutationKind.Unblock:
            case PlanMutationKind.Fail:
            case PlanMutationKind.Skip:
            case PlanMutationKind.Cancel:
            case PlanMutationKind.Revise:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Construye la PlanMutation desde el summary canónico, resolviendo el itemId simbólico
    /// (p. ej. "P1") contra el mapa del run. Devuelve null si el id no se resuelve.
    /// </summary>
    public static PlanMutation? ResolveDeclared(string json, Dictionary<string, PlanItemId> symbolicItems)
    {
        var args = ArgsJson2.Parse(json);
        var kind = args.TryGetValue("kind", out var k) ? k : null;
        var itemId = args.TryGetValue("itemId", out var id) ? id : null;
        var reason = args.TryGetValue("reason", out var r) ? r : null;
        var parsedKind = PlanProposeTool.ParseKind(kind is null ? "" : kind!);
        if (parsedKind is PlanMutationKind pk)
        {
            return ResolveWithKind(json, pk, symbolicItems);
        }

        return null;
    }

    private static PlanMutation? ResolveWithKind(string json, PlanMutationKind pk,
        Dictionary<string, PlanItemId> symbolicItems)
    {
        var args = ArgsJson2.Parse(json);
        var itemId = args.TryGetValue("itemId", out var id) ? id : null;
        var reason = args.TryGetValue("reason", out var r) ? r : null;

        PlanItemId? item = null;
        if (RequiresItem(pk))
        {
            if (itemId is null)
            {
                return null;
            }

            if (symbolicItems.TryGetValue(itemId!, out var resolved))
            {
                item = resolved!;
            }
            else
            {
                try
                {
                    item = PlanItemId.Parse(itemId!);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        var cause = MutationCause.Model;
        if (item is null)
        {
            return RulesWithoutItem(pk, reason);
        }

        var it = item;
        var rs = reason ?? "";
        switch (pk)
        {
            case PlanMutationKind.Start: return PlanMutation.Start(it, cause);
            case PlanMutationKind.Complete: return PlanMutation.Complete(it, cause, rs);
            case PlanMutationKind.Block: return PlanMutation.Block(it, cause, rs.Length == 0 ? "bloqueado" : rs);
            case PlanMutationKind.Unblock: return PlanMutation.Unblock(it, cause, rs);
            case PlanMutationKind.Fail: return PlanMutation.Fail(it, cause, rs.Length == 0 ? "fallo" : rs);
            case PlanMutationKind.Skip: return PlanMutation.Skip(it, cause, rs);
            case PlanMutationKind.Cancel: return PlanMutation.Cancel(it, cause, rs);
            case PlanMutationKind.Revise: return PlanMutation.Revise(it, cause, rs);
            default: return null;
        }
    }

    private static PlanMutation? RulesWithoutItem(PlanMutationKind pk, string? reason)
    {
        var rs = reason is null ? "" : reason!;
        switch (pk)
        {
            case PlanMutationKind.Start:
            case PlanMutationKind.Complete:
            case PlanMutationKind.Block:
            case PlanMutationKind.Unblock:
            case PlanMutationKind.Fail:
            case PlanMutationKind.Skip:
            case PlanMutationKind.Cancel:
            case PlanMutationKind.Revise:
                return null;
            default:
                return null;
        }
    }
}

/// <summary>Mini parser de objetos JSON planos para argumentos de tools.</summary>
internal sealed class ArgsJson2
{
    public static Dictionary<string, string> Parse(string json)
    {
        var map = new Dictionary<string, string>();
        if (json is null || json.Length == 0)
        {
            return map;
        }

        var body = json.Trim();
        if (body.Length >= 2 && body[0] == '{')
        {
            body = body.Substring(1, body.Length - 2);
        }

        var i = 0;
        while (i < body.Length)
        {
            var colon = body.IndexOf(':', i);
            if (colon < 0)
            {
                break;
            }

            var key = body.Substring(i, colon - i).Trim().Trim('"');
            var after = colon + 1;
            while (after < body.Length && body[after] == ' ')
            {
                after += 1;
            }

            if (after >= body.Length)
            {
                break;
            }

            if (body[after] == '"')
            {
                var end = FindStringEnd(body, after);
                map[key] = Json.Unesc(body.Substring(after + 1, Math.Max(0, end - after - 1)));
                i = NextComma(body, end);
            }
            else
            {
                var comma = body.IndexOf(',', after);
                map[key] = comma < 0 ? body.Substring(after).Trim() : body.Substring(after, comma - after).Trim();
                i = comma < 0 ? body.Length : comma + 1;
            }
        }

        return map;
    }

    private static int FindStringEnd(string s, int fromQuote)
    {
        for (var i = fromQuote + 1; i < s.Length; i++)
        {
            if (s[i] == '"' && s[i - 1] != '\\')
            {
                return i;
            }
        }

        return s.Length;
    }

    private static int NextComma(string s, int from)
    {
        var comma = s.IndexOf(',', from);
        return comma < 0 ? s.Length : comma + 1;
    }
}

/// <summary>Escapado JSON mínimo compartido.</summary>
internal sealed class Json
{
    public static string Esc(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    public static string Unesc(string value) =>
        value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
}