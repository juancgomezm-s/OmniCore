using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests de la carga de la identidad OAuth desde providers.yaml (§1 del plan, grupo 11).
/// Demuestran que lo que configura el usuario cambia el comportamiento del flujo (CLAUDE.md:
/// todo valor configurable debe tener un test que lo pruebe) y que un contenido inválido falla
/// con diagnostico en vez de degradar en silencio a API key.
/// </summary>
public sealed class ClaudeOAuthConfigurationTests
{
    private const string ValidYaml = """
        providers:
          anthropic:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-yaml-0001
              userAgent: "agent/{version} (external, cli)"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
        """;

    // ---- Carga desde YAML ------------------------------------------------------------------

    [Fact]
    public void A_provider_declaring_oauth_yields_the_declared_identity()
    {
        var loaded = new ConfigLoader().Load(ValidYaml, ModelsYaml());
        var oauth = loaded.Providers!.Providers!["anthropic"].OAuth;

        var identity = ClaudeOAuthConfiguration.TryBuild("anthropic", oauth, []);

        Assert.NotNull(identity);
        Assert.Equal("cid-yaml-0001", identity!.ClientId);
        Assert.Equal("agent/{version} (external, cli)", identity.UserAgentTemplate);
        Assert.Equal(["user:profile", "user:inference"], identity.Scopes);
        Assert.Equal("https://claude.com/cai/oauth/authorize", identity.AuthorizeUrl);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", identity.TokenUrl);
    }

    [Fact]
    public void The_declared_identity_drives_the_authorize_url_that_gets_built()
    {
        // Es el test que ata §1 con el flujo: cambiar la config cambia la URL que veria el usuario.
        var loaded = new ConfigLoader().Load(ValidYaml, ModelsYaml());
        var identity = ClaudeOAuthConfiguration.TryBuild(
            "anthropic", loaded.Providers!.Providers!["anthropic"].OAuth, [])!;

        var url = ClaudeOAuthAuthorizeUrlBuilder.Build(identity, "challenge", "state", port: 5555);

        Assert.StartsWith("https://claude.com/cai/oauth/authorize?", url, StringComparison.Ordinal);
        Assert.Contains("client_id=cid-yaml-0001", url, StringComparison.Ordinal);
        Assert.Contains("scope=user%3Aprofile%20user%3Ainference", url, StringComparison.Ordinal);
    }

    [Fact]
    public void The_declared_user_agent_reaches_the_inference_headers_with_the_version_filled()
    {
        var loaded = new ConfigLoader().Load(ValidYaml, ModelsYaml());
        var identity = ClaudeOAuthConfiguration.TryBuild(
            "anthropic", loaded.Providers!.Providers!["anthropic"].OAuth, [])!;

        Assert.Equal("agent/9.9.9 (external, cli)", identity.BuildUserAgent("9.9.9"));
    }

    [Fact]
    public void Scopes_may_be_written_as_a_space_separated_scalar_like_the_wire_sends_them()
    {
        var yaml = ValidYaml.Replace("scopes: [user:profile, user:inference]",
            "scopes: user:profile user:inference");
        var loaded = new ConfigLoader().Load(yaml, ModelsYaml());

        var identity = ClaudeOAuthConfiguration.TryBuild(
            "anthropic", loaded.Providers!.Providers!["anthropic"].OAuth, []);

        Assert.Equal(["user:profile", "user:inference"], identity?.Scopes);
    }

    [Fact]
    public void Profile_url_is_optional_and_defaults_to_null()
    {
        var loaded = new ConfigLoader().Load(ValidYaml, ModelsYaml());

        var identity = ClaudeOAuthConfiguration.TryBuild(
            "anthropic", loaded.Providers!.Providers!["anthropic"].OAuth, []);

        Assert.Null(identity?.ProfileUrl);
    }

    [Fact]
    public void A_declared_profile_url_survives_into_the_identity()
    {
        // La sangria tiene que ser la de las claves de oauth: (6 espacios), no la del literal raw.
        const string TokenLine = "tokenUrl: https://platform.claude.com/v1/oauth/token";
        var yaml = ValidYaml.Replace(TokenLine,
            TokenLine + "\n      profileUrl: https://api.example.com/profile");
        var loaded = new ConfigLoader().Load(yaml, ModelsYaml());

        var identity = ClaudeOAuthConfiguration.TryBuild(
            "anthropic", loaded.Providers!.Providers!["anthropic"].OAuth, []);

        Assert.Equal("https://api.example.com/profile", identity?.ProfileUrl);
    }

    [Fact]
    public void A_provider_without_an_oauth_section_has_no_identity()
    {
        var yaml = """
            providers:
              anthropic:
                family: AnthropicMessages
                baseUrl: https://api.anthropic.com
                authRef: anthropic-key
            """;
        var loaded = new ConfigLoader().Load(yaml, ModelsYaml());

        Assert.Null(loaded.Providers!.Providers!["anthropic"].OAuth);
        Assert.Null(ClaudeOAuthConfiguration.TryBuild("anthropic", null, []));
    }

    // ---- Secret ref ------------------------------------------------------------------------

    [Fact]
    public void The_credential_key_defaults_to_the_provider_and_can_be_overridden()
    {
        Assert.Equal("anthropic-oauth", ClaudeOAuthConfiguration.DefaultSecretRef("anthropic"));
        Assert.Equal("anthropic-oauth",
            ClaudeOAuthConfiguration.SecretRefFor("anthropic", new ProviderOAuthYaml()));
        Assert.Equal("mi-clave",
            ClaudeOAuthConfiguration.SecretRefFor("anthropic", new ProviderOAuthYaml { SecretRef = "mi-clave" }));
    }

    // ---- Validacion de contenido -----------------------------------------------------------

    [Fact]
    public void An_unknown_key_inside_the_oauth_section_is_a_diagnosed_config_failure()
    {
        // La sangria de las claves de oauth: son 6 espacios (el raw literal desindenta a 0).
        var yaml = ValidYaml.Replace("clientId: cid-yaml-0001",
            "clientId: cid-yaml-0001" + "\n      clientID: typo");
        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(yaml, ModelsYaml()));
        Assert.Contains(ex.Diagnostics, d => d.KeyPath.EndsWith("oauth.clientID", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://claude.com/oauth/authorize")]
    [InlineData("ftp://platform.claude.com/v1/oauth/token")]
    [InlineData("//relative-only")]
    [InlineData("https://user:pass@platform.claude.com/v1/oauth/token")]
    public void A_non_https_or_credentialed_endpoint_is_rejected(string url)
    {
        // Por aqui viajan el code y el refresh token: un esquema distinto o un userinfo embebido
        // los entregaria a otro sitio (plan §6).
        var yaml = ValidYaml.Replace("tokenUrl: https://platform.claude.com/v1/oauth/token",
            "tokenUrl: " + url);
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(yaml, ModelsYaml()));
    }

    [Fact]
    public void A_user_agent_without_the_version_placeholder_is_rejected()
    {
        var yaml = ValidYaml.Replace("userAgent: \"agent/{version} (external, cli)\"",
            "userAgent: \"agent/fijo (external, cli)\"");
        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(yaml, ModelsYaml()));
        Assert.Contains(ex.Diagnostics, d =>
            d.KeyPath.EndsWith("oauth.userAgent", StringComparison.Ordinal)
            && d.Message.Key == "config.oauth.userAgentNeedsVersion");
    }

    [Fact]
    public void Missing_required_fields_are_diagnosed_one_by_one()
    {
        var yaml = """
            providers:
              anthropic:
                family: AnthropicMessages
                baseUrl: https://api.anthropic.com
                oauth:
                  clientId: cid-solo-esto
            """;
        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(yaml, ModelsYaml()));
        // Cada campo ausente se diagnostica por separado: un unico error generico no diria al
        // usuario cual de los cinco le falto.
        foreach (var field in new[] { "userAgent", "authorizeUrl", "tokenUrl" })
        {
            Assert.Contains(ex.Diagnostics, d =>
                d.KeyPath.EndsWith("oauth." + field, StringComparison.Ordinal)
                && d.Message.Key == "config.missingRequired");
        }

        // scopes declarado pero vacio es otra condicion: no falta, esta vacio.
        Assert.Contains(ex.Diagnostics, d =>
            d.KeyPath.EndsWith("oauth.scopes", StringComparison.Ordinal)
            && d.Message.Key == "config.emptyCollection");
    }

    [Fact]
    public void Duplicate_scopes_are_rejected_rather_than_deduplicated_in_silence()
    {
        var yaml = ValidYaml.Replace("scopes: [user:profile, user:inference]",
            "scopes: [user:profile, user:profile]");
        var ex = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(yaml, ModelsYaml()));
        Assert.Contains(ex.Diagnostics, d => d.KeyPath.EndsWith("oauth.scopes", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_scope_list_is_rejected()
    {
        var yaml = ValidYaml.Replace("scopes: [user:profile, user:inference]", "scopes: []");
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(yaml, ModelsYaml()));
    }

    private static string ModelsYaml() => """
        models:
          sonnet:
            provider: anthropic
            context: 200000
            recommendedUsableContext: 190000
            maxOutput: 8192
            parametersBillions: 100
        """;
}
