namespace OmniCore.Extensions;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>One extension hook transported over the same Strong stdio RPC boundary as tools.</summary>
public sealed class ExtensionLifecycleHook : IHook
{
    private readonly DiscoveredExtension _extension;
    private readonly string _workspaceRoot;
    private readonly IExtensionRpcInvoker _invoker;

    public HookRegistration Registration { get; }

    public ExtensionLifecycleHook(DiscoveredExtension extension, int declarationIndex,
        string workspaceRoot, IExtensionRpcInvoker invoker, IReadOnlySet<string> consentedCapabilities,
        bool explicitlyActivated)
    {
        _extension = extension ?? throw new ArgumentNullException(nameof(extension));
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        if (extension.Status != ExtensionDiscoveryStatus.Ready || extension.Manifest?.Id is not { Length: > 0 } id
            || extension.Manifest.Version is not { Length: > 0 } version
            || extension.Manifest.Provides?.Hooks is not { } hooks || declarationIndex < 0 || declarationIndex >= hooks.Count)
            throw new ArgumentException("Only a ready extension hook declaration can be registered.", nameof(extension));
        var declaration = hooks[declarationIndex];
        if (!Enum.TryParse<LifecyclePoint>(declaration.Point, ignoreCase: false, out var point))
            throw new InvalidDataException("Extension hook lifecycle point is invalid.");
        var requested = (declaration.Capabilities ?? []).Select(value => Enum.TryParse<HookCapability>(value,
                ignoreCase: false, out var capability) ? capability
                : throw new InvalidDataException("Extension hook capability is invalid."))
            .ToHashSet();
        if (requested.Count == 0) throw new InvalidDataException("Extension hook must declare capabilities.");
        var granted = requested.Where(capability => consentedCapabilities.Contains("hook." + capability.ToString().ToLowerInvariant()))
            .ToHashSet();
        var scope = extension.Trust == TrustLevel.Project ? ScopeLevel.Project : ScopeLevel.User;
        var source = new ComponentSource(SourceKind.Extension, scope, extension.Trust,
            id, version, extension.PackageSha256);
        Registration = new HookRegistration(id + ".hook." + declarationIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source, point, requested, granted, HookArgumentAccess.Redacted, explicitlyActivated,
            WorkspaceTrusted: extension.Trust == TrustLevel.Project, Timeout: TimeSpan.FromSeconds(30));
    }

    public async ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken cancellationToken)
    {
        if (!Registration.Enabled) return new HookContinue();
        var runtime = _extension.Manifest?.Runtime
            ?? throw new InvalidDataException("Extension hook has no process runtime.");
        var executable = Path.GetFullPath(Path.IsPathRooted(runtime.Command!)
            ? runtime.Command! : Path.Combine(_extension.Directory, runtime.Command!));
        var process = new ProcessClaim(executable, runtime.Args ?? [], "ExtensionHook",
            NetworkRequired: false, WorkingDirectory: _workspaceRoot);
        // A hook needs its declared Strong runtime but receives no project file, network,
        // or artifact grants through this transport.
        var claims = new ResourceClaims([], [], [], process, []);
        var parameters = EncodeParameters(context);
        var response = await _invoker.InvokeAsync(_extension, "hooks/execute", parameters, claims,
            cancellationToken).ConfigureAwait(false);
        if (response.Length > 262_144) throw new InvalidDataException("Extension hook response exceeds the size limit.");
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Extension hook response is malformed.");
        var kind = kindElement.GetString();
        return kind switch
        {
            "continue" => new HookContinue(),
            "annotate" when Registration.IsEligible(HookCapability.Annotate) => ParseAnnotations(result),
            "restrict" when Registration.IsEligible(HookCapability.Restrict) => ParseRestrictions(result),
            "requireValidation" when Registration.IsEligible(HookCapability.RequireValidation) => ParseValidation(result),
            "fail" => new HookFail("Extension hook reported failure."),
            _ => new HookFail("Extension hook returned an ungranted result."),
        };
    }

    private string EncodeParameters(HookContext context)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("hookId", Registration.Id);
            writer.WriteString("point", context.Point.ToString());
            if (context.ToolId is not null) writer.WriteString("toolId", context.ToolId);
            if (context.RedactedArgumentsJson is not null)
            {
                writer.WritePropertyName("redactedArgumentsJson");
                writer.WriteStringValue(context.RedactedArgumentsJson);
            }
            if (context.EventType is not null) writer.WriteString("eventType", context.EventType);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private HookResult ParseAnnotations(JsonElement result)
    {
        if (!result.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 16)
            throw new InvalidDataException("Extension hook annotations are invalid.");
        var items = new List<ContextItem>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String || !item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String || content.GetString()!.Length > 8192)
                throw new InvalidDataException("Extension hook annotation is invalid.");
            items.Add(new ContextItem(id.GetString()!, ContextItemKind.Knowledge, content.GetString()!,
                Math.Max(1, (content.GetString()!.Length + 3) / 4), ContextPriority.Low,
                RetentionPolicy.ConversationWindow,
                new ContextProvenance("extension-hook", ContributionCategory.Knowledge,
                    "extension", _extension.Trust == TrustLevel.Project ? ScopeLevel.Project : ScopeLevel.User, false)));
        }
        return new HookAnnotate(items);
    }

    private static HookResult ParseRestrictions(JsonElement result)
    {
        if (!result.TryGetProperty("deniedToolIds", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() > 64) throw new InvalidDataException("Extension hook restrictions are invalid.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 160 } id)
                throw new InvalidDataException("Extension hook restriction id is invalid.");
            ids.Add(id);
        }
        return new HookRestrict(ids, "Extension hook restriction.");
    }

    private static HookResult ParseValidation(JsonElement result)
    {
        if (!result.TryGetProperty("validatorId", out var validator) || validator.ValueKind != JsonValueKind.String
            || !result.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Extension hook validation request is invalid.");
        return new HookRequireValidation(validator.GetString()!, reason.GetString()!);
    }
}
