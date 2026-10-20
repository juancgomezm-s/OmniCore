namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            new InputSchema("{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"start\",\"complete\",\"block\",\"unblock\",\"fail\",\"skip\",\"cancel\",\"revise\"],\"description\":\"Plan mutation to propose.\"},"
                + "\"itemId\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128,\"description\":\"Symbolic plan item id such as P1.\"},"
                + "\"reason\":{\"type\":\"string\",\"maxLength\":512,\"description\":\"Optional short reason.\"}},\"required\":[\"kind\",\"itemId\"],\"additionalProperties\":false}"),
            new string[] { "core" }, true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.None);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!TryParseArguments(call.NormalizedArgumentsJson, out _, out _, out _))
            return new PreparationRejected("plan.propose requires a known kind, a 1–128 character itemId, and an optional reason up to 512 characters.",
                null, ToolErrorCode.InvalidArguments);
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson, EffectClass.None,
            ResourceClaims.Empty(), ToolRisk.Low, null);
        return new Prepared(intent);
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        // 1. Sintaxis: los argumentos deben ser una mutación válida (kind conocido).
        if (!TryParseArguments(intent.Intent.NormalizedArgumentsJson, out var kind, out var itemId, out var reason))
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(
                "plan.propose: known kind, itemId, and bounded optional reason are required"));
        }
        // 3. Declaración: devuelve la mutación canónica serializada como efecto aplicado.
        //    El engine invoca PlanService.Apply con esta mutación (INV-017 sigue intacto).
        var mutationJson = Mutations.CanonicalJson(kind, itemId, reason);
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

    private static bool TryParseArguments(string json, out string kind, out string itemId, out string? reason)
    {
        kind = itemId = string.Empty;
        reason = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
                || properties.Any(property => property.Name is not ("kind" or "itemId" or "reason"))
                || !root.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("itemId", out var itemValue) || itemValue.ValueKind != JsonValueKind.String)
                return false;
            kind = kindValue.GetString() ?? string.Empty;
            itemId = itemValue.GetString() ?? string.Empty;
            if (ParseKind(kind) is null || string.IsNullOrWhiteSpace(itemId) || itemId.Length > 128) return false;
            if (root.TryGetProperty("reason", out var reasonValue))
            {
                if (reasonValue.ValueKind != JsonValueKind.String) return false;
                reason = reasonValue.GetString();
                if (reason is { Length: > 512 }) return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

}

/// <summary>Serialización canónica de las mutaciones que declara plan.propose.</summary>
public sealed class Mutations
{
    public static string CanonicalJson(string kind, string? itemId, string? reason)
    {
        return JsonSerializer.Serialize(new PlanMutationArguments(kind, itemId, reason),
            PlanMutationArgumentsJsonContext.Default.PlanMutationArguments);
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
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
                || properties.Any(property => property.Name is not ("kind" or "itemId" or "reason"))
                || !root.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("itemId", out var itemValue) || itemValue.ValueKind != JsonValueKind.String)
                return null;
            var kind = PlanProposeTool.ParseKind(kindValue.GetString() ?? string.Empty);
            if (kind is null) return null;
            var itemId = itemValue.GetString();
            if (string.IsNullOrWhiteSpace(itemId) || itemId.Length > 128) return null;
            string? reason = null;
            if (root.TryGetProperty("reason", out var reasonValue))
            {
                if (reasonValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return null;
                reason = reasonValue.ValueKind == JsonValueKind.String ? reasonValue.GetString() : null;
                if (reason is { Length: > 512 }) return null;
            }
            return ResolveWithValues(json, kind.Value, itemId, reason, symbolicItems);
        }
        catch (JsonException) { return null; }
    }

    private static PlanMutation? ResolveWithValues(string json, PlanMutationKind pk, string? itemId, string? reason,
        Dictionary<string, PlanItemId> symbolicItems)
    {
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

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(PlanMutationArguments))]
internal sealed partial class PlanMutationArgumentsJsonContext : JsonSerializerContext;

internal sealed record PlanMutationArguments(string kind, string? itemId, string? reason);

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
