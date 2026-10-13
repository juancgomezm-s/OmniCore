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
    public ExtensionBoundary Boundary { get; }
    public IReadOnlyDictionary<string, string>? UiDescriptions => _extension.Manifest?.UiResources?
        .Where(pair => pair.Value.ContainsKey("tool." + _tool.Name + ".description"))
        .ToDictionary(pair => pair.Key, pair => pair.Value["tool." + _tool.Name + ".description"], StringComparer.Ordinal);

    public ExtensionTool(DiscoveredExtension extension, ExtensionToolManifest tool, string schemaJson,
        IExtensionRpcInvoker invoker, string workspaceRoot)
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
            ToolRisk.High, new ComponentSource(SourceKind.Extension, ScopeLevel.Project, extension.Trust, id, version,
                extension.PackageSha256),
            ToolProtection.None, EffectClass.NonIdempotent);
        Boundary = new ExtensionBoundary(extension.Manifest.Requests?.Permissions ?? new ExtensionPermissionManifest(),
            id, workspaceRoot, Descriptor.Source);
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
        var executable = Path.GetFullPath(Path.IsPathRooted(runtime.Command!)
            ? runtime.Command! : Path.Combine(_extension.Directory, runtime.Command!));
        var process = new ProcessClaim(executable, runtime.Args ?? [], "ExtensionTool",
            NetworkRequired: false, WorkingDirectory: context.WorkspaceRoot);
        var permissions = _extension.Manifest.Requests?.Permissions;
        var claims = new ResourceClaims(permissions?.Paths?.Read ?? [], permissions?.Paths?.Write ?? [],
            (permissions?.Network ?? []).Select(ParseNetwork).ToArray(), process, []);
        var boundary = Boundary.Evaluate(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.NonIdempotent, claims, ToolRisk.High, reconciliation: null));
        if (!boundary.Allowed)
            return new PreparationRejected(boundary.Reason ?? "Extension intent exceeds requests.permissions.", null,
                ToolErrorCode.PermissionDenied);
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
            var response = await _invoker.InvokeAsync(_extension, "tools/call", parameters, intent.Intent.Claims, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version)
                || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
                || !root.TryGetProperty("result", out var result))
                throw new InvalidDataException("The extension returned an invalid JSON-RPC response.");
            var resultJson = result.GetRawText();
            return new ToolResult("Extension tool completed.", resultJson, null, resultJson.Length, false,
                EffectOutcome.Applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or NotSupportedException
            or TimeoutException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return new ToolResult("Extension tool failed safely (" + ex.GetType().Name + ").", null, null, 0, false,
                EffectOutcome.Unknown, isError: true, ToolErrorCode.ToolFailure);
        }
    }

    private static NetworkGrant ParseNetwork(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var port))
            return new NetworkGrant(value[..separator], port);
        return new NetworkGrant(value, null);
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
