namespace OmniCore.Extensions;

using OmniCore.Domain;

public enum SkillSelectionRequestKind
{
    Automatic,
    UserExplicit,
    ModelExplicit,
    SessionReplay,
}

public enum SkillSelectionDisposition
{
    Selected,
    NotEligible,
    ModelInvocationDisabled,
    SourceNotActivatable,
    TurnLimitExceeded,
    ContextBudgetExceeded,
}

public sealed record SkillSelectionLimits(int MaximumSkills, int ContextTokenBudget)
{
    public static SkillSelectionLimits FromHarness(HarnessPolicy policy, int contextTokenBudget)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (contextTokenBudget < 0) throw new ArgumentOutOfRangeException(nameof(contextTokenBudget));
        // Smaller tool-capability profiles receive fewer skill loads as well. This is a
        // ceiling only; explicit and automatic requests still pass all source checks.
        var maximumSkills = Math.Clamp((policy.MaxVisibleTools + 1) / 2, 1, 6);
        return new(maximumSkills, contextTokenBudget);
    }
}

public sealed record SkillSelectionDecision(DiscoveredSkill Skill,
    SkillSelectionDisposition Disposition, string Reason);

public sealed record SkillSelectionResult(IReadOnlyList<DiscoveredSkill> Selected,
    IReadOnlyList<SkillSelectionDecision> Decisions, int ReservedContextTokens);

/// <summary>Deterministic skill admission by activation, trust, scope, and context budget.</summary>
public sealed class SkillSelector
{
    public SkillSelectionResult Select(IEnumerable<DiscoveredSkill> candidates,
        SkillSelectionRequestKind requestKind, SkillSelectionLimits limits,
        string? requestedId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumSkills < 0 || limits.ContextTokenBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (requestKind is not (SkillSelectionRequestKind.Automatic or SkillSelectionRequestKind.SessionReplay)
            && string.IsNullOrWhiteSpace(requestedId))
            throw new ArgumentException("An explicit skill selection requires an id.", nameof(requestedId));

        var ordered = candidates
            .Where(skill => requestKind is SkillSelectionRequestKind.Automatic or SkillSelectionRequestKind.SessionReplay
                || string.Equals(skill.Manifest.Id, requestedId, StringComparison.Ordinal))
            .OrderByDescending(skill => skill.Scope)
            .ThenByDescending(skill => skill.Manifest.Priority)
            .ThenBy(skill => skill.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
        var selected = new List<DiscoveredSkill>();
        var decisions = new List<SkillSelectionDecision>();
        var reservedTokens = 0;

        foreach (var skill in ordered)
        {
            var disposition = Check(skill, requestKind);
            if (disposition == SkillSelectionDisposition.Selected
                && selected.Count >= limits.MaximumSkills)
                disposition = SkillSelectionDisposition.TurnLimitExceeded;
            if (disposition == SkillSelectionDisposition.Selected
                && skill.Manifest.BudgetTokens > limits.ContextTokenBudget - reservedTokens)
                disposition = SkillSelectionDisposition.ContextBudgetExceeded;

            var reason = disposition switch
            {
                SkillSelectionDisposition.Selected => "Admitted by activation and turn budgets.",
                SkillSelectionDisposition.NotEligible => "Skill is not eligible for this run.",
                SkillSelectionDisposition.ModelInvocationDisabled => "The manifest does not allow model invocation.",
                SkillSelectionDisposition.SourceNotActivatable => "The source is conflicted, released, or not trusted for activation.",
                SkillSelectionDisposition.TurnLimitExceeded => "The per-turn skill limit has been reached.",
                SkillSelectionDisposition.ContextBudgetExceeded => "The declared skill budget exceeds remaining context capacity.",
                _ => throw new ArgumentOutOfRangeException(),
            };
            decisions.Add(new(skill, disposition, reason));
            if (disposition != SkillSelectionDisposition.Selected) continue;
            reservedTokens += skill.Manifest.BudgetTokens;
            selected.Add(skill with { State = SkillLifecycleState.Activated });
        }
        return new(selected, decisions, reservedTokens);
    }

    private static SkillSelectionDisposition Check(DiscoveredSkill skill,
        SkillSelectionRequestKind requestKind)
    {
        if (skill.State is SkillLifecycleState.Conflict or SkillLifecycleState.Released
            || skill.Trust is TrustLevel.Untrusted or TrustLevel.ThirdParty)
            return SkillSelectionDisposition.SourceNotActivatable;

        var eligible = skill.State == SkillLifecycleState.Eligible;
        var explicitEligible = skill.Manifest.Activation?.Explicit == true;
        var restrictions = skill.InvocationRestrictions;
        return requestKind switch
        {
            SkillSelectionRequestKind.Automatic => eligible && (restrictions?.AllowAutomatic ?? true)
                ? SkillSelectionDisposition.Selected : SkillSelectionDisposition.NotEligible,
            SkillSelectionRequestKind.SessionReplay => skill.State is SkillLifecycleState.Activated or SkillLifecycleState.Loaded
                ? SkillSelectionDisposition.Selected : SkillSelectionDisposition.NotEligible,
            SkillSelectionRequestKind.UserExplicit => (eligible || explicitEligible)
                && (restrictions?.AllowUserInvocation ?? true)
                ? SkillSelectionDisposition.Selected : SkillSelectionDisposition.NotEligible,
            SkillSelectionRequestKind.ModelExplicit => (eligible || skill.Manifest.ModelInvocable)
                && (restrictions?.AllowModelInvocation ?? true)
                ? SkillSelectionDisposition.Selected : SkillSelectionDisposition.ModelInvocationDisabled,
            _ => throw new ArgumentOutOfRangeException(nameof(requestKind)),
        };
    }
}
