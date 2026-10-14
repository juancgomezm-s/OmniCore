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
    private readonly string _workspaceRoot;
    private readonly SessionId _session;
    private readonly RunId _run;
    private readonly IExtensionRpcInvoker _invoker;
    private readonly ResourceClaims _claims;
    private readonly Func<bool>? _stillConsented;

    internal ExtensionCompletionValidator(DiscoveredExtension extension, IReadOnlyList<string> validators,
        string workspaceRoot, SessionId session, RunId run, IExtensionRpcInvoker invoker,
        Func<bool>? stillConsented = null)
    {
        _extension = extension;
        _validators = validators.ToArray();
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

    public IReadOnlyList<ExternalCompletionGateResult> Run(CancellationToken cancellationToken = default)
    {
        var results = new List<ExternalCompletionGateResult>(_validators.Count);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stillConsented is not null && !_stillConsented())
                throw new InvalidDataException("Extension consent or package identity changed.");
            var names = "[" + string.Join(",", _validators.Select(Quote)) + "]";
            var parameters = "{\"validators\":" + names + ",\"sessionId\":"
                + Quote(_session.ToString()) + ",\"runId\":" + Quote(_run.ToString()) + "}";
            var response = _invoker.InvokeAsync(_extension, "completion/validate", parameters, _claims,
                cancellationToken).GetAwaiter().GetResult();
            if (response.Length > 65_536) throw new InvalidDataException("Extension validator response exceeds the size limit.");
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 8 });
            if (!document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("results", out var array)
                || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != _validators.Count)
                throw new InvalidDataException("Extension validator response is incomplete.");
            var byId = new Dictionary<string, ExternalCompletionGateResult>(StringComparer.Ordinal);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("passed", out var passedNode)
                    || passedNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Extension validator result is malformed.");
                var id = idNode.GetString()!;
                if (!_validators.Contains(id, StringComparer.Ordinal))
                    throw new InvalidDataException("Extension returned an undeclared validator result.");
                var summary = item.TryGetProperty("summary", out var summaryNode)
                    && summaryNode.ValueKind == JsonValueKind.String ? summaryNode.GetString()! : "";
                if (summary.Length > 512) throw new InvalidDataException("Extension validator summary exceeds the size limit.");
                summary = new RedactionPolicy().Redact(summary);
                if (!byId.TryAdd(id, new("extension:" + _extension.Manifest!.Id + ":" + id,
                        passedNode.GetBoolean(), string.IsNullOrWhiteSpace(summary)
                            ? "Extension validator returned a result." : summary)))
                    throw new InvalidDataException("Extension returned duplicate validator results.");
            }
            results.AddRange(_validators.Select(id => byId.GetValueOrDefault(id)
                ?? new ExternalCompletionGateResult("extension:" + _extension.Manifest!.Id + ":" + id,
                    false, "Extension validator omitted its result.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException
            or InvalidOperationException or UnauthorizedAccessException or ArgumentException or TimeoutException
            or NotSupportedException)
        {
            results.Clear();
            results.AddRange(_validators.Select(id => new ExternalCompletionGateResult(
                "extension:" + _extension.Manifest!.Id + ":" + id, false,
                "Extension validator failed closed (" + exception.GetType().Name + ").")));
        }
        return results.AsReadOnly();
    }
}

public static class ProjectExtensionValidators
{
    public static IReadOnlyList<ExtensionCompletionValidator> Load(string workspaceRoot, IPlatformPaths paths,
        bool workspaceTrusted, SessionId session, RunId run, IExtensionRpcInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        return ProjectExtensionPackageCatalog.Ready(workspaceRoot, paths, workspaceTrusted)
            .Where(item => item.Capabilities.Contains("completion.validate"))
            .Where(item => (item.Extension.Manifest!.Provides?.Validators?.Count ?? 0) > 0)
            .Select(item => new ExtensionCompletionValidator(item.Extension,
                item.Extension.Manifest!.Provides!.Validators!, workspaceRoot, session, run, invoker,
                () => ProjectExtensionPackageCatalog.Ready(workspaceRoot, paths, workspaceTrusted)
                    .Any(current => current.Capabilities.Contains("completion.validate")
                        && current.Extension.Manifest?.Id == item.Extension.Manifest?.Id
                        && current.Extension.Trust == item.Extension.Trust
                        && current.Extension.ManifestSha256 == item.Extension.ManifestSha256
                        && current.Extension.PackageSha256 == item.Extension.PackageSha256)))
            .ToArray();
    }

}
