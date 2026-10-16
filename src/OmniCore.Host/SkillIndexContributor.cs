namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Security;

/// <summary>Small metadata-only index; package instructions are loaded only after a skill.load call.</summary>
public sealed class SkillIndexContributor(SessionId session, IReadOnlyList<DiscoveredSkill> candidates,
    bool enabled, int contextTokenBudget = 1_024) : IContextContributor
{
    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled || request.SessionId != session) return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);
        var visible = candidates.Where(item => item.State == SkillLifecycleState.Eligible
                || item.Manifest.ModelInvocable)
            .Where(item => item.InvocationRestrictions?.AllowModelInvocation is not false)
            .Where(item => item.State is not (SkillLifecycleState.Conflict or SkillLifecycleState.Released)
                && item.Trust is not (TrustLevel.Untrusted or TrustLevel.ThirdParty))
            .OrderByDescending(item => item.Scope).ThenByDescending(item => item.Manifest.Priority)
            .ThenBy(item => item.Manifest.Id, StringComparer.Ordinal).Take(32).ToArray();
        if (visible.Length == 0) return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);
        var redaction = new RedactionPolicy();
        const string header = "Eligible skills may be loaded for the next turn with skill.load({\"id\":\"<id>\"}):\n";
        if (contextTokenBudget <= 0 || EstimateTokens(header) > contextTokenBudget)
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);
        var lines = new List<string>();
        foreach (var item in visible)
        {
            var description = (item.Description ?? item.Manifest.DisplayName ?? "").Trim();
            if (description.Length > 512) description = description[..512];
            if (redaction.Redact(description) != description) continue;
            var line = "- " + item.Manifest.Id + " (" + item.Scope + ", budget "
                + item.Manifest.BudgetTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " tokens): " + description;
            var candidateLines = lines.Append(line).ToArray();
            if (EstimateTokens(header + string.Join("\n", candidateLines)) <= contextTokenBudget)
                lines.Add(line);
        }
        if (lines.Count == 0) return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);
        var content = header + string.Join("\n", lines);
        var tokens = EstimateTokens(content);
        IReadOnlyList<ContextItem> result = [new ContextItem("skills.index", ContextItemKind.Knowledge,
            content, tokens, ContextPriority.Normal, RetentionPolicy.RegenerateEachTurn,
            new ContextProvenance("skills.index", ContributionCategory.Skills,
                "SkillIndex:Session", ScopeLevel.Session, false))];
        return System.Threading.Tasks.Task.FromResult(result);
    }

    private static int EstimateTokens(string content) => Math.Max(1, (content.Length + 3) / 4);
}
