namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>An explicitly activated skill may expose only a fixed Host-compiled workflow request.</summary>
internal sealed class SkillWorkflowCommandHandler : ICommandHandler
{
    private static readonly WorkflowRef SupportedWorkflow = new("core:explore-implement-verify", "1");
    private readonly string _id;
    private readonly string _displayName;

    public CommandDescriptor Descriptor { get; }

    public SkillWorkflowCommandHandler(LoadedSkill skill, LoadedSkillResource resource)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Kind != SkillResourceKind.Workflow) throw new ArgumentException("Resource is not a workflow.", nameof(resource));
        using var document = JsonDocument.Parse(resource.Content, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("workflow", out var workflowNode) || workflowNode.ValueKind != JsonValueKind.String
            || workflowNode.GetString() != SupportedWorkflow.Id
            || !root.TryGetProperty("descriptions", out var descriptions) || descriptions.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A skill workflow must bind the supported compiled Host workflow and localized descriptions.");
        _id = idNode.GetString()!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(_id, "^[a-z0-9][a-z0-9-]{0,39}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Skill workflow identity is invalid.");
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (locale, value) in new[] { "es", "en", "fr", "it" }
            .Select(locale => (locale, descriptions.TryGetProperty(locale, out var value) ? value : default)))
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                || value.GetString()!.Length > 256)
                throw new InvalidDataException("A skill workflow requires bounded descriptions in es/en/fr/it.");
            translations.Add(locale, value.GetString()!);
        }
        if (root.EnumerateObject().Any(property => property.Name is not ("id" or "workflow" or "descriptions")))
            throw new InvalidDataException("Skill workflow has unsupported fields.");
        var name = "skill-" + skill.Skill.Manifest.Id!.Replace('.', '-') + "-" + _id;
        if (name.Length > 64 || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidDataException("Skill workflow command name is invalid.");
        _displayName = skill.Skill.Manifest.DisplayName ?? skill.Skill.Manifest.Id!;
        var source = new ComponentSource(SourceKind.Skill, skill.Skill.Scope, skill.Skill.Trust,
            "skill." + skill.Skill.Manifest.Id, skill.Skill.Manifest.Version!, skill.PackageContentSha256);
        Descriptor = new CommandDescriptor(source.Owner + ":" + name, name, [], CommandKind.Workflow,
            "Run an explicitly activated skill workflow.",
            ["<objective> --verify-executable <path> --verify-argv-json <JSON array>"], source)
        { UiDescriptions = translations };
        Descriptor.Validate();
    }

    public bool IsEnabled(CommandContext context, out string? disabledReason)
    {
        var enabled = context.WorkflowAuthorized && context.Session is not null && context.Run is not null
            && context.Mode == RunMode.Orchestrate;
        disabledReason = enabled ? null : context.WorkflowDisabledReason
            ?? "An authorized Orchestrate Run is required for skill workflows.";
        return enabled;
    }

    public ValueTask<HostCommandOutcome> HandleAsync(IReadOnlyList<string> arguments, CommandContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsEnabled(context, out var reason)) throw new InvalidOperationException("command.disabled:" + reason);
        _ = WorkflowRequestSpecification.Parse(arguments, context.WorkspaceRoot ?? Directory.GetCurrentDirectory());
        var request = new WorkflowRequested(SupportedWorkflow, arguments.ToArray());
        request.Validate();
        return ValueTask.FromResult<HostCommandOutcome>(request);
    }
}

internal static class SessionSkillWorkflowCommands
{
    public static void Refresh(CommandRegistry registry, SessionId? session, string? workspaceRoot,
        string userDataDirectory, string? workspaceDataDirectory, bool workspaceTrusted,
        IEnumerable<(DiscoveredExtension Extension, IReadOnlySet<string> Capabilities)>? extensionPackages = null)
    {
        registry.RemoveSource(SourceKind.Skill);
        if (session is not { } sessionId) return;
        var store = new SessionSkillActivationStore(workspaceDataDirectory ?? userDataDirectory);
        var catalog = new SkillSourceCatalog();
        var discovered = extensionPackages is null
            ? catalog.Discover(workspaceRoot, userDataDirectory, workspaceDataDirectory, sessionId, workspaceTrusted)
            : catalog.DiscoverWithExtensions(workspaceRoot, userDataDirectory, workspaceDataDirectory,
                sessionId, workspaceTrusted, extensionPackages);
        var active = new SkillActivationResolver().LoadCandidates(discovered, sessionId, store);
        foreach (var skill in active)
        foreach (var resource in skill.Resources ?? [])
        {
            if (resource.Kind != SkillResourceKind.Workflow) continue;
            try { registry.Register(new SkillWorkflowCommandHandler(skill, resource)); }
            catch (InvalidOperationException) { /* A conflict stays unavailable; it never wins by order. */ }
        }
    }
}
