using OmniCore.Protocol;

namespace OmniCore.Client;

/// <summary>Renderer adapter for a qualified Host command. Availability and authority must be revalidated.</summary>
public interface IClientCommandHandler
{
    ValueTask<ClientActionResult> ExecuteAsync(CommandInvocation invocation, ClientContext context, CancellationToken cancellationToken);
}

/// <summary>Executes an already resolved binding through the client's existing typed action handler.</summary>
public sealed class KeyBindingDispatcher(IClientActionHandler handler, ClientCommandRegistry? commands = null,
    IClientCommandHandler? hostCommands = null)
{
    public ValueTask<ClientActionResult> ExecuteAsync(KeyBindingResolution resolution, ClientContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resolution is not { Status: KeyBindingResolutionStatus.Matched, Binding: { } binding })
            throw new InvalidOperationException("Only an unambiguous complete binding can be dispatched.");
        var target = binding.Definition.Target;
        var origin = "KeyBinding:" + binding.Definition.SourceId;
        var action = new ClientActionInvocation(target.Id, target.Arguments, origin);
        if (target.Kind == KeyBindingTargetKind.Command)
        {
            var invocation = new CommandInvocation(target.Id, target.Arguments, origin);
            if (!(commands ?? ClientCommandRegistry.Default).TryResolve(invocation, out var commandAction) || commandAction is null)
            {
                if (hostCommands is not null) return hostCommands.ExecuteAsync(invocation, context, cancellationToken);
                throw new InvalidOperationException("The command is not available in the client registry.");
            }
            action = commandAction;
        }
        return handler.ExecuteAsync(action, context, cancellationToken);
    }
}
