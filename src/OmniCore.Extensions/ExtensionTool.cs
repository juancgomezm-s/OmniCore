namespace OmniCore.Extensions;

using System.Text.Json;
using System.Security.Cryptography;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>One declared extension tool. Prepare is pure; the child is invoked after authorization.</summary>
public sealed class ExtensionTool : ITool, IStagedExtensionBatchProducer, IExtensionBatchProposalValidator
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
    public bool IsTrustedPrivateWorkspaceWriter(ToolIntent intent) =>
        intent.ToolId.Equals(Descriptor.Id)
        && _extension.Manifest?.ManifestVersion == 1
        && _invoker is IStagedExtensionRpcInvoker
        && _extension.Manifest.Requests?.Permissions?.Paths?.Write is { Count: > 0 }
        && intent.Claims.Process?.Isolation == ProcessIsolation.PrivateExtensionWorkspace;

    public bool ValidateCurrentBatchProposal(ExtensionWriteBatchProposal proposal, string workspaceRoot)
    {
        if (_stillConsented is not null && !_stillConsented()) return false;
        var manifestPath = Path.Combine(_extension.Directory, ExtensionManifestLoader.ManifestFileName);
        if (_extension.Manifest?.ManifestVersion != 1 || !File.Exists(manifestPath)) return false;
        var currentManifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
        if (!string.Equals(currentManifestHash, _extension.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(ExtensionManifestLoader.ComputePackageSha256(_extension.Directory), _extension.PackageSha256, StringComparison.Ordinal))
            return false;
        return proposal.ToolId == Descriptor.Id.ToString()
            && proposal.ExtensionId == _extension.Manifest.Id
            && proposal.ExtensionVersion == _extension.Manifest.Version
            && proposal.PackageSha256 == _extension.PackageSha256
            && proposal.ManifestSha256 == _extension.ManifestSha256
            && PathEquals(proposal.WorkspaceRoot, workspaceRoot)
            && proposal.Changes.All(change => Boundary.AllowsPath(change.Path, write: true));
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
        var permissions = _extension.Manifest.Requests?.Permissions;
        var process = new ProcessClaim(executable, runtime.Args ?? [], "ExtensionTool",
            NetworkRequired: false, WorkingDirectory: context.WorkspaceRoot,
            Isolation: permissions?.Paths?.Write is { Count: > 0 } && _extension.Manifest.ManifestVersion == 1
                && _invoker is IStagedExtensionRpcInvoker
                ? ProcessIsolation.PrivateExtensionWorkspace : ProcessIsolation.None);
        // The source edit scope is authorized before the Strong child runs, but the child sees
        // only a private copy. Publication remains a separate exact-path batch authorization.
        var stagedWrites = process.Isolation == ProcessIsolation.PrivateExtensionWorkspace
            ? permissions?.Paths?.Write ?? [] : [];
        var claims = new ResourceClaims(permissions?.Paths?.Read ?? [], stagedWrites,
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
                + ",\"arguments\":" + intent.Intent.NormalizedArgumentsJson
                + (_extension.Manifest?.ManifestVersion == 2 ? ",\"toolCallId\":\"" + intent.Intent.ToolCallId + "\"" : "") + "}";
            string response;
            if (intent.Intent.Claims.Process?.Isolation == ProcessIsolation.PrivateExtensionWorkspace)
            {
                if (_invoker is not IStagedExtensionRpcInvoker stagedInvoker)
                    throw new NotSupportedException("The Strong private extension workspace is unavailable.");
                var invocation = await stagedInvoker.InvokeStagedAsync(_extension, "tools/call", parameters,
                    intent.Intent.Claims, context.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
                response = invocation.Response;
                if (invocation.Changes.Count > 0)
                {
                    var proposalId = Guid.NewGuid().ToString("N");
                    var proposal = CreateProposal(proposalId, invocation.Changes, context, intent.Intent);
                    var proposalJson = JsonSerializer.Serialize(proposal, ExtensionWriteBatchJsonContext.Default.ExtensionWriteBatchProposal);
                    if (System.Text.Encoding.UTF8.GetByteCount(proposalJson) > 16 * 1024 * 1024)
                        throw new InvalidDataException("Extension proposal exceeds the protected batch limit.");

                    var purpose = ProposalPurpose(context.WorkspaceRoot, proposalId);
                    var protectedStore = context.Artifacts as IProtectedArtifactStore
                        ?? throw new InvalidOperationException("Protected extension proposal storage is unavailable.");
                    var artifact = protectedStore.PutProtectedText(proposalJson, purpose, "application/json", ArtifactKind.Patch);
                    var preview = WriteProposalPreview(proposalId, proposal.Changes, artifact);
                    return new ToolResult("Extension changes are staged as one protected batch proposal.", preview,
                        artifact, proposalJson.Length, wasExternalized: true, EffectOutcome.Applied);
                }
            }
            else
                response = await _invoker.InvokeAsync(_extension, "tools/call", parameters,
                    intent.Intent.Claims, cancellationToken).ConfigureAwait(false);
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

    private static string WriteProposalPreview(string proposalId, IReadOnlyList<ExtensionWriteBatchChange> changes, ArtifactRef artifact)
    {
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("proposalId", proposalId);
            writer.WriteString("artifactHash", artifact.Hash.Value); writer.WriteNumber("artifactSize", artifact.Size);
            writer.WriteStartArray("files");
            foreach (var change in changes) writer.WriteStringValue(change.Path);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
    private ExtensionWriteBatchProposal CreateProposal(string proposalId, IReadOnlyList<ExtensionWorkspaceChange> changes,
        ToolExecutionContext context, ToolIntent intent)
    {
        if (changes.Count is < 1 or > 64) throw new InvalidDataException("Extension batch file limit exceeded.");
        var root = Path.GetFullPath(context.WorkspaceRoot);
        long total = 0;
        var normalized = new List<ExtensionWriteBatchChange>(changes.Count);
        foreach (var change in changes)
        {
            var relative = change.RelativePath.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or "..")
                || relative.Split('/')[0].Equals(".git", StringComparison.OrdinalIgnoreCase)
                || new RedactionPolicy().IsSecretPath(relative))
                throw new InvalidDataException("Extension batch contains a protected or unsafe path.");
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Extension batch path escaped the workspace.");
            if (HasReparsePoint(root, full)) throw new InvalidDataException("Extension batch target topology changed.");
            var before = File.Exists(full) ? SHA256.HashData(File.ReadAllBytes(full)) : null;
            var beforeHash = before is null ? null : Convert.ToHexString(before).ToLowerInvariant();
            if (beforeHash != change.BeforeSha256)
                throw new InvalidDataException("Extension batch preimage changed while the process was running.");
            total = checked(total + (change.AfterBytes?.LongLength ?? 0));
            if (total > 8L * 1024 * 1024) throw new InvalidDataException("Extension batch byte limit exceeded.");
            normalized.Add(new ExtensionWriteBatchChange(relative, change.BeforeSha256,
                change.AfterSha256, change.AfterBytes));
        }
        return new ExtensionWriteBatchProposal(1, proposalId, root,
            intent.ToolCallId.ToString(), intent.ToolId.ToString(), _extension.Manifest!.Id!,
            _extension.Manifest.Version!, _extension.PackageSha256, _extension.ManifestSha256,
            intent.Claims.Process?.Isolation.ToString() ?? "None", context.MutationRunId, context.MutationTurnId,
            context.MutationLaneId,
            normalized.ToArray());
    }

    private static bool HasReparsePoint(string root, string target)
    {
        for (var current = target; !string.Equals(current, root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);)
        {
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Invalid extension target.");
                continue;
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Invalid extension target.");
        }
        return (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0;
    }

    public static string ProposalPurpose(string workspace, string proposalId) =>
        "extension-write-batch:" + Path.GetFullPath(workspace) + ":" + proposalId;
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
