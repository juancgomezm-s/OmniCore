namespace OmniCore.Host;

using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Materializes only explicitly activated Project skill instructions for their owning Session.</summary>
public sealed class SkillContextContributor(SessionId session, LoadedSkill loaded) : IContextContributor
{
    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SessionId != session) return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);
        var manifest = loaded.Skill.Manifest;
        var id = manifest.Id!;
        var tokens = Math.Max(1, (loaded.Instructions.Length + 3) / 4);
        var priority = manifest.Priority >= 50 ? ContextPriority.High : ContextPriority.Normal;
        IReadOnlyList<ContextItem> items = [new ContextItem("skill." + id, ContextItemKind.Skill,
            "Instructions from Project skill " + id + " (" + manifest.Version + "):\n" + loaded.Instructions,
            tokens, priority, RetentionPolicy.RegenerateEachTurn,
            new ContextProvenance("skills." + id, ContributionCategory.Skills,
                "Extension:Project:" + loaded.Skill.Trust + ":" + id + "@" + manifest.Version,
                ScopeLevel.Project, false, ["manifest-sha256=" + loaded.Skill.ManifestSha256,
                    "instructions-sha256=" + loaded.ContentSha256]))];
        return System.Threading.Tasks.Task.FromResult(items);
    }
}
