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
    private readonly Func<bool>? _stillConsented;
    public ExtensionBoundary Boundary { get; }
    public IReadOnlyDictionary<string, string>? UiDescriptions => _extension.Manifest?.UiResources?
        .Where(pair => pair.Value.ContainsKey("tool." + _tool.Name + ".description"))
        .ToDictionary(pair => pair.Key, pair => pair.Value["tool." + _tool.Name + ".description"], StringComparer.Ordinal);

    public ExtensionTool(DiscoveredExtension extension, ExtensionToolManifest tool, string schemaJson,
        IExtensionRpcInvoker invoker, string workspaceRoot, Func<bool>? stillConsented = null)
    {
        _extension = extension ?? throw new ArgumentNullException(nameof(extension));
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _stillConsented = stillConsented;
        if (extension.Status != ExtensionDiscoveryStatus.Ready || extension.Manifest?.Id is not { Length: > 0 } id
            || extension.Manifest.Version is not { Length: > 0 } version || tool.Name is not { Length: > 0 } name)
            throw new ArgumentException("Only a ready extension with a valid tool declaration can be registered.");
        var tags = tool.Tags is { Count: > 0 } declaredTags ? declaredTags : ["extension", id];
        var scope = extension.Trust == TrustLevel.Project ? ScopeLevel.Project : ScopeLevel.User;
        Descriptor = new ToolDescriptor(new ToolId("ext." + id + "." + name),
            "Extension tool from " + extension.Manifest.DisplayName + ": " + name,
            new InputSchema(schemaJson), tags, readOnly: false, destructive: false,
            ToolRisk.High, new ComponentSource(SourceKind.Extension, scope, extension.Trust, id, version,
                extension.PackageSha256),
            ToolProtection.None, EffectClass.NonIdempotent);
        Boundary = new ExtensionBoundary(extension.Manifest.Requests?.Permissions ?? new ExtensionPermissionManifest(),
            id, workspaceRoot, Descriptor.Source);
    }

    public ToolDescriptor Descriptor { get; }

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (_stillConsented is not null && !_stillConsented())
            return new PreparationRejected("Extension consent or package identity changed.", null,
                ToolErrorCode.PermissionDenied);
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
        if (_stillConsented is not null && !_stillConsented())
            return new ToolResult("Extension consent or package identity changed.", null, null, 0, false,
                EffectOutcome.None, isError: true, ToolErrorCode.PermissionDenied);
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
            ArtifactRef? proposal = null;
            var summary = "Extension completed in a private copy; the workspace is unchanged.";
            var preview = resultJson;
            if (root.TryGetProperty("proposal", out var proposalNode))
            {
                if (proposalNode.ValueKind != JsonValueKind.Object
                    || !proposalNode.TryGetProperty("reference", out var referenceNode))
                    throw new InvalidDataException("The extension returned an invalid protected proposal reference.");
                proposal = JsonSerializer.Deserialize<ArtifactRef>(referenceNode.GetRawText())
                    ?? throw new InvalidDataException("The extension proposal reference is invalid.");
                if (proposal.Kind != ArtifactKind.Patch || proposal.Sensitivity != Sensitivity.Sensitive
                    || proposal.Size <= 0 || proposal.Hash.Algorithm != "sha256")
                    throw new InvalidDataException("The extension proposal reference has an invalid classification.");
                if (proposalNode.TryGetProperty("summary", out var summaryNode)
                    && summaryNode.ValueKind == JsonValueKind.String)
                    summary = summaryNode.GetString() ?? summary;
                if (proposalNode.TryGetProperty("paths", out var pathsNode)
                    && pathsNode.ValueKind == JsonValueKind.Array)
                {
                    var paths = pathsNode.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString() ?? "").Where(value => value.Length > 0).ToArray();
                    preview += "\nProposed paths: " + string.Join(", ", paths)
                        + ". Read the attached proposal with artifact.read, then use filesystem.write for each accepted file; no workspace changes have been published yet.";
                }
            }
            return new ToolResult(summary, preview, proposal, preview.Length, proposal is not null,
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
