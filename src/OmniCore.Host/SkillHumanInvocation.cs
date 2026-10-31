namespace OmniCore.Host;

using System.Text.RegularExpressions;
using OmniCore.Domain;
using OmniCore.Extensions;

public sealed record SkillHumanInvocationResolution(IReadOnlyList<DiscoveredSkill> Skills,
    IReadOnlyList<string> Diagnostics);

/// <summary>Resolves human-written names without reading instruction bodies or granting tool permissions.</summary>
public static class SkillHumanInvocationResolver
{
    private static readonly Regex NonMentionData = new(
        @"(?<![\p{L}\p{N}_])(?:[a-z][a-z0-9+.-]{0,31}://|www\.)[^\s<>""'`]+|[\p{L}\p{N}.!#$%&'*+/=?^_`{|}~-]{1,256}@[\p{L}\p{N}](?:[\p{L}\p{N}.-]{0,253}[\p{L}\p{N}])?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static bool InsideDataSpan((int Start, int End)[] spans, int start, int end)
    {
        var low = 0;
        var high = spans.Length - 1;
        var previous = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (spans[middle].Start <= start) { previous = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return previous >= 0 && spans[previous].End >= end;
    }
    public static string CommandName(string id) => "skill-" + id.Replace('.', '-');

    public static SkillHumanInvocationResolution Resolve(string text, IEnumerable<DiscoveredSkill> candidates,
        IEnumerable<string>? occupiedNames = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(candidates);
        if (text.Length > 131_072)
            throw new InvalidOperationException("Skill invocation text exceeds the discovery limit.");
        var dataSpans = NonMentionData.Matches(text).Cast<Match>()
            .Select(match => (Start: match.Index, End: match.Index + match.Length)).ToArray();
        var occupied = new HashSet<string>(occupiedNames ?? [], StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, List<DiscoveredSkill>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, DiscoveredSkill skill)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name.Any(char.IsControl)) return;
            if (!names.TryGetValue(name, out var matches)) names[name] = matches = [];
            if (!matches.Any(match => match.Scope == skill.Scope && match.Manifest.Id == skill.Manifest.Id
                    && match.ManifestSha256 == skill.ManifestSha256)) matches.Add(skill);
        }
        foreach (var skill in candidates.Take(1024))
        {
            if (skill.State == SkillLifecycleState.Released || string.IsNullOrWhiteSpace(skill.Manifest.Id)) continue;
            Add(skill.Manifest.Id, skill);
            Add(skill.Manifest.DisplayName, skill);
            Add(CommandName(skill.Manifest.Id), skill);
            Add(skill.Scope + ":" + skill.Manifest.Id, skill);
        }
        var selected = new Dictionary<SkillActivationIdentity, DiscoveredSkill>();
        var diagnostics = new List<string>();
        var occurrences = new List<(string Name, List<DiscoveredSkill> Skills, int Start, int End)>();
        foreach (var (name, matches) in names.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var reserved = CommandRegistry.IsReserved(name) || occupied.Contains(name);
            // Core/external command collisions need an explicit $name, /skill-name or Scope:id.
            var prefix = reserved ? @"(?:\$)" : @"(?:\$|/)?";
            var pattern = @"(?<![\p{L}\p{N}_./\\:-])" + prefix + Regex.Escape(name)
                + @"(?![\p{L}\p{N}_/\\-]|[.:][\p{L}\p{N}_])";
            foreach (Match occurrence in Regex.Matches(text, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                // Names contained in a URL or email are reference data, not a distinct human invocation.
                if (InsideDataSpan(dataSpans, occurrence.Index, occurrence.Index + occurrence.Length)) continue;
                if (occurrences.Count >= 16_384)
                    throw new InvalidOperationException("Too many skill-name occurrences in one prompt.");
                occurrences.Add((name, matches, occurrence.Index, occurrence.Index + occurrence.Length));
            }
        }
        // For complete multiword names, use the longest spelling at a given position. A shorter
        // skill name contained inside that spelling is not a second invocation; a later standalone
        // mention remains independent. Sorting makes containment linear after the bounded scan.
        var furthestEnd = -1;
        foreach (var occurrence in occurrences.OrderBy(item => item.Start).ThenByDescending(item => item.End))
        {
            if (occurrence.End <= furthestEnd) continue;
            furthestEnd = Math.Max(furthestEnd, occurrence.End);
            var (name, matches, _, _) = occurrence;
            if (matches.Count != 1 || matches[0].State == SkillLifecycleState.Conflict)
            {
                diagnostics.Add("Ambiguous skill name '" + name + "'. Use "
                    + string.Join(" or ", matches.Select(skill => skill.Scope + ":" + skill.Manifest.Id)
                        .Distinct(StringComparer.Ordinal)) + ".");
                continue;
            }
            var match = matches[0];
            selected[new(match.Scope, match.Manifest.Id!)] = match;
        }
        return new(selected.Values.OrderBy(skill => skill.Manifest.Id, StringComparer.Ordinal)
            .ThenBy(skill => skill.Scope).ToArray(), diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Human activation at a settled Turn boundary using the existing loader, selection budgets and journal.</summary>
public static class SkillHumanInvocationActivation
{
    public static IReadOnlyList<string> Activate(string text, IReadOnlyList<DiscoveredSkill> candidates,
        SessionId session, SessionSkillActivationStore store, SkillSelectionLimits limits, bool workspaceTrusted,
        Action<SkillActivationChanged> emitLifecycle, IEnumerable<string>? occupiedNames = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(emitLifecycle);
        var resolution = SkillHumanInvocationResolver.Resolve(text, candidates, occupiedNames);
        if (resolution.Diagnostics.Count != 0)
            throw new InvalidOperationException(string.Join("\n", resolution.Diagnostics));
        if (resolution.Skills.Count == 0) return [];
        var selector = new SkillSelector();
        var loader = new SkillManifestLoader();
        var loaded = new List<LoadedSkill>();
        foreach (var skill in resolution.Skills)
        {
            if (skill.Scope == ScopeLevel.Project && !workspaceTrusted)
                throw new InvalidOperationException("Trust the workspace before activating Project skills.");
            var selection = selector.Select([skill], SkillSelectionRequestKind.UserExplicit,
                limits, skill.Manifest.Id);
            if (selection.Selected.Count != 1)
                throw new InvalidOperationException("Skill '" + skill.Manifest.Id + "' cannot be activated: "
                    + selection.Decisions[0].Reason);
            loaded.Add(loader.Activate(selection.Selected[0]));
        }
        // Validate the entire proposed set before recording any activation. Replays retain their existing
        // deterministic context admission, but a new explicit invocation must fit its current Turn budget.
        var replacing = loaded.Select(skill => new SkillActivationIdentity(skill.Skill.Scope,
            skill.Skill.Manifest.Id!)).ToHashSet();
        var active = new SkillActivationResolver().LoadCandidates(candidates, session, store,
            limits: new SkillSelectionLimits(int.MaxValue, int.MaxValue), replacing: replacing);
        var proposed = active.Select(item => item.Skill).ToDictionary(
            skill => new SkillActivationIdentity(skill.Scope, skill.Manifest.Id!));
        foreach (var skill in loaded)
            proposed[new(skill.Skill.Scope, skill.Skill.Manifest.Id!)] = skill.Skill with { State = SkillLifecycleState.Activated };
        var admission = selector.Select(proposed.Values, SkillSelectionRequestKind.SessionReplay, limits);
        if (admission.Selected.Count != proposed.Count)
            throw new InvalidOperationException("Requested skills exceed this Turn's skill/context budget; release another skill or use fewer skills.");
        var changed = new List<string>();
        foreach (var skill in loaded)
        {
            var id = skill.Skill.Manifest.Id!;
            var identity = new SkillActivationIdentity(skill.Skill.Scope, id);
            var previous = store.ReadDetailed(session).GetValueOrDefault(identity);
            var packageHash = skill.PackageContentSha256 ?? skill.ContentSha256;
            if (previous is not null && previous.Origin == "User" && previous.ManifestSha256 == skill.Skill.ManifestSha256
                && previous.PackageContentSha256 == packageHash) continue;
            store.Activate(session, skill.Skill.Scope, id, skill.Skill.ManifestSha256, packageHash, "User");
            try { emitLifecycle(new SkillActivationChanged(id, skill.Skill.Scope,
                skill.Skill.ManifestSha256, Activated: true, Origin: "User")); }
            catch
            {
                if (previous is null) store.Release(session, skill.Skill.Scope, id);
                else store.Activate(session, skill.Skill.Scope, id, previous.ManifestSha256,
                    previous.PackageContentSha256, previous.Origin);
                throw;
            }
            changed.Add(id);
        }
        return changed;
    }
}
