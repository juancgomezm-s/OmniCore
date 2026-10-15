namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Security;

/// <summary>Small metadata-only index; package instructions are loaded only after a skill.load call.</summary>
public sealed class SkillIndexContributor(SessionId session, IReadOnlyList<DiscoveredSkill> candidates,
    bool enabled) : IContextContributor
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
        var lines = visible.Select(item =>
        {
            var description = (item.Description ?? "").Trim();
            if (description.Length > 512) description = description[..512];
            if (redaction.Redact(description) != description) return null;
            return "- " + item.Manifest.Id + " (" + item.Scope + ", budget "
                + item.Manifest.BudgetTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " tokens): " + description;
        }).Where(line => line is not null).ToArray();
        if (lines.Length == 0) return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);
        var content = "Eligible skills may be loaded for the next turn with skill.load({\"id\":\"<id>\"}):\n"
            + string.Join("\n", lines);
        var tokens = Math.Max(1, (content.Length + 3) / 4);
        IReadOnlyList<ContextItem> result = [new ContextItem("skills.index", ContextItemKind.System,
            content, tokens, ContextPriority.Normal, RetentionPolicy.RegenerateEachTurn,
            new ContextProvenance("skills.index", ContributionCategory.Skills,
                "SkillIndex:Session", ScopeLevel.Session, false))];
        return System.Threading.Tasks.Task.FromResult(result);
    }
}
