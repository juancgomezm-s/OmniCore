namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed class ClaudeLaneInvocationTests
{
    [Fact]
    public void Unexpected_tool_diagnostics_expose_only_bounded_identifiers_or_closed_categories()
    {
        var descriptions = ClaudeLaneInvocationBuilder.DescribeUnexpectedTools(
            ["EndConversation", "ToolV2", "mcp__server_tool", "bad tool\n"], ["EndConversation"]);
        Assert.Equal(new[] { "McpTool", "ToolId-ToolV2", "UntrustedToolIdentifier" }, descriptions);
        Assert.Equal(new[] { "ToolId-LS" }, ClaudeLaneInvocationBuilder.DescribeUnexpectedTools(
            ["LS"], ["Read", "Glob", "Grep", "Write", "Edit"]));
    }

    [Fact]
    public void Invocation_is_explicit_bounded_and_restricts_builtins_to_profile_file_scope()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "omni-claude-lane"));
        var profile = Profile();
        var packet = Packet(root, new TaskBudget(0.25m, 8000, 4, 12));
        var invocation = ClaudeLaneInvocationBuilder.Build(profile, packet, "{\"facts\":[]}",
            Path.Combine(root, "claude.exe"), root, null);
        var args = invocation.Arguments;
        Assert.Equal("stream-json", ValueAfter(args, "--output-format"));
        Assert.Equal("dontAsk", ValueAfter(args, "--permission-mode"));
        Assert.Equal("none", ValueAfter(args, "--permission-prompts"));
        Assert.Equal("{\"mcpServers\":{}}", ValueAfter(args, "--mcp-config"));
        Assert.Contains("--restricted", args);
        Assert.Equal("", ValueAfter(args, "--setting-sources"));
        Assert.Equal("mcp__*", ValueAfter(args, "--disallowedTools"));
        Assert.Equal("claude-haiku-5-5", ValueAfter(args, "--model"));
        Assert.Equal("0.25", ValueAfter(args, "--max-budget-usd"));
        Assert.Contains("--safe-mode", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("--add-dir", args);
        Assert.Equal(new[] { "Read", "Glob", "Grep", "Write", "Edit" }, invocation.RequestedTools);
        Assert.Equal(invocation.RequestedTools.Append("StructuredOutput"), invocation.ExpectedTools);
        var allowedTools = Array.IndexOf(args.ToArray(), "--allowedTools");
        Assert.Equal(invocation.RequestedTools,
            args.Skip(allowedTools + 1).Take(invocation.RequestedTools.Count));
        Assert.Equal("--add-dir", args[allowedTools + 1 + invocation.RequestedTools.Count]);
        Assert.DoesNotContain("Bash", args);
        Assert.DoesNotContain("Task", args);
        Assert.Equal(8000, invocation.StreamLimits.MaximumTokens);
        Assert.Equal(12, invocation.StreamLimits.MaximumToolCalls);
        Assert.Equal(0.25m, invocation.StreamLimits.MaximumCostUsd);
        Assert.Empty(invocation.Environment);
    }

    [Fact]
    public void Invocation_rejects_unbounded_budget_or_unowned_worktree_before_process_launch()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "omni-claude-lane"));
        var profile = Profile();
        Assert.Throws<InvalidDataException>(() => ClaudeLaneInvocationBuilder.Build(profile,
            Packet(root, new TaskBudget(null, 8000, 4, 12)), "{}", Path.Combine(root, "claude.exe"), root, null));
        Assert.Throws<InvalidDataException>(() => ClaudeLaneInvocationBuilder.Build(profile,
            Packet(Path.Combine(root, "elsewhere"), new TaskBudget(0.25m, 8000, 4, 12)), "{}",
            Path.Combine(root, "claude.exe"), root, null));
        Assert.Throws<InvalidOperationException>(() => ClaudeLaneInvocationBuilder.Build(
            new AgentProfile(ProfileId.New(), "native", 1,
                PermissionScope.With(["**"], [], [], [], [], false), [new ToolId("filesystem.read")]),
            Packet(root, new TaskBudget(0.25m, 8000, 4, 12)), "{}",
            Path.Combine(root, "claude.exe"), root, null));
    }

    private static string ValueAfter(IReadOnlyList<string> args, string flag) =>
        args[Array.IndexOf(args.ToArray(), flag) + 1];

    private static AgentProfile Profile() => new(ProfileId.New(), "claude-worker", 1,
        PermissionScope.With(["**"], ["**"], [], [], [], false), [], AgentExecutorKind.ClaudeCode,
        "claude-haiku-5-5");

    private static TaskPacket Packet(string root, TaskBudget budget)
    {
        var refValue = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)), 128,
            ContextInheritanceService.DelegationPacketMediaType, ArtifactKind.Other, Sensitivity.Sensitive);
        return new TaskPacket(1, SessionId.New(), RunId.New(), TaskId.New(), LaneId.New(), ExecutionId.New(),
            DelegationId.New(), ExecutionId.New(), ProfileId.New(), "test task", budget, refValue,
            Guid.NewGuid().ToString("N"), root, Path.Combine(root, ".git"), root,
            new string('b', 40), new string('c', 40), new string('d', 40),
            WorktreeBase.SnapshotOfWorkingTree, "working state");
    }
}
