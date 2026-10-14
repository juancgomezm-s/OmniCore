namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>Stable lifecycle interception points available to explicitly enabled hooks.</summary>
public enum LifecyclePoint
{
    SessionStarting, SessionStarted, RunStarting, TaskCreated, LaneStarting,
    BeforeModel, AfterModel, BeforeTool, AfterTool, ToolFailed, BeforePermission,
    BeforeCompact, AfterCompact, BeforeValidation, AfterValidation,
    BeforeCompletion, RunCompleted, RunFailed,
}

public enum HookCapability { Observe, Annotate, Restrict, RequireValidation }
public enum HookArgumentAccess { None, Redacted, Full }

/// <summary>Lifecycle points with an actual M8 consumer. A declared point without an owning
/// transition consumer stays disabled rather than silently appearing active.</summary>
public static class LifecycleHookSupport
{
    public static bool IsSupported(LifecyclePoint point) => Enum.IsDefined(point);

    /// <summary>Capabilities are available only where their result still has a real consumer.</summary>
    public static bool IsCapabilitySupported(LifecyclePoint point, HookCapability capability) => capability switch
    {
        HookCapability.Observe => true,
        HookCapability.Annotate => point is LifecyclePoint.BeforeModel or LifecyclePoint.AfterModel
            or LifecyclePoint.BeforeTool or LifecyclePoint.AfterTool or LifecyclePoint.ToolFailed
            or LifecyclePoint.BeforePermission or LifecyclePoint.BeforeCompact or LifecyclePoint.AfterCompact,
        HookCapability.Restrict => point is LifecyclePoint.BeforeModel or LifecyclePoint.AfterModel
            or LifecyclePoint.BeforeTool or LifecyclePoint.AfterTool or LifecyclePoint.ToolFailed
            or LifecyclePoint.BeforePermission or LifecyclePoint.BeforeCompact or LifecyclePoint.AfterCompact
            or LifecyclePoint.BeforeValidation or LifecyclePoint.AfterValidation
            or LifecyclePoint.BeforeCompletion,
        HookCapability.RequireValidation => point is LifecyclePoint.BeforeModel or LifecyclePoint.AfterModel
            or LifecyclePoint.BeforeTool or LifecyclePoint.AfterTool or LifecyclePoint.ToolFailed
            or LifecyclePoint.BeforePermission or LifecyclePoint.BeforeValidation
            or LifecyclePoint.AfterValidation or LifecyclePoint.BeforeCompletion,
        _ => false,
    };
}

/// <summary>Only data that the Host has already filtered/redacted can enter a hook.</summary>
public sealed record HookContext(LifecyclePoint Point, string? ToolId = null,
    string? RedactedArgumentsJson = null, string? EventType = null,
    string? TaskId = null, string? LaneId = null);

/// <summary>Capability ceiling and explicit activation for one declared hook.</summary>
public sealed record HookRegistration(string Id, ComponentSource Source, LifecyclePoint Point,
    IReadOnlySet<HookCapability> RequestedCapabilities, IReadOnlySet<HookCapability> GrantedCapabilities,
    HookArgumentAccess ArgumentAccess, bool Enabled, bool WorkspaceTrusted = false,
    TimeSpan? Timeout = null, bool FullArgumentsConsented = false,
    IReadOnlySet<string>? EventTypeFilters = null)
{
    public HookArgumentAccess EffectiveArgumentAccess => Source.Trust switch
    {
        TrustLevel.Core => ArgumentAccess,
        TrustLevel.Trusted when ArgumentAccess == HookArgumentAccess.Full && FullArgumentsConsented => HookArgumentAccess.Full,
        TrustLevel.Trusted when ArgumentAccess != HookArgumentAccess.None => HookArgumentAccess.Redacted,
        TrustLevel.Project or TrustLevel.ThirdParty or TrustLevel.Untrusted
            when ArgumentAccess != HookArgumentAccess.None => HookArgumentAccess.Redacted,
        _ => HookArgumentAccess.None,
    };

    public bool IsEligible(HookCapability capability) => Enabled
        && RequestedCapabilities.Contains(capability)
        && (Source.Trust == TrustLevel.Core
            || Source.Trust == TrustLevel.ThirdParty
                && capability is HookCapability.Observe or HookCapability.Annotate
            || GrantedCapabilities.Contains(capability))
        && (Source.Trust != TrustLevel.Project || WorkspaceTrusted)
        && (Source.Trust switch
        {
            TrustLevel.Untrusted => capability == HookCapability.Observe,
            TrustLevel.ThirdParty => capability is HookCapability.Observe or HookCapability.Annotate,
            _ => true,
        });
}

/// <summary>Hook outcomes can only contribute context or narrow the operation.</summary>
public abstract record HookResult;
public sealed record HookContinue : HookResult;
public sealed record HookAnnotate(IReadOnlyList<ContextItem> Items) : HookResult;
public sealed record HookRestrict(IReadOnlySet<string> DeniedToolIds, string Reason) : HookResult;
public sealed record HookRequireValidation(string ValidatorId, string Reason) : HookResult;
public sealed record HookFail(string Reason) : HookResult;

public interface IHook
{
    HookRegistration Registration { get; }
    ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken cancellationToken);
}

public sealed record HookDispatchResult(bool Denied, IReadOnlySet<string> DeniedToolIds,
    IReadOnlyList<ContextItem> Annotations, IReadOnlyList<HookRequireValidation> Validations,
    IReadOnlyList<string> Warnings, IReadOnlyList<HookApplication> Applications)
{
    public static HookDispatchResult Empty { get; } = new(false, new HashSet<string>(StringComparer.Ordinal),
        [], [], [], []);
}

public sealed record HookApplication(string HookId, string Result, IReadOnlyList<string>? DetailIds = null);

/// <summary>Runs only activated, consented capabilities. Restriction/validation failures deny;
/// observation/annotation failures are warnings and never interrupt the caller.</summary>
public sealed class LifecycleHookDispatcher
{
    private const int MaximumRedactedArgumentCharacters = 16_384;
    private const int MaximumAnnotations = 16;
    private const int MaximumAnnotationCharacters = 8_192;

    public async ValueTask<HookDispatchResult> DispatchAsync(IEnumerable<IHook> hooks, HookContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(context);
        var deniedTools = new HashSet<string>(StringComparer.Ordinal);
        var annotations = new List<ContextItem>();
        var validations = new List<HookRequireValidation>();
        var warnings = new List<string>();
        var applications = new List<HookApplication>();
        var denied = false;
        foreach (var hook in hooks)
        {
            var registration = hook.Registration;
            if (!registration.Enabled || registration.Point != context.Point) continue;
            if (registration.EventTypeFilters is not null
                && (context.EventType is null || !registration.EventTypeFilters.Contains(context.EventType))) continue;
            if (registration.Source.Trust == TrustLevel.Untrusted
                && (registration.EventTypeFilters is null || context.EventType is null
                    || !registration.EventTypeFilters.Contains(context.EventType))) continue;
            if (registration.RequestedCapabilities.Any(registration.IsEligible) == false) continue;
            var restrictive = registration.IsEligible(HookCapability.Restrict)
                || registration.IsEligible(HookCapability.RequireValidation);
            if (!registration.IsEligible(HookCapability.Observe)
                && !registration.IsEligible(HookCapability.Annotate) && !restrictive) continue;
            var access = registration.EffectiveArgumentAccess;
            if (context.RedactedArgumentsJson is { Length: > MaximumRedactedArgumentCharacters })
            {
                if (restrictive) denied = true;
                warnings.Add("HookArgumentsTooLarge:" + registration.Id);
                applications.Add(new HookApplication(registration.Id, restrictive ? "failed_closed" : "failed_open"));
                continue;
            }
            var hookContext = access == HookArgumentAccess.None
                ? context with { RedactedArgumentsJson = null } : context;
            if (registration.Timeout is { } specifiedTimeout
                && (specifiedTimeout <= TimeSpan.Zero || specifiedTimeout > TimeSpan.FromSeconds(30)))
            {
                if (restrictive) denied = true;
                warnings.Add("HookTimeoutInvalid:" + registration.Id);
                applications.Add(new HookApplication(registration.Id, restrictive ? "failed_closed" : "failed_open"));
                continue;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(registration.Timeout ?? TimeSpan.FromSeconds(2));
            try
            {
                var result = await hook.ExecuteAsync(hookContext, timeout.Token).AsTask()
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
                switch (result)
                {
                    case HookContinue:
                        applications.Add(new HookApplication(registration.Id, "continued"));
                        break;
                    case HookAnnotate annotate when registration.IsEligible(HookCapability.Annotate):
                        if (annotate.Items.Count > MaximumAnnotations
                            || annotations.Count + annotate.Items.Count > MaximumAnnotations
                            || annotate.Items.Sum(item => item.Content.Length) > MaximumAnnotationCharacters)
                        {
                            warnings.Add("HookAnnotationsTooLarge:" + registration.Id);
                            applications.Add(new HookApplication(registration.Id, "failed_open"));
                            break;
                        }
                        annotations.AddRange(annotate.Items.Select(item => SafeAnnotation(registration, item)));
                            applications.Add(new HookApplication(registration.Id, "annotated",
                                annotate.Items.Select(item => "hook." + registration.Id + "." + SafeId(item.Id)).Take(MaximumAnnotations).ToArray()));
                        break;
                    case HookRestrict restrict when registration.IsEligible(HookCapability.Restrict):
                        deniedTools.UnionWith(restrict.DeniedToolIds);
                        denied |= hookContext.ToolId is not null && restrict.DeniedToolIds.Contains(hookContext.ToolId);
                        applications.Add(new HookApplication(registration.Id, "restricted",
                            restrict.DeniedToolIds.Order(StringComparer.Ordinal).Take(64).ToArray()));
                        break;
                    case HookRequireValidation validation when registration.IsEligible(HookCapability.RequireValidation):
                        if (!IsCanonicalId(validation.ValidatorId) || validation.Reason.Length > 1024)
                        {
                            denied = true;
                            warnings.Add("HookValidationInvalid:" + registration.Id);
                            applications.Add(new HookApplication(registration.Id, "failed_closed"));
                        }
                        else if (validations.Count >= MaximumAnnotations)
                        {
                            denied = true;
                            warnings.Add("HookValidationsTooMany:" + registration.Id);
                            applications.Add(new HookApplication(registration.Id, "failed_closed"));
                        }
                        else
                        {
                            validations.Add(validation);
                            applications.Add(new HookApplication(registration.Id, "validation_required", [validation.ValidatorId]));
                        }
                        break;
                    default:
                        if (restrictive) denied = true;
                        warnings.Add("HookResultNotGranted:" + registration.Id);
                        applications.Add(new HookApplication(registration.Id, restrictive ? "failed_closed" : "failed_open"));
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (restrictive) denied = true;
                warnings.Add("HookFailed:" + registration.Id + ":" + exception.GetType().Name);
                applications.Add(new HookApplication(registration.Id, restrictive ? "failed_closed" : "failed_open"));
            }
        }
        return new HookDispatchResult(denied, deniedTools, annotations, validations, warnings, applications);
    }

    private static ContextItem SafeAnnotation(HookRegistration registration, ContextItem item)
    {
        if (item.Content.Length > MaximumAnnotationCharacters)
            throw new InvalidDataException("Hook annotation exceeded the content bound.");
        var source = registration.Source;
        return new ContextItem("hook." + registration.Id + "." + SafeId(item.Id), ContextItemKind.Knowledge,
            item.Content, Math.Max(1, (item.Content.Length + 3) / 4), ContextPriority.Low,
            RetentionPolicy.ConversationWindow,
            new ContextProvenance("hook." + registration.Id, ContributionCategory.Knowledge,
                source.Owner, source.Scope, item.Provenance.Sensitive, item.Provenance.Refs));
    }

    private static string SafeId(string value)
    {
        var clean = new string(value.Where(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.').Take(96).ToArray());
        return clean.Length == 0 ? "annotation" : clean;
    }

    private static bool IsCanonicalId(string value) => value.Length is > 0 and <= 160
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');
}

/// <summary>
/// Immutable host-side registry. Registration is distinct from activation: only registrations
/// whose owner explicitly enabled them are returned to the dispatcher. Capability grants are
/// intersected with the registration's requested set before the dispatcher can see a hook.
/// </summary>
public sealed class LifecycleHookRegistry
{
    private readonly Dictionary<string, IHook> _hooks = new(StringComparer.Ordinal);

    public void Register(IHook hook, bool explicitlyActivated, IReadOnlySet<HookCapability>? consented = null)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var registration = hook.Registration;
        if (!IsCanonicalRegistrationId(registration.Id))
            throw new ArgumentException("Hook registration id is invalid.", nameof(hook));
        if (_hooks.ContainsKey(registration.Id))
            throw new InvalidOperationException("Hook registration id is already in use.");
        if (explicitlyActivated && registration.RequestedCapabilities.Any(capability =>
                !LifecycleHookSupport.IsCapabilitySupported(registration.Point, capability)))
            throw new InvalidOperationException("Hook registration requests a capability without a lifecycle consumer.");
        var grants = consented is null
            ? new HashSet<HookCapability>()
            : new HashSet<HookCapability>(consented);
        if (!grants.IsSubsetOf(registration.RequestedCapabilities))
            throw new InvalidOperationException("Hook consent exceeds the declared capabilities.");
        var enabled = explicitlyActivated && LifecycleHookSupport.IsSupported(registration.Point);
        var normalized = registration with
        {
            Enabled = enabled,
            GrantedCapabilities = grants,
        };
        _hooks.Add(registration.Id, new RegisteredHook(hook, normalized));
    }

    public IReadOnlyList<IHook> Activated() => _hooks.Values
        .Where(hook => hook.Registration.Enabled)
        .OrderBy(hook => ScopeOrder(hook.Registration.Source.Scope))
        .ThenBy(hook => hook.Registration.Id, StringComparer.Ordinal).ToArray();

    private static int ScopeOrder(ScopeLevel scope) => scope switch
    {
        ScopeLevel.BuiltIn => 0, ScopeLevel.User => 1, ScopeLevel.Project => 2,
        ScopeLevel.Workspace => 3, ScopeLevel.Session => 4, ScopeLevel.Run => 5,
        ScopeLevel.Task => 6, ScopeLevel.Lane => 7, _ => 8,
    };

    private static bool IsCanonicalRegistrationId(string id) => id.Length is > 0 and <= 160
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private sealed class RegisteredHook(IHook inner, HookRegistration registration) : IHook
    {
        public HookRegistration Registration { get; } = registration;
        public ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(context, cancellationToken);
    }
}
