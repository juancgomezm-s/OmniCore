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

            File.WriteAllText(Path.Combine(config, "permissions.yaml"),
                "profile: conservative\nrules:\n  fake.test: deny\n");
            var policy = OmniHost.CreateGrantAwarePolicy(RunMode.Act, null, workspace, null);

            Assert.Equal(PermissionDecision.Ask, policy.Evaluate(Intent("fake.write")).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent("fake.test")).Final);

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
