namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Bounded catalog discovery. Results are candidates only; the next ToolPlan and the
/// ordinary capability and permission checks decide whether a result can be called.</summary>
public sealed class ToolSearchTool(FakeCatalog catalog) : ITool
{
    private readonly ToolDescriptor _descriptor = new(new ToolId("tool.search"),
        "Searches the registered tool catalog by id, description, and tags. Results do not grant permission.",
        new InputSchema("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"maxResults\":{\"type\":\"integer\"}},\"required\":[\"query\"],\"additionalProperties\":false}"),
        ["read"], true, false, ToolRisk.Low, ComponentSource.Core(), ToolProtection.Protected);

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        using var document = JsonDocument.Parse(call.NormalizedArgumentsJson);
        var root = document.RootElement;
        var query = root.TryGetProperty("query", out var queryValue) && queryValue.ValueKind == JsonValueKind.String
            ? queryValue.GetString()?.Trim() : null;
        var limit = 5;
        var limitText = "5";
        if (root.TryGetProperty("maxResults", out var limitValue))
        {
            limitText = DiagnosticValue(limitValue);
            if (limitValue.ValueKind != JsonValueKind.Number || !limitValue.TryGetInt32(out limit))
                return new PreparationRejected(ToolArgumentDiagnostic.Invalid("tool.error.search_bounds",
                    ("queryLength", (query?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("maxResults", limitText)), null, ToolErrorCode.InvalidArguments);
            limitText = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256 || limit is < 1 or > 8)
            return new PreparationRejected(ToolArgumentDiagnostic.Invalid("tool.error.search_bounds",
                ("queryLength", (query?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("maxResults", limitText)), null, ToolErrorCode.InvalidArguments);
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));

        static string DiagnosticValue(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number) return "invalid";
            var raw = value.GetRawText();
            if (value.TryGetInt32(out _)) return raw;
            return value.TryGetInt64(out _) || raw.Length > 32 ? "out-of-range" : "invalid";
        }
    }

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(intent.Intent.NormalizedArgumentsJson);
        var root = document.RootElement;
        var query = root.GetProperty("query").GetString()!.Trim();
        var limit = 5;
        if (root.TryGetProperty("maxResults", out var limitValue)
            && (limitValue.ValueKind != JsonValueKind.Number || !limitValue.TryGetInt32(out limit)))
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ToolErrorCode.InvalidArguments,
                ToolArgumentDiagnostic.Invalid("tool.error.search_bounds",
                    ("queryLength", query.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("maxResults", DiagnosticValue(limitValue)))));
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var matches = catalog.RegisteredTools()
            .Where(tool => tool.Descriptor.Id != Descriptor.Id && tool.Descriptor.Source.Trust != TrustLevel.Untrusted)
            .Where(tool => context.IsModelToolVisible?.Invoke(tool.Descriptor.Id.ToString()) ?? true)
            .Select(tool => new
            {
                id = tool.Descriptor.Id.ToString(),
                description = tool.Descriptor.Description.Length > 160
                    ? tool.Descriptor.Description[..160] + "…" : tool.Descriptor.Description,
                score = terms.Sum(term =>
                    (tool.Descriptor.Id.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ? 4 : 0)
                    + (tool.Descriptor.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                    + (tool.Descriptor.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 1 : 0))
            })
            .Where(item => item.score > 0)
            .Where(item => item.id.Length <= 160)
            .OrderByDescending(item => item.score).ThenBy(item => item.id, StringComparer.Ordinal)
            .Take(limit).Select(item => new { item.id, item.description }).ToArray();
        string preview;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("query", query);
                writer.WriteStartArray("matches");
                foreach (var match in matches)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", match.id);
                    writer.WriteString("description", match.description);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            preview = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        if (System.Text.Encoding.UTF8.GetByteCount(preview) > 8192)
            return System.Threading.Tasks.Task.FromResult(ToolResult.Error(ToolErrorCode.LimitExceeded,
                ToolArgumentDiagnostic.Invalid("tool.error.search_result_limit", ("maxBytes", "8192"))));
        return System.Threading.Tasks.Task.FromResult(new ToolResult("ok",
            preview, null, preview.Length, false, EffectOutcome.None));

        static string DiagnosticValue(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number) return "invalid";
            var raw = value.GetRawText();
            if (value.TryGetInt32(out _)) return raw;
            return value.TryGetInt64(out _) || raw.Length > 32 ? "out-of-range" : "invalid";
        }
    }
}
