namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Runs one consented non-Core contributor through the Strong Extension API boundary.</summary>
internal sealed class ExtensionContextContributor : IContextContributor
{
    private const int MaximumItems = 16;
    private const int MaximumItemCharacters = 8_192;
    private readonly DiscoveredExtension _extension;
    private readonly string _name;
    private readonly string _workspaceRoot;
    private readonly IExtensionRpcInvoker _invoker;
    private readonly ResourceClaims _claims;
    private readonly Func<bool>? _stillConsented;

    public ExtensionContextContributor(DiscoveredExtension extension, string name,
        string workspaceRoot, IExtensionRpcInvoker invoker, Func<bool>? stillConsented = null)
    {
        _extension = extension;
        _name = name;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _stillConsented = stillConsented;
        var runtime = extension.Manifest?.Runtime
            ?? throw new ArgumentException("Extension contributor has no declared runtime.", nameof(extension));
        var executable = Path.GetFullPath(Path.IsPathRooted(runtime.Command!)
            ? runtime.Command! : Path.Combine(extension.Directory, runtime.Command!));
        _claims = new ResourceClaims([], [], [], new ProcessClaim(executable, runtime.Args ?? [],
            "ExtensionContextContributor", WorkingDirectory: _workspaceRoot), []);
    }

    // Invocation is only available to the explicit human preview path. Package consent alone
    // must never execute a contributor or admit its output into a model request.
    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);

    internal async Task<IReadOnlyList<ContextItem>> ReadPreviewAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stillConsented is not null && !_stillConsented())
            throw new InvalidOperationException("Extension consent or package identity changed before context contribution.");
        var parameters = "{\"name\":\"" + JsonEncodedText.Encode(_name).ToString()
            + "\",\"sessionId\":\"" + JsonEncodedText.Encode(request.SessionId.ToString()).ToString()
            + "\",\"runId\":\"" + JsonEncodedText.Encode(request.RunId.ToString()).ToString()
            + "\",\"taskId\":\"" + JsonEncodedText.Encode(request.TaskId?.ToString() ?? "").ToString()
            + "\",\"laneId\":\"" + JsonEncodedText.Encode(request.LaneId?.ToString() ?? "").ToString()
            + "\",\"turnId\":\"" + JsonEncodedText.Encode(request.TurnId?.ToString() ?? "").ToString()
            + "\",\"fingerprint\":\"" + JsonEncodedText.Encode(request.Fingerprint.Hash()).ToString() + "\"}";
        var responseJson = await _invoker.InvokeAsync(_extension, "context/contribute", parameters,
            _claims, cancellationToken).ConfigureAwait(false);
        using var response = JsonDocument.Parse(responseJson, new JsonDocumentOptions { MaxDepth = 12 });
        if (!response.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaximumItems)
            throw new InvalidDataException("The extension context contributor returned an invalid bounded item list.");
        var source = _extension.Manifest!;
        var output = new List<ContextItem>(items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("kind", out var kindNode) || kindNode.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("content", out var contentNode) || contentNode.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("The extension context item must declare id, kind, and content.");
            var id = idNode.GetString()!;
            var kind = kindNode.GetString() switch
            {
                "skill" => ContextItemKind.Skill,
                "knowledge" => ContextItemKind.Knowledge,
                _ => throw new InvalidDataException("The extension context contributor returned a protected item kind."),
            };
            var content = contentNode.GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,63}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                || content.Length is 0 or > MaximumItemCharacters)
                throw new InvalidDataException("The extension context item exceeds its identity or content limit.");
            var category = kind == ContextItemKind.Knowledge
                ? ContributionCategory.Knowledge : ContributionCategory.Skills;
            output.Add(new ContextItem("extension." + source.Id + "." + _name + "." + id, kind,
                content, Math.Max(1, (content.Length + 3) / 4), ContextPriority.Normal,
                RetentionPolicy.RegenerateEachTurn,
                new ContextProvenance("extension." + source.Id + "." + _name, category,
                    "Extension:" + source.Id + "@" + source.Version + ":" + _extension.PackageSha256,
                    _extension.Trust == TrustLevel.Project ? ScopeLevel.Project : ScopeLevel.User, Sensitive: false,
                    ["extension-manifest-sha256=" + _extension.ManifestSha256,
                        "extension-package-sha256=" + _extension.PackageSha256,
                        "contributor=" + _name])));
        }
        return output.AsReadOnly();
    }
}

public static class ProjectExtensionContributors
{
    public static IReadOnlyList<IContextContributor> Load(string workspaceRoot, IPlatformPaths paths,
        bool workspaceTrusted, IExtensionRpcInvoker invoker,
        Func<MaterializeRequest, IReadOnlyList<ContextItem>>? approvedContext = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        return approvedContext is null ? [] : [new ApprovedContributor(approvedContext)];
    }

    private sealed class ApprovedContributor(Func<MaterializeRequest, IReadOnlyList<ContextItem>> read) : IContextContributor
    {
        public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return System.Threading.Tasks.Task.FromResult(read(request)); }
    }
}
