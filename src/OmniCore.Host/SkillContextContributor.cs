namespace OmniCore.Host;

using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Materializes only explicitly activated skill instructions for their owning Session.</summary>
public sealed class SkillContextContributor(SessionId session, LoadedSkill loaded) : IContextContributor
{
    public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SessionId != session) return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([]);
        var manifest = loaded.Skill.Manifest;
        var id = manifest.Id!;
        var priority = manifest.Priority >= 50 ? ContextPriority.High : ContextPriority.Normal;
        var items = new List<ContextItem> { new("skill." + id, ContextItemKind.Skill,
            "Instructions from skill " + id + " (" + manifest.Version + "):\n" + loaded.Instructions,
            Math.Max(1, (loaded.Instructions.Length + 3) / 4), priority, RetentionPolicy.RegenerateEachTurn,
            new ContextProvenance("skills." + id, ContributionCategory.Skills,
                "Skill:" + loaded.Skill.Scope + ":" + loaded.Skill.Trust + ":" + id + "@" + manifest.Version,
                loaded.Skill.Scope, false, ["manifest-sha256=" + loaded.Skill.ManifestSha256,
                    "instructions-sha256=" + loaded.ContentSha256])) };
        foreach (var resource in loaded.Resources ?? [])
        {
            if (resource.Kind == SkillResourceKind.Validator) continue;
            var kind = resource.Kind == SkillResourceKind.Knowledge
                ? ContextItemKind.Knowledge : ContextItemKind.Skill;
            var category = resource.Kind == SkillResourceKind.Knowledge
                ? ContributionCategory.Knowledge : ContributionCategory.Skills;
            var label = resource.Kind switch
            {
                SkillResourceKind.Workflow => "Workflow template",
                SkillResourceKind.Contributor => "Skill context contribution",
                SkillResourceKind.Knowledge => "Knowledge reference",
                _ => throw new ArgumentOutOfRangeException(),
            };
            items.Add(new("skill." + id + "." + resource.Kind.ToString().ToLowerInvariant() + "." + resource.Sha256[..12],
                kind, label + " from skill " + id + ":\n" + resource.Content,
                Math.Max(1, (resource.Content.Length + 3) / 4), ContextPriority.Normal,
                RetentionPolicy.RegenerateEachTurn,
                new ContextProvenance("skills." + id + "." + resource.Kind.ToString().ToLowerInvariant(),
                    category, "Skill:" + loaded.Skill.Scope + ":" + loaded.Skill.Trust + ":" + id,
                    loaded.Skill.Scope, false, ["manifest-sha256=" + loaded.Skill.ManifestSha256,
                        "resource-sha256=" + resource.Sha256])));
        }
        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(items);
    }
}
