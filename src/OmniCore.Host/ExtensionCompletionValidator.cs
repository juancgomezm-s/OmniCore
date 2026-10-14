namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;

/// <summary>Executes declared package validators over Strong stdio with metadata-only input.</summary>
public sealed class ExtensionCompletionValidator
{
    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";
    private readonly DiscoveredExtension _extension;
    private readonly IReadOnlyList<string> _validators;
    private readonly IReadOnlyList<string> _completionGates;
    private readonly string _workspaceRoot;
    private readonly SessionId _session;
    private readonly RunId _run;
    private readonly IExtensionRpcInvoker _invoker;
    private readonly ResourceClaims _claims;
    private readonly Func<bool>? _stillConsented;

    internal ExtensionCompletionValidator(DiscoveredExtension extension, IReadOnlyList<string> validators,
        string workspaceRoot, SessionId session, RunId run, IExtensionRpcInvoker invoker,
        Func<bool>? stillConsented = null, IReadOnlyList<string>? completionGates = null)
    {
        _extension = extension;
        _validators = validators.ToArray();
        _completionGates = completionGates?.ToArray() ?? [];
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _session = session;
        _run = run;
        _invoker = invoker;
        _stillConsented = stillConsented;
        var runtime = extension.Manifest?.Runtime
            ?? throw new ArgumentException("Extension validator has no declared runtime.", nameof(extension));
        var executable = Path.GetFullPath(Path.IsPathRooted(runtime.Command!)
            ? runtime.Command! : Path.Combine(extension.Directory, runtime.Command!));
        _claims = new ResourceClaims([], [], [], new ProcessClaim(executable, runtime.Args ?? [],
            "ExtensionCompletionValidator", WorkingDirectory: _workspaceRoot), []);
    }

    public IReadOnlyList<ExternalCompletionGateResult> Run(CancellationToken cancellationToken = default,
        string? boundedRunContextJson = null, IReadOnlySet<string>? requestedHookIds = null)
    {
        var results = new List<ExternalCompletionGateResult>(_validators.Count + _completionGates.Count);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stillConsented is not null && !_stillConsented())
                throw new InvalidDataException("Extension consent or package identity changed.");
            var manifestId = _extension.Manifest!.Id!;
            var validators = requestedHookIds is null ? _validators : _validators.Where(id =>
                requestedHookIds.Contains("extension." + manifestId + "." + id)).ToArray();
            var gates = requestedHookIds is null ? _completionGates : _completionGates.Where(id =>
                requestedHookIds.Contains("completion.gate." + manifestId + "." + id)).ToArray();
            if (validators.Count > 0)
                results.AddRange(RunSet(validators, "completion/validate", "extension:" + manifestId + ":",
                    "extension." + manifestId + ".", boundedRunContextJson, cancellationToken));
            if (gates.Count > 0)
                results.AddRange(RunSet(gates, "completion/gate", "completion.gate:" + manifestId + ":",
                    "completion.gate." + manifestId + ".", boundedRunContextJson, cancellationToken));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException or TimeoutException
            or NotSupportedException)
        {
            results.Clear();
            var selectedValidators = requestedHookIds is null ? _validators : _validators.Where(id =>
                requestedHookIds.Contains("extension." + _extension.Manifest!.Id + "." + id)).ToArray();
            var selectedGates = requestedHookIds is null ? _completionGates : _completionGates.Where(id =>
                requestedHookIds.Contains("completion.gate." + _extension.Manifest!.Id + "." + id)).ToArray();
            results.AddRange(selectedValidators.Select(id => new ExternalCompletionGateResult(
                "extension:" + _extension.Manifest!.Id + ":" + id, false,
                "Extension validator failed closed (" + exception.GetType().Name + ").",
                ValidatorId: "extension." + _extension.Manifest!.Id + "." + id)));
            results.AddRange(selectedGates.Select(id => new ExternalCompletionGateResult(
                "completion.gate:" + _extension.Manifest!.Id + ":" + id, false,
                "Extension completion gate failed closed (" + exception.GetType().Name + ").",
                ValidatorId: "completion.gate." + _extension.Manifest!.Id + "." + id)));
        }
        return results.AsReadOnly();
    }

    internal IEnumerable<(string HookId, string ResultKey)> HookValidationIdentities()
    {
        var manifestId = _extension.Manifest!.Id!;
        return _validators.Select(id => ("extension." + manifestId + "." + id,
                "extension:" + manifestId + ":" + id))
            .Concat(_completionGates.Select(id => ("completion.gate." + manifestId + "." + id,
                "completion.gate:" + manifestId + ":" + id))).ToArray();
    }

    private IReadOnlyList<ExternalCompletionGateResult> RunSet(IReadOnlyList<string> ids, string method,
        string keyPrefix, string hookIdPrefix, string? boundedRunContextJson, CancellationToken cancellationToken)
    {
        var context = boundedRunContextJson is null ? null :
            JsonDocument.Parse(boundedRunContextJson, new JsonDocumentOptions { MaxDepth = 8 });
        try
        {
            var request = new ExtensionCompletionRequestJson(_session.ToString(), _run.ToString(),
                method == "completion/validate" ? ids : null,
                method == "completion/gate" ? ids : null, context?.RootElement);
            var parameters = JsonSerializer.Serialize(request, LifecycleJsonContext.Default.ExtensionCompletionRequestJson);
            var response = _invoker.InvokeAsync(_extension, method, parameters, _claims,
                cancellationToken).GetAwaiter().GetResult();
            if (response.Length > 65_536) throw new InvalidDataException("Extension validation response exceeds the size limit.");
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 8 });
            if (!document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("results", out var array)
                || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != ids.Count)
                throw new InvalidDataException("Extension validation response is incomplete.");
            var byId = new Dictionary<string, ExternalCompletionGateResult>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("passed", out var passedNode)
                    || passedNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Extension validation result is malformed.");
                var id = idNode.GetString()!;
                if (!ids.Contains(id, StringComparer.Ordinal))
                    throw new InvalidDataException("Extension returned an undeclared validation result.");
                var summary = item.TryGetProperty("summary", out var summaryNode)
                    && summaryNode.ValueKind == JsonValueKind.String ? summaryNode.GetString()! : "";
                if (summary.Length > 512) throw new InvalidDataException("Extension summary exceeds the size limit.");
                summary = new RedactionPolicy().Redact(summary);
                if (!byId.TryAdd(id, new(keyPrefix + id,
                        passedNode.GetBoolean(), string.IsNullOrWhiteSpace(summary)
                            ? "Extension validation returned a result." : summary,
                        ValidatorId: hookIdPrefix + id)))
                    throw new InvalidDataException("Extension returned duplicate validation results.");
            }
            return ids.Select(id => byId.GetValueOrDefault(id)
                ?? new ExternalCompletionGateResult(keyPrefix + id, false,
                    "Extension omitted its validation result.", ValidatorId: hookIdPrefix + id)).ToArray();
        }
        finally { context?.Dispose(); }
    }
}

public static class ProjectExtensionValidators
{
    public static IReadOnlyList<ExtensionCompletionValidator> Load(string workspaceRoot, IPlatformPaths paths,
        bool workspaceTrusted, SessionId session, RunId run, IExtensionRpcInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        return ProjectExtensionPackageCatalog.Ready(workspaceRoot, paths, workspaceTrusted)
            .Where(item => item.Capabilities.Contains("completion.validate")
                || item.Capabilities.Contains("completion.gate"))
            .Where(item => item.Capabilities.Contains("completion.validate")
                    && (item.Extension.Manifest!.Provides?.Validators?.Count ?? 0) > 0
                || item.Capabilities.Contains("completion.gate")
                    && (item.Extension.Manifest!.Provides?.CompletionGates?.Count ?? 0) > 0)
            .Select(item =>
            {
                var requiredCapabilities = new HashSet<string>(StringComparer.Ordinal);
                if (item.Capabilities.Contains("completion.validate")
                    && (item.Extension.Manifest!.Provides?.Validators?.Count ?? 0) > 0)
                    requiredCapabilities.Add("completion.validate");
                if (item.Capabilities.Contains("completion.gate")
                    && (item.Extension.Manifest!.Provides?.CompletionGates?.Count ?? 0) > 0)
                    requiredCapabilities.Add("completion.gate");
                return new ExtensionCompletionValidator(item.Extension,
                item.Capabilities.Contains("completion.validate")
                    ? item.Extension.Manifest!.Provides!.Validators ?? [] : [],
                workspaceRoot, session, run, invoker,
                () => ProjectExtensionPackageCatalog.Ready(workspaceRoot, paths, workspaceTrusted)
                    .Any(current => requiredCapabilities.All(current.Capabilities.Contains)
                        && current.Extension.Manifest?.Id == item.Extension.Manifest?.Id
                        && current.Extension.Trust == item.Extension.Trust
                        && current.Extension.ManifestSha256 == item.Extension.ManifestSha256
                        && current.Extension.PackageSha256 == item.Extension.PackageSha256),
                item.Capabilities.Contains("completion.gate")
                    ? item.Extension.Manifest!.Provides!.CompletionGates ?? [] : []);
            })
            .ToArray();
    }

}
