namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

public sealed class ToolNameWireMapTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void Core_tools_use_Codex_names_and_extension_sources_remain_readable(int maximumLength)
    {
        var expected = new Dictionary<string, string>
        {
            ["filesystem.read"] = "read_file",
            ["filesystem.list"] = "list_dir",
            ["filesystem.search"] = "grep_files",
            ["filesystem.patch"] = "apply_patch",
            ["filesystem.write"] = "write_file",
            ["media.read"] = "view_image",
            ["shell.exec"] = "exec_command",
            ["process.exec"] = "exec_process",
            ["process.start"] = "start_process",
            ["process.read"] = "read_process_output",
            ["process.send_input"] = "write_stdin",
            ["process.stop"] = "stop_process",
            ["plan.propose"] = "update_plan",
            ["user.ask"] = "request_user_input",
            ["tool.search"] = "tool_search",
            ["search.web"] = "web_search",
            ["web.read"] = "web_fetch",
            ["artifact.read"] = "read_artifact",
            ["reference.resolve"] = "resolve_reference",
            ["skill.load"] = "load_skill",
            ["memory.propose"] = "propose_memory",
            ["mode.propose"] = "propose_mode",
            ["core.agents.mailbox.receive"] = "receive_agent_messages",
            ["worktree.integrate"] = "integrate_worktree",
            ["worktree.cleanup"] = "cleanup_worktree",
            ["worktree.discard"] = "discard_worktree",
            ["worktree.retention"] = "set_worktree_retention",
            ["extension.batch.apply"] = "apply_extension_batch",
            ["companion.pet"] = "companion_pet",
            ["mcp.docs.web.search"] = "mcp_docs_web_search",
            ["ext.fixture.native.mutate"] = "ext_fixture_native_mutate",
            ["dyn.core.verify_integration"] = "dyn_core_verify_integration",
            ["custom_tool-2"] = "custom_tool-2",
            ["omni_reserved"] = "omni_reserved",
        };
        var map = ToolNameWireMap.Create(expected.Keys, maximumLength);

        foreach (var (canonical, alias) in expected)
        {
            Assert.Equal(alias, map.ToWire(canonical));
            Assert.Equal(canonical, map.ToCanonical(alias));
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void Collisions_and_long_names_stay_unique_reversible_and_order_independent(int maximumLength)
    {
        var longPrefix = "mcp.documentation." + new string('a', maximumLength);
        string[] names = ["read_file", "filesystem_read", "filesystem.read", "filesystem/read", "filesystem:read",
            longPrefix + ".read", longPrefix + ".write"];
        var map = ToolNameWireMap.Create(names, maximumLength);
        var reversed = ToolNameWireMap.Create(names.Reverse(), maximumLength);
        var aliases = names.Select(map.ToWire).ToArray();

        Assert.Equal(names.Length, aliases.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("filesystem_read", map.ToWire("filesystem_read"));
        Assert.Equal("read_file", map.ToWire("read_file"));
        Assert.StartsWith("read_file_", map.ToWire("filesystem.read"));
        Assert.StartsWith("mcp_documentation_", map.ToWire(longPrefix + ".read"));
        foreach (var name in names)
        {
            var alias = map.ToWire(name);
            Assert.InRange(alias.Length, 1, maximumLength);
            Assert.Matches("^[a-zA-Z0-9_-]+$", alias);
            Assert.Equal(name, map.ToCanonical(alias));
            Assert.Equal(alias, reversed.ToWire(name));
        }
    }

    [Fact]
    public void A_canonical_name_cannot_be_shadowed_by_a_generated_suffix()
    {
        var initial = ToolNameWireMap.Create(["filesystem.read", "read_file"], 64);
        var reserved = initial.ToWire("filesystem.read");
        var map = ToolNameWireMap.Create(["filesystem.read", "read_file", reserved], 64);

        Assert.Equal(reserved, map.ToWire(reserved));
        Assert.NotEqual(reserved, map.ToWire("filesystem.read"));
        Assert.Equal("filesystem.read", map.ToCanonical(map.ToWire("filesystem.read")));
        Assert.Equal(reserved, map.ToCanonical(reserved));
        Assert.Equal("unlisted_tool", map.ToCanonical("unlisted_tool"));
    }

    [Fact]
    public void Hidden_history_and_exact_choice_share_the_request_mapping_without_adding_tools()
    {
        var historical = new ToolCallBlock(ToolCallId.New(), "old-call", "filesystem.read", "{}");
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture"), 4096, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.Assistant, [historical])], null,
            [new ToolDefinition("read_file", "Other operation", "{}")],
            ToolChoice.Exact("ext.fixture.mutate"), null, null, null, null);
        var map = ToolNameWireMap.ForRequest(request, 64);

        Assert.Equal("read_file", map.ToWire("read_file"));
        Assert.NotEqual("read_file", map.ToWire(historical.ToolName));
        Assert.Equal(historical.ToolName, map.ToCanonical(map.ToWire(historical.ToolName)));
        Assert.Equal("ext_fixture_mutate", map.ToWire("ext.fixture.mutate"));
        Assert.Equal("read_file", Assert.Single(request.Tools).Name);
    }

    [Fact]
    public void Metadata_references_use_the_allocated_alias_without_rewriting_unrelated_identifiers()
    {
        var map = ToolNameWireMap.Create(["filesystem.read", "read_file", "process.start", "process.read",
            "process.send_input", "process.stop", "search.web", "web.read"], 64);
        var allocated = map.ToWire("filesystem.read");

        Assert.Equal($"Use `{allocated}` then web_fetch; search.web.extra and mcp.filesystem.read stay unchanged.",
            map.ToWireText("Use `filesystem.read` then web.read; search.web.extra and mcp.filesystem.read stay unchanged."));
        Assert.Equal("start_process returns a handle; use read_process_output, write_stdin or stop_process.",
            map.ToWireText("process.start returns a handle; use process.read, process.send_input or process.stop."));
        Assert.Equal("filesystem.patch is unavailable.", map.ToWireText("filesystem.patch is unavailable."));
    }
}
