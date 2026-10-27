using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class CommandRegistryTests
{
    private static readonly ComponentSource Source = new(SourceKind.BuiltIn, ScopeLevel.BuiltIn,
        TrustLevel.Core, "tests", "1");

    [Fact]
    public void Failed_registration_does_not_leave_name_or_alias_and_reserved_alias_is_rejected()
    {
        var registry = new CommandRegistry();
        var invalid = new Handler(new("tests:helper", "helper", ["plan"], CommandKind.Prompt,
            "test", [], Source));

        Assert.Throws<InvalidOperationException>(() => registry.Register(invalid));
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("helper"));
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("plan"));

        registry.Register(new Handler(new("tests:helper", "helper", [], CommandKind.Prompt,
            "test", [], Source)));
        Assert.Equal("tests:helper", registry.Resolve("helper").Descriptor.Id);
    }

    [Fact]
    public void Short_name_precedence_is_scope_ordered_but_a_less_trusted_source_cannot_hide_a_better_trusted_one()
    {
        var registry = new CommandRegistry();
        var user = new Handler(new("user:review", "review", [], CommandKind.Prompt, "user",
            [], new ComponentSource(SourceKind.Extension, ScopeLevel.User, TrustLevel.Trusted, "user", "1")));
        var workspace = new Handler(new("workspace:review", "review", [], CommandKind.Prompt, "workspace",
            [], new ComponentSource(SourceKind.Extension, ScopeLevel.Workspace, TrustLevel.Trusted, "workspace", "1")));
        registry.Register(user);
        registry.Register(workspace);

        Assert.Same(workspace, registry.Resolve("review"));
        Assert.Same(user, registry.Resolve("user:review"));

        var lessTrustedWorkspace = new Handler(new("project:review", "review", [], CommandKind.Prompt, "project",
            [], new ComponentSource(SourceKind.Extension, ScopeLevel.Workspace, TrustLevel.Project, "project", "1")));
        var conflict = new CommandRegistry();
        conflict.Register(user);
        conflict.Register(lessTrustedWorkspace);
        Assert.Throws<InvalidOperationException>(() => conflict.Resolve("review"));

        var catalog = new CommandService(conflict).Catalog();
        Assert.All(catalog.Commands, item =>
        {
            Assert.False(item.Enabled);
            Assert.True(item.CanonicalEnabled);
            Assert.Contains("ambiguous", item.DisabledReason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task Workflow_catalog_reports_authorization_and_request_returns_typed_workflow_intent()
    {
        var service = new CommandService();
        var disabled = service.Catalog(new CommandContext(null, null, null, null));
        var workflow = Assert.Single(disabled.Commands, command => command.Id == "core:orq-auth");
        Assert.False(workflow.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(workflow.DisabledReason));

        var context = new CommandContext(SessionId.New(), RunId.New(), RunMode.Orchestrate, ".", true);
        var enabled = service.Catalog(context).Commands.Single(command => command.Id == "core:orq-auth");
        Assert.True(enabled.Enabled);
        Assert.Null(enabled.DisabledReason);

        var arguments = new[] { "check", "integration", "--verify-executable", "dotnet",
            "--verify-argv-json", "[\"test\",\"tests/Connected.csproj\"]" };
        var result = await service.InvokeAsync(new CommandInvocation("orq-auth", arguments, "Typed"),
            context, CancellationToken.None);
        var request = Assert.IsType<WorkflowRequested>(result);
        Assert.Equal("core:explore-implement-verify", request.Workflow.Id);
        Assert.Equal(arguments, request.Arguments);

        using var json = JsonDocument.Parse(CommandCatalogJson.Encode(service.Catalog(context)));
        Assert.Contains(json.RootElement.GetProperty("commands").EnumerateArray(), item =>
            item.GetProperty("id").GetString() == "core:orq-auth" && item.GetProperty("enabled").GetBoolean());
    }

    private sealed class Handler(CommandDescriptor descriptor) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor;
        public ValueTask<HostCommandOutcome> HandleAsync(IReadOnlyList<string> arguments, CommandContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult<HostCommandOutcome>(new PromptCommandRequested("ok", "test"));
    }
}
