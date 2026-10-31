namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Metadata-only slash commands; human activation occurs in the normal prompt's Turn composition.</summary>
public static class SkillInvocationCommands
{
    public static void Register(CommandRegistry registry, IEnumerable<DiscoveredSkill> candidates)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(candidates);
        var skills = candidates.Where(skill => !string.IsNullOrWhiteSpace(skill.Manifest.Id)
            && skill.State is not (SkillLifecycleState.Conflict or SkillLifecycleState.Released)
            && skill.Trust is not (TrustLevel.Untrusted or TrustLevel.ThirdParty)
            && (skill.InvocationRestrictions?.AllowUserInvocation ?? true)).ToArray();
        var existing = registry.Descriptors();
        var occupied = existing.SelectMany(command => new[] { command.Name }.Concat(command.Aliases))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = existing.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        var duplicated = skills.GroupBy(skill => SkillHumanInvocationResolver.CommandName(skill.Manifest.Id!),
                StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills.OrderBy(skill => skill.Manifest.Id, StringComparer.Ordinal).ThenBy(skill => skill.Scope))
        {
            var id = skill.Manifest.Id!;
            var owner = "skill.invoke." + skill.Scope.ToString().ToLowerInvariant() + "." + id;
            if (existing.Any(command => command.Source.Owner == owner)) continue;
            var name = SkillHumanInvocationResolver.CommandName(id);
            if (duplicated.Contains(name) || occupied.Contains(name))
                name += "-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(skill.Scope + ":" + id)))[..8];
            if (ids.Contains(owner + ":" + name)) continue;
            var aliases = ValidName(id) && !CommandRegistry.IsReserved(id) && !occupied.Contains(id)
                && skills.Count(other => string.Equals(other.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)) == 1
                ? new[] { id } : [];
            var handler = new Handler(skill, owner, name, aliases);
            try
            {
                registry.Register(handler);
                occupied.Add(name);
                foreach (var alias in aliases) occupied.Add(alias);
                ids.Add(handler.Descriptor.Id);
            }
            catch (InvalidOperationException) { /* Existing names retain ownership; no skill wins by iteration order. */ }
        }
    }

    private static bool ValidName(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private sealed class Handler : ICommandHandler
    {
        private readonly string _skillId;
        private readonly ScopeLevel _scope;
        public CommandDescriptor Descriptor { get; }

        public Handler(DiscoveredSkill skill, string owner, string name, IReadOnlyList<string> aliases)
        {
            _skillId = skill.Manifest.Id!;
            _scope = skill.Scope;
            var source = new ComponentSource(SourceKind.Skill, skill.Scope, skill.Trust,
                owner, skill.Manifest.Version!, skill.ManifestSha256);
            Descriptor = new(owner + ":" + name, name, aliases, CommandKind.Prompt,
                "Use skill " + _skillId + " (" + _scope + ").", ["<objective>"], source)
            {
                SourceFingerprint = skill.PortableContentSha256 ?? skill.ParentPackageSha256 ?? skill.ManifestSha256,
                UiDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["es"] = "Usar skill " + _skillId + " (" + _scope + ").",
                    ["en"] = "Use skill " + _skillId + " (" + _scope + ").",
                    ["fr"] = "Utiliser la compétence " + _skillId + " (" + _scope + ").",
                    ["it"] = "Usare la skill " + _skillId + " (" + _scope + ").",
                },
            };
            Descriptor.Validate();
        }

        public ValueTask<HostCommandOutcome> HandleAsync(IReadOnlyList<string> arguments, CommandContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.Sum(argument => (long)argument.Length) > 65_536)
                throw new InvalidOperationException("Skill command objective exceeds the input limit.");
            return ValueTask.FromResult<HostCommandOutcome>(new PromptCommandRequested(
                "Use skill " + _scope + ":" + _skillId + ".\n" + string.Join(" ", arguments),
                "PromptCommand(" + Descriptor.Id + "@" + Descriptor.Source.Version + ")"));
        }
    }
}
