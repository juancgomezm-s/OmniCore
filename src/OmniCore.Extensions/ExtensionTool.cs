namespace OmniCore.Extensions;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>One declared extension tool. Prepare is pure; the child is invoked after authorization.</summary>
public sealed class ExtensionTool : ITool
{
    private readonly DiscoveredExtension _extension;
    private readonly ExtensionToolManifest _tool;
    private readonly IExtensionRpcInvoker _invoker;

    public ExtensionTool(DiscoveredExtension extension, ExtensionToolManifest tool, string schemaJson,
        IExtensionRpcInvoker invoker)
    {
        _extension = extension ?? throw new ArgumentNullException(nameof(extension));
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        if (extension.Status != ExtensionDiscoveryStatus.Ready || extension.Manifest?.Id is not { Length: > 0 } id
            || extension.Manifest.Version is not { Length: > 0 } version || tool.Name is not { Length: > 0 } name)
            throw new ArgumentException("Only a ready extension with a valid tool declaration can be registered.");
        Descriptor = new ToolDescriptor(new ToolId("ext." + id + "." + name),
            "Extension tool from " + extension.Manifest.DisplayName + ": " + name,
            new InputSchema(schemaJson), ["extension", id], readOnly: false, destructive: false,
            ToolRisk.High, new ComponentSource(SourceKind.Extension, ScopeLevel.Project, extension.Trust, id, version),
            ToolProtection.None, EffectClass.NonIdempotent);
    }

    public ToolDescriptor Descriptor { get; }

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        try
        {
            using var _ = JsonDocument.Parse(call.NormalizedArgumentsJson);
        }
        catch (JsonException)
        {
            return new PreparationRejected("Extension tool arguments must be a JSON object.", null,
                ToolErrorCode.InvalidArguments);
        }
        var runtime = _extension.Manifest!.Runtime!;
        var process = new ProcessClaim(runtime.Command!, runtime.Args ?? [], "ExtensionTool",
            NetworkRequired: false, WorkingDirectory: _extension.Directory);
        var claims = new ResourceClaims([], [], [], process, []);
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.High, reconciliation: null));
    }

    public async Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!Equals(intent.Intent.ToolId, Descriptor.Id))
            return new ToolResult("Extension tool authorization did not match.", null, null, 0, false,
                EffectOutcome.None, isError: true, ToolErrorCode.ToolFailure);
        try
        {
            var parameters = "{\"name\":\"" + Escape(_tool.Name!) + "\""
                + ",\"arguments\":" + intent.Intent.NormalizedArgumentsJson + "}";
            var response = await _invoker.InvokeAsync(_extension, "tools/call", parameters, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version)
                || version.GetString() != "2.0" || !root.TryGetProperty("result", out var result))
                throw new InvalidDataException("The extension returned an invalid JSON-RPC response.");
            var resultJson = result.GetRawText();
            return new ToolResult("Extension tool completed.", resultJson, null, resultJson.Length, false,
                EffectOutcome.Applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or NotSupportedException)
        {
            return new ToolResult("Extension tool failed: " + ex.Message, null, null, 0, false,
                EffectOutcome.Unknown, isError: true, ToolErrorCode.ToolFailure);
        }
    }

    private static string Escape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < ' ') builder.Append("\\u").Append(((int)character).ToString("x4"));
                    else builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }
}
