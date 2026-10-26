using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class KeyBindingConfigurationTests
{
    [Fact]
    public void Diagnostic_positions_match_the_actual_user_file()
    {
        var parsed = KeyBindingConfigurationReader.Parse("version: 1\nbindings:\n  - keys: F7\n    action: unknown\n");
        var entry = Assert.Single(parsed.Bindings);
        Assert.Equal(3, entry.Line);
        Assert.Equal(5, entry.Column);
    }

    [Theory]
    [InlineData("version: 2\nbindings: []")]
    [InlineData("bindings: []")]
    [InlineData("version: 1\nbindings: null")]
    [InlineData("version: 1\nproviders: {}\nbindings: []")]
    [InlineData("version: 1\nbindings: []\nbindings: []")]
    [InlineData("version: 1\nbindings:\n  - keys: Ctrl+K\n    action: help.show\n    command: help")]
    [InlineData("version: 1\nbindings:\n  - action: help.show")]
    [InlineData("version: 1\nbindings:\n  - keys: Ctrl+K\n    action: help.show\n    source: Default")]
    [InlineData("version: 1\nbindings:\n  - keys: Ctrl+K\n    action: help.show\n    priority: 1001")]
    [InlineData("version: 1\nbindings:\n  - action: -run.cancel\n    keys: Ctrl+K")]
    [InlineData("version: 1\nbindings:\n  - null")]
    public void Invalid_user_configuration_is_diagnosed_with_position_and_not_partially_applied(string yaml)
    {
        var snapshot = KeyBindingConfigurationReader.Parse(yaml);
        Assert.Empty(snapshot.Bindings);
        Assert.NotEmpty(snapshot.Issues);
        Assert.All(snapshot.Issues, issue => { Assert.True(issue.Line >= 1); Assert.True(issue.Column >= 1); });
    }

    [Fact]
    public void Oversized_files_and_excessive_bindings_are_bounded()
    {
        Assert.Contains(KeyBindingConfigurationReader.Parse(new string('x', 65_537)).Issues, issue => issue.Code == "file_too_large");
        var yaml = "version: 1\nbindings:\n" + string.Concat(Enumerable.Repeat("  - keys: Ctrl+P\n    action: view.plan\n", 257));
        Assert.Contains(KeyBindingConfigurationReader.Parse(yaml).Issues, issue => issue.Code == "too_many_bindings");
    }

    [Fact]
    public void User_only_loader_preserves_literal_commands_reports_semantic_errors_and_never_reads_project_config()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-keybindings-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"));
            Directory.CreateDirectory(paths.ConfigDirectory);
            var project = Path.Combine(root, "workspace", ".omnicore"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "keybindings.yaml"), "not: a user configuration");
            var first = OmniCore.Cli.KeyBindingConfiguration.Load(TestContext.Current.CancellationToken, paths.DataDirectory, paths.ConfigDirectory);
            Assert.Empty(first.Configuration.Issues);
            Assert.False(File.Exists(first.Configuration.Path));
            var yaml = """
                version: 1
                bindings:
                  - keys: Ctrl+K Ctrl+P
                    command: plan
                    arguments: ["literal $(never execute)"]
                    when: input && !paletteOpen
                    priority: 10
                  - action: -run.cancel
                  - keys: Ctrl+J
                    action: unknown.action
                """;
            File.WriteAllText(first.Configuration.Path, yaml);
            var before = File.ReadAllBytes(first.Configuration.Path);
            var configured = OmniCore.Cli.KeyBindingConfiguration.Load(TestContext.Current.CancellationToken, paths.DataDirectory, paths.ConfigDirectory);
            Assert.Empty(configured.Configuration.Issues);
            Assert.Equal(3, configured.Configuration.Bindings.Count);
            Assert.True(configured.Configuration.Bindings[0].Line > 1);
            Assert.Contains(configured.Service.Diagnostics, issue => issue.Code == "protected_restored");
            Assert.Contains(configured.Service.Diagnostics, issue => issue.Code == "unknown_target");
            var diagnostic = Assert.Single(configured.Diagnostics, issue => issue.Code == "unknown_target");
            Assert.Equal(first.Configuration.Path, diagnostic.Source);
            Assert.True(diagnostic.Line > 1);
            Assert.True(diagnostic.Column > 0);
            Assert.True(KeyChord.TryParseSequence("Ctrl+K Ctrl+P", out var sequence));
            var resolution = configured.Service.Resolve(sequence, new KeyBindingContext([new("input", true)]));
            Assert.Equal("plan", resolution.Binding!.Definition.Target.Id);
            Assert.Equal(["literal $(never execute)"], resolution.Binding.Definition.Target.Arguments);
            Assert.Equal(before, File.ReadAllBytes(first.Configuration.Path));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => OmniCore.Cli.KeyBindingConfiguration.Load(canceled.Token, paths.DataDirectory, paths.ConfigDirectory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Extension_contributions_cannot_claim_user_precedence_in_the_configuration_adapter()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-keybindings-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"));
            var forged = new KeyBindingDefinition("Ctrl+P", new(KeyBindingTargetKind.Action, ClientActionIds.RunCancel),
                Source: KeyBindingSource.User, SourceId: "pilot");
            var configured = OmniCore.Cli.KeyBindingConfiguration.Load(TestContext.Current.CancellationToken,
                paths.DataDirectory, paths.ConfigDirectory, [forged]);
            Assert.Contains(configured.Service.Diagnostics, issue => issue.Code == "extension_ownership");
            Assert.DoesNotContain(configured.Service.Bindings, binding => binding.Definition.SourceId == "pilot");
        }
        finally { Directory.Delete(root, true); }
    }
}
