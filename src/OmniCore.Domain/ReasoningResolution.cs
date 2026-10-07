namespace OmniCore.Domain;

/// <summary>Where an effective reasoning request came from; None differs from an explicit off selection.</summary>
public enum ReasoningSelectionSource
{
    None,
    UserDefault,
    RunOverride,
    TurnBoost,
    UltraCode,
}

/// <summary>Deterministic reason that a requested native effort was reduced before dispatch.</summary>
public enum ReasoningReduction
{
    Capability,
    OutputReserve,
    ContextCapacity,
    TaskBudget,
}

/// <summary>
/// Frozen provider-neutral resolution for one Turn/step. Requested and applied values are separate;
/// this is evidence of the selection and route decision, not a claim that the provider honored it.
/// </summary>
public sealed record ReasoningResolution
{
    private readonly IReadOnlyList<ReasoningReduction> _reductions;

    public ReasoningRequest? RequestedRequest { get; }
    public ReasoningRequest? AppliedRequest { get; }
    public ReasoningSelectionSource Source { get; }
    public long? UserPreferenceRevision { get; }
    public long? RunPreferenceRevision { get; }
    public long? ModeAuthorityRevision { get; }
    public Guid? TurnBoostId { get; }
    public int? OutputReserveTokens { get; }
    public IReadOnlyList<ReasoningReduction> Reductions => _reductions;

    [System.Text.Json.Serialization.JsonConstructor]
    public ReasoningResolution(ReasoningRequest? requestedRequest, ReasoningRequest? appliedRequest,
        ReasoningSelectionSource source, long? userPreferenceRevision = null, long? runPreferenceRevision = null,
        long? modeAuthorityRevision = null, Guid? turnBoostId = null, int? outputReserveTokens = null,
        IReadOnlyList<ReasoningReduction>? reductions = null)
    {
        RequestedRequest = requestedRequest;
        AppliedRequest = appliedRequest;
        Source = source;
        UserPreferenceRevision = userPreferenceRevision;
        RunPreferenceRevision = runPreferenceRevision;
        ModeAuthorityRevision = modeAuthorityRevision;
        TurnBoostId = turnBoostId;
        OutputReserveTokens = outputReserveTokens;
        _reductions = Array.AsReadOnly((reductions ?? Array.Empty<ReasoningReduction>()).ToArray());
        Validate();
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Source) || _reductions.Any(reduction => !Enum.IsDefined(reduction))
            || _reductions.Distinct().Count() != _reductions.Count
            || !_reductions.SequenceEqual(_reductions.OrderBy(reduction => reduction)))
            throw new ArgumentException("Reasoning resolution contains an unknown or duplicate value.");
        if (UserPreferenceRevision is <= 0 || RunPreferenceRevision is <= 0 || ModeAuthorityRevision is <= 0
            || TurnBoostId == Guid.Empty || OutputReserveTokens is <= 0)
            throw new ArgumentException("Reasoning resolution contains an invalid revision, id, or reserve.");
        ValidateRequest(RequestedRequest);
        ValidateRequest(AppliedRequest);
        if (Source == ReasoningSelectionSource.None
            && (RequestedRequest is not null || AppliedRequest is not null || UserPreferenceRevision is not null
                || RunPreferenceRevision is not null || ModeAuthorityRevision is not null || TurnBoostId is not null
                || OutputReserveTokens is not null || _reductions.Count != 0))
            throw new ArgumentException("An unselected reasoning resolution cannot carry an inferred request or authority.");
        if (Source == ReasoningSelectionSource.TurnBoost && (TurnBoostId is null || RequestedRequest is null))
            throw new ArgumentException("A Turn reasoning boost requires its explicit request and identity.");
        if (Source == ReasoningSelectionSource.UltraCode && ModeAuthorityRevision is null)
            throw new ArgumentException("UltraCode reasoning requires the durable mode-authority revision.");
        if (Source == ReasoningSelectionSource.UserDefault
            && (UserPreferenceRevision is null || RunPreferenceRevision is not null || TurnBoostId is not null))
            throw new ArgumentException("A User-default resolution requires its captured User revision.");
        if (Source == ReasoningSelectionSource.RunOverride
            && (RunPreferenceRevision is null || UserPreferenceRevision is not null || TurnBoostId is not null))
            throw new ArgumentException("A Run override resolution requires its Run revision.");
        if (Source == ReasoningSelectionSource.TurnBoost
            && (UserPreferenceRevision is not null || RunPreferenceRevision is not null))
            throw new ArgumentException("A Turn boost cannot claim a persisted preference revision.");
        if (Source == ReasoningSelectionSource.UltraCode
            && (UserPreferenceRevision is not null || RunPreferenceRevision is not null || TurnBoostId is not null))
            throw new ArgumentException("UltraCode reasoning cannot claim another selection source.");
        if (Source != ReasoningSelectionSource.UltraCode && OutputReserveTokens is not null)
            throw new ArgumentException("Only an UltraCode resolution can carry its configured output reserve.");
        if (AppliedRequest is not null && RequestedRequest is null)
            throw new ArgumentException("An applied reasoning request requires a recorded request.");
        if (RequestedRequest != AppliedRequest && _reductions.Count == 0)
            throw new ArgumentException("A changed reasoning request must record a deterministic reduction.");
        if (RequestedRequest == AppliedRequest && _reductions.Count != 0)
            throw new ArgumentException("A resolution without a change cannot claim a reduction.");
        if (Source == ReasoningSelectionSource.UltraCode && RequestedRequest is null
            && !_reductions.Contains(ReasoningReduction.Capability))
            throw new ArgumentException("An UltraCode resolution without a native request must record the capability limitation.");
    }

    public bool IsEquivalentTo(ReasoningResolution? other) => other is not null
        && RequestedRequest == other.RequestedRequest && AppliedRequest == other.AppliedRequest
        && Source == other.Source && UserPreferenceRevision == other.UserPreferenceRevision
        && RunPreferenceRevision == other.RunPreferenceRevision && ModeAuthorityRevision == other.ModeAuthorityRevision
        && TurnBoostId == other.TurnBoostId && OutputReserveTokens == other.OutputReserveTokens
        && _reductions.SequenceEqual(other._reductions);

    private static void ValidateRequest(ReasoningRequest? request)
    {
        if (request is null) return;
        if (string.IsNullOrWhiteSpace(request.Kind)
            || (request.Kind == "budget" ? request.BudgetTokens is null or < 1024 : request.BudgetTokens is not null))
            throw new ArgumentException("Reasoning request has an invalid budget representation.");
    }
}

/// <summary>Resolved system instruction intent captured for deterministic same-Turn resume.</summary>
public sealed record TurnInstructionSnapshot(bool ConversationOnly, string ResolvedInstruction)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ResolvedInstruction))
            throw new ArgumentException("Turn instruction snapshot must preserve the resolved instruction.");
    }
}
