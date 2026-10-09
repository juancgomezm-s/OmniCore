namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>Server command descriptor and handler contracts (ADR-0024 §3).</summary>
public enum CommandKind
{
    Prompt,
    Workflow,
}

public sealed record CommandDescriptor(string Id, string Name, IReadOnlyList<string> Aliases,
    CommandKind Kind, string Description, IReadOnlyList<string> Arguments, ComponentSource Source)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(Aliases);
        ArgumentNullException.ThrowIfNull(Arguments);
        ArgumentNullException.ThrowIfNull(Source);
        if (Id != Source.Owner + ":" + Name || Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            || Aliases.Any(alias => string.IsNullOrWhiteSpace(alias)
                || alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            || Aliases.Contains(Name, StringComparer.OrdinalIgnoreCase)
            || Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Aliases.Count
            || Arguments.Any(argument => string.IsNullOrWhiteSpace(argument)))
            throw new ArgumentException("Command descriptor identity or arguments are invalid.");
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
    }
}

public sealed record WorkflowRef(string Id, string Version)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Id.Contains(':', StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Version)) throw new ArgumentException("Workflow reference is invalid.");
    }
}

/// <summary>Command handler output. A workflow request is data; only the Host executes it.</summary>
public abstract record HostCommandOutcome;

public sealed record PromptCommandRequested(string Text, string Origin) : HostCommandOutcome;

public sealed record WorkflowRequested(WorkflowRef Workflow, IReadOnlyList<string> Arguments) : HostCommandOutcome
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Workflow);
        Workflow.Validate();
        ArgumentNullException.ThrowIfNull(Arguments);
        if (Arguments.Any(argument => argument is null || argument.Length > 16_384))
            throw new ArgumentException("Workflow arguments are invalid.");
    }
}

public sealed record CommandContext(SessionId? Session, RunId? Run, RunMode? Mode, string? WorkspaceRoot,
    bool WorkflowAuthorized = false, string? WorkflowDisabledReason = null);

public interface ICommandHandler
{
    CommandDescriptor Descriptor { get; }

    bool IsEnabled(CommandContext context, out string? disabledReason)
    {
        disabledReason = null;
        return true;
    }

    ValueTask<HostCommandOutcome> HandleAsync(IReadOnlyList<string> arguments, CommandContext context,
        CancellationToken cancellationToken);
}
