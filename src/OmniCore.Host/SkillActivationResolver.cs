namespace OmniCore.Host;

using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Revalidates durable Session selections and materializes only their current package identities.</summary>
public sealed class SkillActivationResolver
{
    public IReadOnlyList<LoadedSkill> Load(string root, SessionId session,
        SessionSkillActivationStore store, bool workspaceTrusted)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!workspaceTrusted) return [];
        var loader = new SkillManifestLoader();
        var candidates = SkillManifestLoader.ResolvePrecedence(loader.Discover(root,
            ScopeLevel.Project, TrustLevel.Project, workspaceTrusted: true));
        return LoadCandidates(candidates, session, store, workspaceTrusted);
    }

    public IReadOnlyList<LoadedSkill> LoadCandidates(IEnumerable<DiscoveredSkill> candidates, SessionId session,
        SessionSkillActivationStore store, bool workspaceTrusted = true, SkillSelectionLimits? limits = null,
        IReadOnlySet<SkillActivationIdentity>? replacing = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(store);
        var loader = new SkillManifestLoader();
        var active = store.ReadDetailed(session);
        var resolved = candidates.ToArray();
        var selectedCandidates = new List<DiscoveredSkill>();
        foreach (var (identity, hash) in active.OrderBy(pair => pair.Key))
        {
            if (replacing?.Contains(identity) == true) continue;
            var matches = resolved.Where(skill => skill.Scope == identity.Scope
                && string.Equals(skill.Manifest.Id, identity.Id, StringComparison.Ordinal)
                && skill.State is not (SkillLifecycleState.Conflict or SkillLifecycleState.Released)).ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].ManifestSha256,
                    hash.ManifestSha256, StringComparison.Ordinal))
                throw new InvalidDataException("An active skill is no longer available or its package identity changed.");
            selectedCandidates.Add(matches[0] with { State = SkillLifecycleState.Activated });
        }
        var selection = new SkillSelector().Select(selectedCandidates, SkillSelectionRequestKind.SessionReplay,
            limits ?? new SkillSelectionLimits(6, 16_384));
        // Persisted Session selections may outlive a smaller model/harness budget. Keep the
        // Session usable and admit only the deterministic prefix selected under this Turn's
        // current ceiling; invalid or changed identities still fail closed above.
        var result = new List<LoadedSkill>();
        foreach (var candidate in selection.Selected)
        {
            var loaded = loader.Activate(candidate);
            var activation = active[new SkillActivationIdentity(candidate.Scope, candidate.Manifest.Id!)];
            if (!string.Equals(activation.PackageContentSha256, activation.ManifestSha256, StringComparison.Ordinal)
                && !string.Equals(activation.PackageContentSha256, loaded.PackageContentSha256, StringComparison.Ordinal))
                throw new InvalidDataException("An active skill contribution changed after activation; activate it again.");
            result.Add(loaded with { Skill = loaded.Skill with { State = SkillLifecycleState.Loaded } });
        }
        return result;
    }
}
