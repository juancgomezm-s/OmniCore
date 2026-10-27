using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class HostKeyBindingDispatcherTests
{
    [Fact]
    public async Task Qualified_command_dispatches_literal_argv_only_to_the_explicit_Host_adapter()
    {
        var target = new KeyBindingTarget(KeyBindingTargetKind.Command, "project:inspect", ["a b", "$(literal)", "{\"x\":1}"]);
        var catalog = KeyBindingTargetCatalog.Core.WithTargets([new(target.CatalogKey, "Project:fixture@1")]);
        var service = new KeyBindingService([new("F8", target, Source: KeyBindingSource.User, SourceId: "User")], catalog);
        Assert.True(KeyChord.TryParseSequence("F8", out var keys));
        var resolved = service.Resolve(keys, new([]));
        var adapter = new HostAdapter();
        await new KeyBindingDispatcher(adapter, hostCommands: adapter).ExecuteAsync(resolved,
            new(null!, new("es")), TestContext.Current.CancellationToken);
        var invocation = Assert.Single(adapter.Commands);
        Assert.Equal("project:inspect", invocation.Name); Assert.Equal(target.Arguments, invocation.Arguments);
        Assert.Equal("KeyBinding:User", invocation.InvocationOrigin); Assert.Equal(0, adapter.ActionCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new KeyBindingDispatcher(adapter)
            .ExecuteAsync(resolved, new(null!, new("en")), TestContext.Current.CancellationToken).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new KeyBindingDispatcher(adapter, hostCommands: adapter)
            .ExecuteAsync(resolved, new(null!, new("en")), cancelled.Token).AsTask());
        Assert.Single(adapter.Commands);
    }

    [Fact]
    public void Dynamic_catalog_ownership_never_allows_extensions_to_claim_Core_or_another_source()
    {
        var catalog = KeyBindingTargetCatalog.Core.WithTargets([new("Command:core:explain", "Core"), new("Command:project:inspect", "Project:fixture@1")]);
        var service = new KeyBindingService([
            new("F8", new(KeyBindingTargetKind.Command, "core:explain"), Source: KeyBindingSource.Extension, SourceId: "Core"),
            new("F9", new(KeyBindingTargetKind.Command, "project:inspect"), Source: KeyBindingSource.Extension, SourceId: "other")], catalog);
        Assert.Equal(2, service.Diagnostics.Count(issue => issue.Code == "extension_ownership"));
        Assert.DoesNotContain(service.Bindings, binding => binding.Definition.Source == KeyBindingSource.Extension);
    }

    private sealed class HostAdapter : IClientCommandHandler, IClientActionHandler
    {
        internal readonly List<CommandInvocation> Commands = [];
        internal int ActionCalls;
        public ValueTask<ClientActionResult> ExecuteAsync(CommandInvocation invocation, ClientContext context, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Commands.Add(invocation); return ValueTask.FromResult(new ClientActionResult("ok")); }
        public ValueTask<ClientActionResult> ExecuteAsync(ClientActionInvocation invocation, ClientContext context, CancellationToken cancellationToken)
        { ActionCalls++; return ValueTask.FromResult(new ClientActionResult("")); }
    }
}
