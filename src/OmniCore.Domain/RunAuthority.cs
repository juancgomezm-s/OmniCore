namespace OmniCore.Domain;

/// <summary>Product effort is independent of RunMode and provider-native reasoning (ADR-0047).</summary>
public enum ProductEffort
{
    Standard,
    UltraCode,
    LegacyUnknown,
}

/// <summary>Finite user-selected ceilings for one UltraCode authorization (ADR-0047 §4).</summary>
public sealed record ModeSwitchLimits(
    int MaxAgents,
    int MaxDepth,
    int MaxTurns,
    int MaxToolCalls,
    long MaxElapsedSeconds,
    decimal MaxSpendUsd)
{
    public void Validate()
    {
        if (MaxAgents < 0 || MaxDepth < 0 || MaxTurns <= 0 || MaxToolCalls <= 0
            || MaxElapsedSeconds <= 0 || MaxSpendUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(ModeSwitchLimits),
                "Mode-switch limits must be explicit, finite, and non-negative (turns/tools/time must be positive).");
    }
}

/// <summary>Run-scoped permission to change modes; not a tool permission or route grant.</summary>
public sealed record ModeSwitchAuthorization(
    Guid AuthorizationId,
    long AuthorityRevision,
    long ObjectiveRevision,
    string ObjectiveDigest,
    long PolicyRevision,
    IReadOnlyList<RunMode> AllowedModes,
    ModeSwitchLimits Limits,
    DateTimeOffset? GrantedAtUtc = null)
{
    private IReadOnlyList<RunMode> _allowedModes = Array.AsReadOnly(AllowedModes.ToArray());

    /// <summary>Frozen allowlist; callers cannot widen a durable authorization by mutating an input list.</summary>
    public IReadOnlyList<RunMode> AllowedModes
    {
        get => _allowedModes;
        init => _allowedModes = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }

    public void Validate()
    {
        if (AuthorizationId == Guid.Empty || AuthorityRevision <= 0 || ObjectiveRevision <= 0
            || PolicyRevision <= 0 || string.IsNullOrWhiteSpace(ObjectiveDigest)
            || AllowedModes is null || AllowedModes.Count == 0 || Limits is null)
            throw new ArgumentException("Mode-switch authorization is incomplete.");
        if (AllowedModes.Any(mode => !Enum.IsDefined(mode)) || AllowedModes.Distinct().Count() != AllowedModes.Count)
            throw new ArgumentException("Allowed modes must be valid and unique.");
        Limits.Validate();
        if (GrantedAtUtc is not { } granted || granted.Offset != TimeSpan.Zero)
            throw new ArgumentException("Adaptive mode authorization requires a durable UTC grant time.");
        try { _ = granted.AddSeconds(Limits.MaxElapsedSeconds); }
        catch (ArgumentOutOfRangeException exception)
        { throw new ArgumentException("Adaptive authorization elapsed-time limit exceeds the UTC range.", exception); }
    }

    public bool IsExpiredAt(DateTimeOffset utcNow)
    {
        Validate();
        if (utcNow.Offset != TimeSpan.Zero) throw new ArgumentException("Clock value must be UTC.", nameof(utcNow));
        return utcNow >= GrantedAtUtc!.Value.AddSeconds(Limits.MaxElapsedSeconds);
    }
}

/// <summary>Replayable authority state for one Run. Legacy journals have no authorization.</summary>
public sealed record RunModeAuthority(
    RunId RunId,
    long Revision,
    RunMode Mode,
    ExecutionStrategy Strategy,
    ProductEffort ProductEffort,
    bool ModePinned,
    bool AutoModeSwitch,
    long ObjectiveRevision,
    string ObjectiveDigest,
    long PolicyRevision,
    ModeSwitchAuthorization? Authorization)
{
    /// <summary>Whether adaptive mode changes are currently usable, not merely stored as selected.</summary>
    public bool IsAutoModeSwitchEffectiveAt(DateTimeOffset utcNow)
    {
        if (utcNow.Offset != TimeSpan.Zero) throw new ArgumentException("Clock value must be UTC.", nameof(utcNow));
        Validate();
        return AutoModeSwitch && Authorization is { } authorization && !ModePinned
            && authorization.AllowedModes.Contains(Mode) && !authorization.IsExpiredAt(utcNow);
    }

    public static RunModeAuthority Legacy(RunCreated created) => new(created.RunId, 0, created.Mode,
        created.Strategy, ProductEffort.LegacyUnknown, false, false, 1,
        ObjectiveDigestFor(created.Objective), 1, null);

    /// <summary>
    /// Binds authority to the same objective representation the journal preserves. EventStream
    /// redacts PII before persistence, so hashing the caller's raw text would make replay reject a
    /// valid Run whenever redaction changed the objective.
    /// </summary>
    public static string ObjectiveDigestFor(string objective)
    {
        ArgumentNullException.ThrowIfNull(objective);
        var durableObjective = new PiiRedactor().Redact(objective);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(durableObjective)));
    }

    public void Validate()
    {
        if (Revision < 0 || ObjectiveRevision <= 0 || PolicyRevision <= 0
            || string.IsNullOrWhiteSpace(ObjectiveDigest))
            throw new ArgumentException("Run authority is incomplete.");
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Strategy) || !Enum.IsDefined(ProductEffort))
            throw new ArgumentException("Run authority contains an unknown enum value.");
        if (AutoModeSwitch && (ProductEffort != ProductEffort.UltraCode || ModePinned || Authorization is null))
            throw new ArgumentException("Adaptive mode switching requires an unpinned UltraCode authorization.");
        if (ProductEffort != ProductEffort.UltraCode && (AutoModeSwitch || Authorization is not null))
            throw new ArgumentException("Only explicit UltraCode selection can carry adaptive authority.");
        if (Authorization is not null)
        {
            Authorization.Validate();
            if (Authorization.AuthorityRevision != Revision
                || Authorization.ObjectiveRevision != ObjectiveRevision
                || Authorization.ObjectiveDigest != ObjectiveDigest
                || Authorization.PolicyRevision != PolicyRevision)
                throw new ArgumentException("Mode-switch authorization must match its Run authority revisions.");
            if (!Authorization.AllowedModes.Contains(Mode))
                throw new ArgumentException("The selected mode must be covered by its adaptive authorization.");
        }
    }
}
