namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Memory;

/// <summary>Rebuilds the active skill set and all of its Turn consumers at a settled Turn boundary.</summary>
public sealed class SkillTurnComposition
{
    private readonly SessionId _session;
    private readonly SessionSkillActivationStore _store;
    private readonly Func<IReadOnlyList<DiscoveredSkill>> _discover;
    private readonly SkillSelectionLimits _limits;
    private readonly ITokenCounter _counter;
    private readonly string _workspaceData;
    private readonly bool _guidanceEnabled;
    private readonly bool _includeWorkingState;
    private readonly string _workingState;
    private readonly IReadOnlyList<IContextContributor> _extensionContributors;
    private readonly Func<IReadOnlyList<IContextContributor>>? _extensionContributorFactory;
    private readonly Func<IReadOnlyList<ExtensionCompletionValidator>>? _extensionValidatorFactory;
    private readonly Action<SkillActivationChanged>? _emitLifecycle;

    public SkillTurnComposition(SessionId session, SessionSkillActivationStore store,
        Func<IReadOnlyList<DiscoveredSkill>> discover, SkillSelectionLimits limits,
        ITokenCounter counter, string workspaceData,
        bool guidanceEnabled, bool includeWorkingState, string workingState,
        IReadOnlyList<IContextContributor>? extensionContributors = null,
        Func<IReadOnlyList<ExtensionCompletionValidator>>? extensionValidatorFactory = null,
        Func<IReadOnlyList<IContextContributor>>? extensionContributorFactory = null,
        Action<SkillActivationChanged>? emitLifecycle = null)
    {
        _session = session;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _limits = limits;
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _workspaceData = Path.GetFullPath(workspaceData);
        _guidanceEnabled = guidanceEnabled;
        _includeWorkingState = includeWorkingState;
        _workingState = workingState ?? "";
        _extensionContributors = extensionContributors?.ToArray() ?? [];
        _extensionValidatorFactory = extensionValidatorFactory;
        _extensionContributorFactory = extensionContributorFactory;
        _emitLifecycle = emitLifecycle;
    }

    public SkillTurnCompositionSnapshot Refresh()
    {
        var candidates = _discover().ToArray();
        ReconcileStaleActivations(candidates);
        var persisted = new SkillActivationResolver().LoadCandidates(candidates, _session, _store,
            limits: _limits);
        var remaining = new SkillSelectionLimits(
            Math.Max(0, _limits.MaximumSkills - persisted.Count),
            Math.Max(0, _limits.ContextTokenBudget - persisted.Sum(skill => skill.Skill.Manifest.BudgetTokens)));
        var activeIdentities = persisted.Select(skill => new SkillActivationIdentity(
            skill.Skill.Scope, skill.Skill.Manifest.Id!)).ToHashSet();
        // Persisted skills must not be offered to the Automatic selector: otherwise they can
        // consume its last slot before duplicate identities are filtered from the result.
        var eligible = candidates.Where(candidate => candidate.State == SkillLifecycleState.Eligible
            && !activeIdentities.Contains(new SkillActivationIdentity(candidate.Scope, candidate.Manifest.Id!)))
            .ToList();
        var loader = new SkillManifestLoader();
        var automaticLoaded = new List<LoadedSkill>();
        var selector = new SkillSelector();
        while (remaining.MaximumSkills > 0 && eligible.Count > 0)
        {
            var selection = selector.Select(eligible, SkillSelectionRequestKind.Automatic,
                remaining with { MaximumSkills = 1 });
            var candidate = selection.Selected.SingleOrDefault();
            if (candidate is null) break;
            eligible.RemoveAll(item => item.Scope == candidate.Scope
                && string.Equals(item.Manifest.Id, candidate.Manifest.Id, StringComparison.Ordinal));
            var loadedCandidate = loader.Activate(candidate);
            var packageHash = loadedCandidate.PackageContentSha256 ?? loadedCandidate.ContentSha256;
            if (_store.IsAutomaticSuppressed(_session, candidate.Scope, candidate.Manifest.Id!,
                    candidate.ManifestSha256, packageHash)) continue;
            automaticLoaded.Add(loadedCandidate);
            remaining = remaining with
            {
                MaximumSkills = remaining.MaximumSkills - 1,
                ContextTokenBudget = Math.Max(0, remaining.ContextTokenBudget - candidate.Manifest.BudgetTokens),
            };
        }
        foreach (var activatedSkill in automaticLoaded)
        {
            var id = activatedSkill.Skill.Manifest.Id!;
            var packageHash = activatedSkill.PackageContentSha256 ?? activatedSkill.ContentSha256;
            var current = _store.ReadDetailed(_session).GetValueOrDefault(new(activatedSkill.Skill.Scope, id));
            if (current is not null && current.Origin == "Automatic"
                && current.ManifestSha256 == activatedSkill.Skill.ManifestSha256 && current.PackageContentSha256 == packageHash)
                continue;
            var emitLifecycle = _emitLifecycle ?? throw new InvalidOperationException(
                "Automatic skill activation requires a durable Session lifecycle event sink.");
            _store.ActivateAutomatic(_session, activatedSkill.Skill.Scope, id, activatedSkill.Skill.ManifestSha256, packageHash);
            try
            {
                emitLifecycle(new SkillActivationChanged(id, activatedSkill.Skill.Scope,
                    activatedSkill.Skill.ManifestSha256, Activated: true, Origin: "Automatic"));
            }
            catch
            {
                if (current is null) _store.Release(_session, activatedSkill.Skill.Scope, id);
                else _store.Activate(_session, activatedSkill.Skill.Scope, id, current.ManifestSha256,
                    current.PackageContentSha256, current.Origin);
                throw;
            }
        }
        var loaded = persisted.Concat(automaticLoaded)
            .OrderBy(skill => skill.Skill.Scope).ThenByDescending(skill => skill.Skill.Manifest.Priority)
            .ThenBy(skill => skill.Skill.Manifest.Id, StringComparer.Ordinal).ToArray();
        var fingerprints = loaded.Select(skill => new ActiveSkillFingerprint(skill.Skill.Manifest.Id!,
            skill.Skill.Manifest.Version!, ContentHash.Sha256(
                (skill.PackageContentSha256 ?? skill.ContentSha256) + "\nmanifest:" + skill.Skill.ManifestSha256))).ToArray();
        var contributors = loaded.Select(skill => (IContextContributor)new SkillContextContributor(_session, skill))
            .Append(new SkillIndexContributor(_session, candidates, _guidanceEnabled, _limits.ContextTokenBudget))
            .Append(new SessionMemoryContextContributor(new SessionMemoryStore(_workspaceData)))
            .Concat(_extensionContributorFactory?.Invoke() ?? _extensionContributors).ToArray();
        if (_includeWorkingState)
            contributors = new IContextContributor[] { new WorkingStateContributor(_workingState) }
                .Concat(contributors).ToArray();
        var materializer = new ContextMaterializer(_counter, contributors);
        var validators = new SkillCompletionValidator(loaded, _extensionValidatorFactory?.Invoke());
        var toolIds = loaded.SelectMany(skill => skill.Skill.Manifest.Tools ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        return new(candidates, loaded, fingerprints, toolIds, materializer, validators);
    }

    /// <summary>Delegated Runs use only the separately accepted context packet materialized by
    /// ExplorerTurn. They never replay or reconcile principal Session skill selections.</summary>
    public static SkillTurnCompositionSnapshot CreateDelegatedSnapshot(ITokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return new([], [], [], [], new ContextMaterializer(counter, []), new SkillCompletionValidator([]));
    }

    private void ReconcileStaleActivations(IReadOnlyList<DiscoveredSkill> candidates)
    {
        foreach (var (identity, activation) in _store.ReadDetailed(_session).ToArray())
        {
            var matches = candidates.Where(candidate => candidate.Scope == identity.Scope
                && candidate.Manifest.Id == identity.Id).ToArray();
            var unchanged = matches.Length == 1 && matches[0].State is not (SkillLifecycleState.Conflict or SkillLifecycleState.Released)
                && matches[0].ManifestSha256 == activation.ManifestSha256
                && (activation.Origin != "Automatic" || matches[0].State == SkillLifecycleState.Eligible);
            if (unchanged) continue;
            var emitLifecycle = _emitLifecycle ?? throw new InvalidOperationException(
                "Stale skill release requires a durable Session lifecycle event sink.");
            _store.Release(_session, identity.Scope, identity.Id);
            try
            {
                emitLifecycle(new SkillActivationChanged(identity.Id, identity.Scope,
                    activation.ManifestSha256, Activated: false, Origin: activation.Origin));
            }
            catch
            {
                _store.Activate(_session, identity.Scope, identity.Id, activation.ManifestSha256,
                    activation.PackageContentSha256, activation.Origin);
                throw;
            }
        }
    }
}

public sealed record SkillTurnCompositionSnapshot(IReadOnlyList<DiscoveredSkill> Candidates,
    IReadOnlyList<LoadedSkill> LoadedSkills, IReadOnlyList<ActiveSkillFingerprint> Fingerprints,
    IReadOnlyList<string> ToolIds, ContextMaterializer Materializer,
    SkillCompletionValidator Validators);
