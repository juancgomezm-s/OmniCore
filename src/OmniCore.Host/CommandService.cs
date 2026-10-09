namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Protocol;

/// <summary>Host command registry for PromptCommand and WorkflowCommand (ADR-0024 §3).</summary>
public sealed class CommandRegistry
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "plan", "tasks", "mode", "cancel", "interrupt", "resume", "permissions", "context",
        "help", "exit", "commands", "tools", "agents", "delegate", "join",
    };

    private readonly Dictionary<string, ICommandHandler> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ICommandHandler>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ICommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var descriptor = handler.Descriptor;
        descriptor.Validate();
        if (_byId.ContainsKey(descriptor.Id))
            throw new InvalidOperationException("CommandRegistrationRejected: duplicate canonical id " + descriptor.Id);
        var names = new[] { descriptor.Name }.Concat(descriptor.Aliases).ToArray();
        foreach (var name in names)
        {
            if (ReservedNames.Contains(name))
                throw new InvalidOperationException("CommandRegistrationRejected: reserved Core command " + name);
            if (_byName.TryGetValue(name, out var candidates) && candidates.Any(candidate => candidate.Descriptor.Source.Scope == descriptor.Source.Scope
                && candidate.Descriptor.Source.Trust == descriptor.Source.Trust))
                throw new InvalidOperationException("CommandRegistrationRejected: ambiguous short name " + name);
        }
        // Mutate only after every name has passed validation; failed registrations leave no aliases behind.
        foreach (var name in names)
        {
            if (!_byName.TryGetValue(name, out var candidates)) _byName[name] = candidates = [];
            candidates.Add(handler);
        }
        _byId.Add(descriptor.Id, handler);
    }

    public IReadOnlyList<CommandDescriptor> Descriptors() => _byId.Values.Select(item => item.Descriptor)
        .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();

    public ICommandHandler Resolve(string name)
    {
        if (_byId.TryGetValue(name, out var canonical)) return canonical;
        if (!_byName.TryGetValue(name, out var candidates))
            throw new InvalidOperationException("command.prompt_not_found:" + name);
        if (candidates.Count != 1) throw new InvalidOperationException("command.ambiguous:" + name);
        return candidates[0];
    }
}

public sealed class CommandService
{
    private readonly CommandRegistry _registry;

    public CommandService() : this(CreateDefaultRegistry()) { }

    public CommandService(CommandRegistry registry) => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public CommandRegistry Registry => _registry;

    public async ValueTask<HostCommandOutcome> InvokeAsync(CommandInvocation invocation, CommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var handler = _registry.Resolve(invocation.Name);
        if (!handler.IsEnabled(context, out var reason))
            throw new InvalidOperationException("command.disabled:" + (reason ?? "Unavailable in the current Run."));
        return await handler.HandleAsync(invocation.Arguments, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compatibility adapter for the existing PromptCommand call path.</summary>
    public PromptExpanded Expand(CommandInvocation invocation)
    {
        var outcome = InvokeAsync(invocation, new CommandContext(null, null, null, null), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (outcome is not PromptCommandRequested prompt)
            throw new InvalidOperationException("command.not_a_prompt:" + invocation.Name);
        return new PromptExpanded(prompt.Text, prompt.Origin);
    }

    public CommandCatalogSnapshot Catalog(CommandContext? context = null) => new(_registry.Descriptors().Select(descriptor =>
    {
        var handler = _registry.Resolve(descriptor.Id);
        var enabled = handler.IsEnabled(context ?? new CommandContext(null, null, null, null), out var reason);
        return new CommandCatalogEntry(descriptor.Id, descriptor.Name, descriptor.Aliases,
            descriptor.Kind.ToString(), descriptor.Description, descriptor.Arguments,
            descriptor.Source.Kind + ":" + descriptor.Source.Owner + "@" + descriptor.Source.Version,
            enabled, enabled ? null : reason);
    }).ToArray());

    private static CommandRegistry CreateDefaultRegistry()
    {
        var registry = new CommandRegistry();
        var source = new ComponentSource(SourceKind.BuiltIn, ScopeLevel.BuiltIn, TrustLevel.Core, "core", "1");
        registry.Register(new ExplainHandler(source));
        registry.Register(new OrqAuthWorkflowHandler(source));
        return registry;
    }

    private sealed class ExplainHandler(ComponentSource source) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("core:explain", "explain", [], CommandKind.Prompt,
            "Explain repository content at a path.", ["path"], source);

        public ValueTask<HostCommandOutcome> HandleAsync(IReadOnlyList<string> arguments, CommandContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = arguments.Count == 0 ? "." : string.Join(" ", arguments);
            var prompt = "Explain the repository content at the path " + target
                + ". Ground the explanation in files you inspect; distinguish observed facts from inference."
                + " Respond in Spanish unless the user explicitly requests another language.";
            return ValueTask.FromResult<HostCommandOutcome>(new PromptCommandRequested(prompt,
                "PromptCommand(core:explain@1)"));
        }
    }

    private sealed class OrqAuthWorkflowHandler(ComponentSource source) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("core:orq-auth", "orq-auth", [], CommandKind.Workflow,
            "Explore, implement and verify an explicitly requested integration change.", ["objective"], source);

        public bool IsEnabled(CommandContext context, out string? disabledReason)
        {
            var enabled = context.WorkflowAuthorized && context.Session is not null && context.Run is not null
                && context.Mode == RunMode.Orchestrate;
            disabledReason = enabled ? null : context.WorkflowDisabledReason
                ?? "An active Orchestrate Run with current authorization is required.";
            return enabled;
        }

        public ValueTask<HostCommandOutcome> HandleAsync(IReadOnlyList<string> arguments, CommandContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.Count == 0 || string.IsNullOrWhiteSpace(string.Join(" ", arguments)))
                throw new ArgumentException("/orq-auth requires an objective.");
            var outcome = new WorkflowRequested(new WorkflowRef("core:explore-implement-verify", "1"), arguments.ToArray());
            outcome.Validate();
            return ValueTask.FromResult<HostCommandOutcome>(outcome);
        }
    }
}
