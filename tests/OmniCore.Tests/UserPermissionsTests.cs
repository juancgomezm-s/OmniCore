using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;

namespace OmniCore.Tests;

/// <summary>ADR-0037 §4–§5 / A11: <c>permissions.yaml</c> (scope User) aporta el perfil y la capa UserPolicy.</summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class UserPermissionsTests
{
    private static ToolIntent Intent(string tool, ResourceClaims? claims = null) => new(ToolCallId.New(),
        new ToolId(tool), "{}", EffectClass.None, claims ?? ResourceClaims.Empty(), ToolRisk.Low, null);

    private static ScriptedPermissionPolicy Policy(RunMode mode, PermissionProfile profile = PermissionProfile.Autonomous,
        Dictionary<string, PermissionDecision>? rules = null,
        Dictionary<string, PermissionDecision>? projectRestrictions = null)
    {
        var policy = new ScriptedPermissionPolicy(projectRestrictions ?? new()).WithModeDefaults(mode).WithProfile(profile);
        foreach (var (tool, decision) in rules ?? new()) policy.WithUserPolicyTool(tool, decision);
        return policy;
    }

    // ---------------------------------------------------------------- carga y validación

    [Theory]
    [InlineData("allow", PermissionDecision.Allow)]
    [InlineData("ask", PermissionDecision.Ask)]
    [InlineData("deny", PermissionDecision.Deny)]
    public void Legacy_search_rules_apply_to_the_canonical_filesystem_search(string decision, PermissionDecision expected)
    {
        var parsed = UserPermissionsLoader.Parse("rules:\n  search.text: " + decision + "\n");

        Assert.Equal("filesystem.search", Assert.Single(parsed.Rules).Key);
        Assert.Equal(expected, Policy(RunMode.Act, rules: parsed.Rules.ToDictionary()).Evaluate(Intent("filesystem.search")).Final);
    }

    [Theory]
    [InlineData("search.text: deny", "filesystem.search: allow", PermissionDecision.Deny)]
    [InlineData("filesystem.search: allow", "search.text: deny", PermissionDecision.Deny)]
    [InlineData("search.text: ask", "filesystem.search: allow", PermissionDecision.Ask)]
    [InlineData("filesystem.search: allow", "search.text: ask", PermissionDecision.Ask)]
    public void Legacy_and_canonical_search_rules_keep_the_stricter_decision_in_either_order(
        string first, string second, PermissionDecision expected)
    {
        var parsed = UserPermissionsLoader.Parse("rules:\n  " + first + "\n  " + second + "\n");

        Assert.Equal("filesystem.search", Assert.Single(parsed.Rules).Key);
        Assert.Equal(expected, Policy(RunMode.Act, rules: parsed.Rules.ToDictionary()).Evaluate(Intent("filesystem.search")).Final);
    }

    [Fact]
    public void Permissions_yaml_declares_the_profile_and_the_user_rules_and_defaults_without_the_file()
    {
        var parsed = UserPermissionsLoader.Parse("profile: balanced\nrules:\n  filesystem.write: ask\n  process.exec: deny\n  plan.propose: allow\n");

        Assert.Equal(PermissionProfile.Balanced, parsed.Profile);
        Assert.Equal(PermissionDecision.Ask, parsed.Rules["filesystem.write"]);
        Assert.Equal(PermissionDecision.Deny, parsed.Rules["process.exec"]);
        Assert.Equal(PermissionDecision.Allow, parsed.Rules["plan.propose"]);

        Assert.Equal(PermissionProfile.Autonomous, UserPermissionsLoader.Parse("").Profile);
        Assert.Empty(UserPermissionsLoader.Parse("rules: {}\n").Rules);
        var empty = Path.Combine(Path.GetTempPath(), "omni-perm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Equal(UserPermissions.Default, UserPermissionsLoader.Load(new DefaultPlatformPaths(empty)));
        }
        finally { Directory.Delete(empty, true); }
    }

    [Theory]
    [InlineData("profile: reckless\n", "profile", "config.outOfRange")]
    [InlineData("profile: [balanced]\n", "profile", "config.wrongType")]
    [InlineData("rules: nope\n", "rules", "config.wrongType")]
    [InlineData("rules:\n  filesystem.write: maybe\n", "rules.filesystem.write", "config.outOfRange")]
    [InlineData("rules:\n  filesystem.write: [ask]\n", "rules.filesystem.write", "config.wrongType")]
    [InlineData("grants: {}\n", "grants", "config.unknownKey")]
    [InlineData("- a\n- b\n", "$", "config.expectedMapping")]
    public void Invalid_permissions_yaml_is_reported_with_its_key_path_and_never_ignored(string yaml, string keyPath,
        string message)
    {
        var error = Assert.Throws<ConfigValidationException>(() => UserPermissionsLoader.Parse(yaml));

        var diagnostic = Assert.Single(error.Diagnostics, item => item.KeyPath == keyPath);
        Assert.Equal("permissions.yaml", diagnostic.File);
        Assert.Equal(message, diagnostic.Message.Key);
    }

    [Fact]
    public void Diagnostics_point_at_the_exact_line_and_column_of_the_offending_value()
    {
        // YamlDotNet ya numera desde 1: sumar uno más desplazaba todos los diagnósticos de configuración.
        var profile = Assert.Throws<ConfigValidationException>(() => UserPermissionsLoader.Parse("profile: reckless\n"));
        Assert.Equal((1, 10), (profile.Diagnostics[0].Line, profile.Diagnostics[0].Column));

        var rule = Assert.Throws<ConfigValidationException>(() =>
            UserPermissionsLoader.Parse("profile: balanced\nrules:\n  filesystem.write: maybe\n"));
        Assert.Equal((3, 21), (rule.Diagnostics[0].Line, rule.Diagnostics[0].Column));
    }

    [Fact]
    public void A_yaml_syntax_error_carries_its_line_and_column()
    {
        var error = Assert.Throws<ConfigValidationException>(() => UserPermissionsLoader.Parse("rules:\n  filesystem.write: [\n"));

        Assert.Contains(error.Diagnostics, item => item.Message.Key == "config.yamlSyntax" && item.Line > 0 && item.Column > 0);
    }

    // ---------------------------------------------------------------- perfil

    [Fact]
    public void The_profile_changes_what_asks_but_never_grants_more_than_the_autonomous_default()
    {
        var build = Intent("fake.test");
        var write = Intent("fake.write");

        Assert.Equal(PermissionDecision.Allow, Policy(RunMode.Act).Evaluate(build).Final);
        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Act, PermissionProfile.Balanced).Evaluate(build).Final);
        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Orchestrate, PermissionProfile.Balanced).Evaluate(build).Final);
        Assert.Equal(PermissionDecision.Allow, Policy(RunMode.Act, PermissionProfile.Balanced).Evaluate(write).Final);

        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Act, PermissionProfile.Conservative).Evaluate(write).Final);
        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Act, PermissionProfile.Conservative).Evaluate(build).Final);

        // PLAN sigue negando escrituras y procesos con efecto, y los secretos son Deny en todos los perfiles.
        var secret = Intent("plan.propose", new ResourceClaims([], [], [], null, ["~/.ssh/id_rsa"]));
        foreach (var profile in Enum.GetValues<PermissionProfile>())
        {
            Assert.Equal(PermissionDecision.Deny, Policy(RunMode.Plan, profile).Evaluate(write).Final);
            Assert.Equal(PermissionDecision.Deny, Policy(RunMode.Plan, profile).Evaluate(build).Final);
            Assert.Equal(PermissionDecision.Deny, Policy(RunMode.Act, profile).Evaluate(secret).Final);
        }
    }

    [Fact]
    public void Exact_worktree_disposition_claim_uses_workspace_write_policy_and_still_obeys_user_deny()
    {
        var claim = new WorktreeDispositionClaim("0123456789abcdef",
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "omni-disposition-policy")),
            LaneId.New().ToString(), Guid.NewGuid().ToString("N"), WorktreeDispositionOperation.Discard,
            new string('a', 40), new string('b', 40));
        var claims = new ResourceClaims([], [], [], null, [], claim);
        var disposition = new ToolIntent(ToolCallId.New(), new ToolId("worktree.discard"), "{}",
            EffectClass.Reconcilable, claims, ToolRisk.High, null);
        var malformedClaim = claim with { OwnershipId = "foreign" };
        var malformed = new ToolIntent(ToolCallId.New(), new ToolId("worktree.discard"), "{}",
            EffectClass.Reconcilable, new ResourceClaims([], [], [], null, [], malformedClaim), ToolRisk.High, null);
        var missingClaim = new ToolIntent(ToolCallId.New(), new ToolId("worktree.discard"), "{}",
            EffectClass.Reconcilable, ResourceClaims.Empty(), ToolRisk.High, null);

        Assert.Equal(PermissionDecision.Allow, Policy(RunMode.Act).Evaluate(disposition).Final);
        Assert.Equal(PermissionDecision.Allow, Policy(RunMode.Act, rules: new()
            { ["worktree.discard"] = PermissionDecision.Allow }).Evaluate(disposition).Final);
        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Act).Evaluate(missingClaim).Final);
        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Act).Evaluate(malformed).Final);
        Assert.Equal(PermissionDecision.Deny, Policy(RunMode.Act, rules: new()
            { ["worktree.discard"] = PermissionDecision.Deny }).Evaluate(disposition).Final);
    }

    // ---------------------------------------------------------------- reglas de usuario (UserPolicy)

    [Fact]
    public void User_rules_restrict_with_deny_and_ask_and_show_up_as_the_UserPolicy_layer()
    {
        var rules = new Dictionary<string, PermissionDecision>
        {
            ["fake.write"] = PermissionDecision.Deny, ["fake.test"] = PermissionDecision.Ask,
        };

        var denied = Policy(RunMode.Act, rules: rules).Evaluate(Intent("fake.write"));
        var asked = Policy(RunMode.Act, rules: rules).Evaluate(Intent("fake.test"));

        Assert.Equal(PermissionDecision.Deny, denied.Final);
        Assert.Contains(denied.Layers, layer => layer.Layer == "UserPolicy" && layer.Decision == PermissionDecision.Deny);
        Assert.Equal(PermissionDecision.Ask, asked.Final);
        Assert.Contains(asked.Layers, layer => layer.Layer == "UserPolicy" && layer.Decision == PermissionDecision.Ask);
    }

    [Fact]
    public void An_explicit_allow_rule_lifts_a_mode_or_profile_ask_but_never_a_deny_or_a_repo_restriction()
    {
        var allow = new Dictionary<string, PermissionDecision>
        {
            ["fake.write"] = PermissionDecision.Allow, ["fake.test"] = PermissionDecision.Allow,
        };

        // El Ask del perfil conservador se levanta con una regla explícita del usuario.
        var lifted = Policy(RunMode.Act, PermissionProfile.Conservative, allow).Evaluate(Intent("fake.write"));
        Assert.Equal(PermissionDecision.Allow, lifted.Final);
        Assert.Contains(lifted.Layers, layer => layer.Layer == "user-rule" && layer.Decision == PermissionDecision.Allow);

        // Un Deny (PLAN no escribe) no se levanta.
        Assert.Equal(PermissionDecision.Deny, Policy(RunMode.Plan, PermissionProfile.Conservative, allow)
            .Evaluate(Intent("fake.write")).Final);

        // Una restricción que el repo estrecha a Ask tampoco la levanta el usuario por esta vía.
        var restricted = Policy(RunMode.Act, PermissionProfile.Conservative, allow,
            new() { ["fake.write"] = PermissionDecision.Ask });
        Assert.Equal(PermissionDecision.Ask, restricted.Evaluate(Intent("fake.write")).Final);

        // Sin la regla, el Ask se mantiene (el valor configurado cambia el comportamiento).
        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Act, PermissionProfile.Conservative)
            .Evaluate(Intent("fake.write")).Final);
    }

    [Fact]
    public void Exact_core_extension_batch_intent_uses_workspace_write_defaults_and_keeps_user_rules()
    {
        var batch = new ToolIntent(ToolCallId.New(), new ToolId("extension.batch.apply"), "{}",
            EffectClass.Reconcilable, ResourceClaims.Empty(), ToolRisk.High, null);

        Assert.Equal(PermissionDecision.Allow, Policy(RunMode.Act).Evaluate(batch).Final);
        Assert.Equal(PermissionDecision.Ask, Policy(RunMode.Act, PermissionProfile.Autonomous,
            new Dictionary<string, PermissionDecision> { ["extension.batch.apply"] = PermissionDecision.Ask })
            .Evaluate(batch).Final);
        Assert.Equal(PermissionDecision.Deny, Policy(RunMode.Act, PermissionProfile.Autonomous,
            new Dictionary<string, PermissionDecision> { ["extension.batch.apply"] = PermissionDecision.Deny })
            .Evaluate(batch).Final);
        Assert.Equal(PermissionDecision.Allow, Policy(RunMode.Act, PermissionProfile.Balanced).Evaluate(batch).Final);
    }

    // ---------------------------------------------------------------- cableado de producción

    [Fact]
    public void The_production_policy_factory_applies_the_users_permissions_yaml()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-perm-wiring-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(data);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);

            Assert.Equal(PermissionDecision.Allow, OmniHost.CreateGrantAwarePolicy(RunMode.Act, null, workspace, null)
                .Evaluate(Intent("fake.write")).Final);

            var extensionWriter = new ToolIntent(ToolCallId.New(), new ToolId("ext.sample.run"), "{}",
                EffectClass.NonIdempotent, new ResourceClaims([workspace], [workspace], [],
                    new ProcessClaim("extension-host", [], "WorkspaceEffect", WorkingDirectory: workspace,
                        Isolation: ProcessIsolation.PrivateExtensionWorkspace), []), ToolRisk.High, null);
            var livePolicy = OmniHost.CreateGrantAwarePolicy(RunMode.Act, null, workspace, null);
            var batchAuthority = Assert.IsAssignableFrom<IAutomaticExtensionBatchAuthority>(livePolicy);
            Assert.True(batchAuthority.AllowsAutomaticExtensionBatch(extensionWriter));
            Assert.True(batchAuthority.AllowsAutomaticExtensionBatchCurrently(extensionWriter));

            File.WriteAllText(Path.Combine(config, "permissions.yaml"),
                "profile: balanced\nrules:\n  fake.test: deny\n  ext.sample.run: allow\n");
            var policy = OmniHost.CreateGrantAwarePolicy(RunMode.Act, null, workspace, null);

            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent("fake.write")).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent("fake.test")).Final);
            // An explicit Allow can lift a per-tool Ask, but must not turn Balanced into
            // autonomous batch publication. The already-created policy reads the file afresh.
            Assert.Equal(PermissionDecision.Allow, batchAuthority.EvaluateCurrentPermission(extensionWriter));
            Assert.False(batchAuthority.AllowsAutomaticExtensionBatchCurrently(extensionWriter));

            File.WriteAllText(Path.Combine(config, "permissions.yaml"),
                "profile: autonomous\nrules:\n  ext.sample.run: deny\n");
            Assert.Equal(PermissionDecision.Deny, batchAuthority.EvaluateCurrentPermission(extensionWriter));
            Assert.True(batchAuthority.AllowsAutomaticExtensionBatchCurrently(extensionWriter));

            File.WriteAllText(Path.Combine(config, "permissions.yaml"), "profile: reckless\n");
            Assert.Throws<ConfigValidationException>(() => OmniHost.CreateGrantAwarePolicy(RunMode.Act, null, workspace, null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, priorConfig);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, priorData);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
