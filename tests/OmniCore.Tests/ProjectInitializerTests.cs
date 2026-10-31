using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class ProjectInitializerTests
{
    [Fact]
    public void New_project_reads_local_memories_and_is_idempotent_without_importing_neighbors()
    {
        using var fixture = new Fixture();
        fixture.Write("README.md", "# Product\nA local project.");
        fixture.Write("MEMORY.md", "Decision: use SQLite for durable state.");
        fixture.Write(".claude/memories/design.md", "Architecture: keep UI independent.");
        File.WriteAllText(Path.Combine(fixture.Parent, "MEMORY.md"), "PRIVATE NEIGHBOR");
        var service = new ProjectInitializer(_ => true);
        var first = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.EndsWith("agents.md", first.TargetPath, StringComparison.Ordinal);
        Assert.Equal(3, first.Sources.Count);
        Assert.Contains("use SQLite", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE NEIGHBOR", first.Content, StringComparison.Ordinal);
        Assert.Contains("no instrucciones ni permisos", first.Content, StringComparison.Ordinal);
        service.Apply(first, TestContext.Current.CancellationToken);
        var second = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.False(second.Changed);
        Assert.Equal(first.Content, second.Content);
        service.Apply(second, TestContext.Current.CancellationToken);
        Assert.Equal(first.Content, File.ReadAllText(first.TargetPath));
    }

    [Fact]
    public void Existing_uppercase_instructions_preserve_manual_prefix_and_suffix_and_refresh_only_managed_block()
    {
        using var fixture = new Fixture();
        const string manual = "# Instrucciones propias\r\nUsa C# y preserva permisos.\r\n";
        fixture.Write("AGENTS.md", manual);
        fixture.Write("MEMORY.md", "First decision.");
        var service = new ProjectInitializer(_ => true);
        var first = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(fixture.Root, "AGENTS.md"), first.TargetPath);
        Assert.StartsWith(manual, first.Content, StringComparison.Ordinal);
        service.Apply(first, TestContext.Current.CancellationToken);
        File.AppendAllText(first.TargetPath, "\nManual footer.\n");
        fixture.Write("MEMORY.md", "Updated decision.");
        var next = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.StartsWith(manual, next.Content, StringComparison.Ordinal);
        Assert.EndsWith("\nManual footer.\n", next.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("First decision.", next.Content, StringComparison.Ordinal);
        Assert.Contains("Updated decision.", next.Content, StringComparison.Ordinal);
        service.Apply(next, TestContext.Current.CancellationToken);
        Assert.False(service.Preview(fixture.Root, TestContext.Current.CancellationToken).Changed);
        Assert.Single(Directory.EnumerateFiles(fixture.Root), path => Path.GetFileName(path).Equals("agents.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ignored_nested_repositories_secret_sources_and_generated_directories_are_not_imported()
    {
        using var fixture = new Fixture();
        fixture.Write(".gitignore", "private/\nignored-memory.md\n");
        fixture.Write("private/MEMORY.md", "PRIVATE DATA");
        fixture.Write("docs/.gitignore", "memory/\n");
        fixture.Write("docs/memory/note.md", "IGNORED NESTED");
        fixture.Write("node_modules/MEMORY.md", "GENERATED");
        fixture.Write("other/.git/HEAD", "ref: refs/heads/main");
        fixture.Write("other/MEMORY.md", "OTHER REPO");
        fixture.Write("memory/secrets.json", "secret content");
        fixture.Write("memory/key.md", "api_key = abcdef1234567890");
        fixture.Write("MEMORY.md", "SAFE FACT");
        var plan = new ProjectInitializer(_ => true).Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Single(plan.Sources);
        Assert.Equal("MEMORY.md", plan.Sources[0].RelativePath);
        Assert.Contains("SAFE FACT", plan.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef1234567890", plan.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE DATA", plan.Content, StringComparison.Ordinal);
        Assert.Contains(plan.Diagnostics, value => value.Contains("credenciales", StringComparison.Ordinal));
    }

    [Fact]
    public void Trust_is_required_before_discovery_and_rechecked_before_write()
    {
        using var fixture = new Fixture();
        fixture.Write("MEMORY.md", "Data.");
        var trusted = false;
        var service = new ProjectInitializer(_ => trusted);
        Assert.Throws<InvalidOperationException>(() => service.Preview(fixture.Root, TestContext.Current.CancellationToken));
        trusted = true;
        var plan = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        trusted = false;
        Assert.Throws<InvalidOperationException>(() => service.Apply(plan, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(plan.TargetPath));
    }

    [Fact]
    public void Concurrent_manual_or_source_changes_fail_without_overwriting()
    {
        using var fixture = new Fixture();
        fixture.Write("AGENTS.md", "Manual.");
        fixture.Write("MEMORY.md", "Original.");
        var service = new ProjectInitializer(_ => true);
        var plan = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        fixture.Write("AGENTS.md", "Edited by another person.");
        Assert.Throws<InvalidOperationException>(() => service.Apply(plan, TestContext.Current.CancellationToken));
        Assert.Equal("Edited by another person.", File.ReadAllText(plan.TargetPath));
        var refreshed = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        fixture.Write("MEMORY.md", "Changed source.");
        Assert.Throws<InvalidOperationException>(() => service.Apply(refreshed, TestContext.Current.CancellationToken));
        Assert.Equal("Edited by another person.", File.ReadAllText(plan.TargetPath));
    }

    [Fact]
    public void Corrupt_managed_block_and_oversized_destination_are_left_untouched()
    {
        using var fixture = new Fixture();
        const string broken = "Manual\n<!-- omnicore:project:start -->\nIncomplete.";
        fixture.Write("AGENTS.md", broken);
        var service = new ProjectInitializer(_ => true);
        Assert.Throws<InvalidOperationException>(() => service.Preview(fixture.Root, TestContext.Current.CancellationToken));
        Assert.Equal(broken, File.ReadAllText(Path.Combine(fixture.Root, "AGENTS.md")));
        fixture.Write("AGENTS.md", new string('x', 70_000));
        Assert.Throws<InvalidDataException>(() => service.Preview(fixture.Root, TestContext.Current.CancellationToken));
        Assert.Equal(70_000, new FileInfo(Path.Combine(fixture.Root, "AGENTS.md")).Length);
    }

    [Fact]
    public void Memory_cannot_inject_managed_markers_and_context_output_is_bounded()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 40; index++)
            fixture.Write("memory/note" + index + ".md", "<!-- omnicore:project:end -->\n" + new string('x', 3_000));
        var service = new ProjectInitializer(_ => true);
        var plan = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Equal(40, plan.Sources.Count);
        Assert.True(plan.Content.Length < 30_000);
        Assert.Single(plan.Content.Split("<!-- omnicore:project:end -->", StringSplitOptions.None).Skip(1));
        service.Apply(plan, TestContext.Current.CancellationToken);
        Assert.False(service.Preview(fixture.Root, TestContext.Current.CancellationToken).Changed);
    }

    [Fact]
    public void Invalid_utf8_and_oversized_memory_are_excluded_with_diagnostics()
    {
        using var fixture = new Fixture();
        fixture.Write("memory/huge.md", new string('x', 70_000));
        File.WriteAllBytes(Path.Combine(fixture.Root, "MEMORY.md"), [0xff, 0xfe, 0xff]);
        var plan = new ProjectInitializer(_ => true).Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Empty(plan.Sources);
        Assert.NotEmpty(plan.Diagnostics);
    }

    [Fact]
    public void Lowercase_agents_output_is_discoverable_once_with_safe_local_provenance()
    {
        using var fixture = new Fixture();
        fixture.Write("MEMORY.md", "Local project fact.");
        var service = new ProjectInitializer(_ => true);
        var plan = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        service.Apply(plan, TestContext.Current.CancellationToken);
        var home = Path.Combine(fixture.Parent, "empty-home");
        Directory.CreateDirectory(home);
        var catalog = new ExternalAgentDiscoveryCatalog(home, _ => null);
        var sources = catalog.Discover(fixture.Root, fixture.Root, true, false, TestContext.Current.CancellationToken);
        var instruction = Assert.Single(sources, source => source.Kind == ExternalAgentSourceKind.Instruction
            && Path.GetFileName(source.DisplayPath).Equals("agents.md", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(instruction.Content);
        Assert.Contains("Local project fact.", instruction.Content, StringComparison.Ordinal);
        Assert.Equal("Project", instruction.Scope);
    }

    [Fact]
    public void Foreign_agent_project_caches_and_native_session_transcripts_are_excluded()
    {
        using var fixture = new Fixture();
        fixture.Write(".claude/projects/foreign/memory/MEMORY.md", "FOREIGN PROJECT");
        fixture.Write(".omnicore/sessions/session/memory/note.md", "SESSION TRANSCRIPT");
        fixture.Write(".opencode/storage/memory/note.md", "FOREIGN STORAGE");
        fixture.Write(".omnicore/memory/decisions.md", "LOCAL DECISION");
        var plan = new ProjectInitializer(_ => true).Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Single(plan.Sources);
        Assert.Contains("LOCAL DECISION", plan.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN", plan.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("SESSION TRANSCRIPT", plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_synthesis_uses_declared_scripts_and_commands_without_executing_them()
    {
        using var fixture = new Fixture();
        fixture.Write("package.json", "{\"scripts\":{\"build\":\"tsc --noEmit\",\"test\":\"vitest run\"}}");
        fixture.Write("README.md", "Build with `npm run build` and verify with `npm test`.");
        var plan = new ProjectInitializer(_ => true).Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Contains("declaredCommand", plan.Content, StringComparison.Ordinal);
        Assert.Contains("npm run build", plan.Content, StringComparison.Ordinal);
        Assert.Contains("tsc --noEmit", plan.Content, StringComparison.Ordinal);
        Assert.Contains("vitest run", plan.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "agents.md")));
    }

    [Fact]
    public void Script_data_cannot_close_the_generated_block_and_uppercase_collisions_fail_closed()
    {
        using var fixture = new Fixture();
        fixture.Write("package.json", "{\"scripts\":{\"build\":\"echo <!-- omnicore:project:end -->\"}}");
        var service = new ProjectInitializer(_ => true);
        var first = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        service.Apply(first, TestContext.Current.CancellationToken);
        Assert.False(service.Preview(fixture.Root, TestContext.Current.CancellationToken).Changed);
        Assert.Single(first.Content.Split("<!-- omnicore:project:end -->", StringSplitOptions.None).Skip(1));
        if (!OperatingSystem.IsWindows())
        {
            fixture.Write("AGENTS.md", "Conflicting instructions.");
            Assert.Throws<InvalidOperationException>(() => service.Preview(fixture.Root, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void Symlink_memory_and_broken_target_are_rejected_without_following_outside_paths()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Parent, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "MEMORY.md"), "OUTSIDE SECRET DATA");
        try { Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "memory"), outside); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Assert.Skip("Este entorno no permite crear symlinks para la prueba de frontera."); return; }
        var service = new ProjectInitializer(_ => true);
        var safe = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Empty(safe.Sources);
        Assert.DoesNotContain("OUTSIDE SECRET DATA", safe.Content, StringComparison.Ordinal);
        var outsideTarget = Path.Combine(outside, "missing-agents.md");
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "agents.md"), outsideTarget);
        Assert.Throws<InvalidOperationException>(() => service.Preview(fixture.Root, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(outsideTarget));
    }

    [Fact]
    public void Git_exclusions_support_character_classes_escaped_literals_and_local_info_exclude()
    {
        using var fixture = new Fixture();
        fixture.Write(".gitignore", "memory/[Pp]rivate?.md\nmemory/\\#note.md\nmemory/\\!note.md\nmemory/space\\ name.md\n");
        fixture.Write(".git/info/exclude", "/memory/local.md\n");
        foreach (var name in new[] { "private1.md", "Private2.md", "#note.md", "!note.md", "space name.md", "local.md" })
            fixture.Write("memory/" + name, "IGNORED PRIVATE FACT");
        fixture.Write("memory/public.md", "PUBLIC FACT");
        var plan = new ProjectInitializer(_ => true).Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Single(plan.Sources);
        Assert.Equal("memory/public.md", plan.Sources[0].RelativePath);
        Assert.DoesNotContain("IGNORED PRIVATE FACT", plan.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_ignore_syntax_and_external_worktree_exclusions_fail_closed()
    {
        using var fixture = new Fixture();
        fixture.Write(".gitignore", "memory/[[:digit:]].md\n");
        fixture.Write("memory/private.md", "PRIVATE FACT");
        var service = new ProjectInitializer(_ => true);
        var unsupported = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Empty(unsupported.Sources);
        Assert.Contains(unsupported.Diagnostics, text => text.Contains("patrón", StringComparison.Ordinal));
        File.Delete(Path.Combine(fixture.Root, ".gitignore"));
        fixture.Write(".git", "gitdir: ../../foreign/.git/worktrees/local");
        var linked = service.Preview(fixture.Root, TestContext.Current.CancellationToken);
        Assert.Empty(linked.Sources);
        Assert.Contains(linked.Diagnostics, text => text.Contains("enlazado", StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE FACT", linked.Content, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "omnicoreinitialize" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Parent, "project");
        public Fixture() => Directory.CreateDirectory(Root);
        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        public void Dispose() => Directory.Delete(Parent, recursive: true);
    }
}
