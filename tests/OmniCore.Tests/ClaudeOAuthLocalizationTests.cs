using OmniCore.Client;

namespace OmniCore.Tests;

/// <summary>
/// Paridad es/en de las claves del flujo OAuth de Claude (§9.12 y ADR-0040 del plan).
/// </summary>
public sealed class ClaudeOAuthLocalizationTests
{
    /// <summary>Claves que el flujo emite. Si una falta, la UI muestra la clave en crudo.</summary>
    private static readonly string[] FlowKeys =
    [
        "claude.oauth.awaiting_browser",
        "claude.oauth.awaiting_browser_manual",
        "claude.oauth.exchanging",
        "claude.oauth.fetching_profile",
        "claude.oauth.persisting",
        "claude.oauth.succeeded",
        "claude.oauth.failed.cancelled",
        "claude.oauth.failed.timeout",
        "claude.oauth.failed.callback",
        "claude.oauth.failed.browser",
        "claude.oauth.failed.transport",
        "claude.oauth.failed.exchange",
        "claude.oauth.failed.exchange_invalid",
        "claude.oauth.failed.storage",
        "claude.oauth.manual.bad_format",
        "claude.oauth.manual.prompt",
        "doctor.claude.oauth.none",
        "doctor.claude.oauth.active",
        "doctor.claude.oauth.dead",
        "doctor.claude.oauth.notConfigured",
        "config.oauth.httpsRequired",
        "config.oauth.noUserInfoInUrl",
        "config.oauth.userAgentNeedsVersion",
    ];

    [Theory]
    [MemberData(nameof(FlowCases))]
    public void The_key_exists_in_both_locales(string key)
    {
        Assert.True(Localization.HasInBoth(key), $"clave sin recurso en alguno de los dos idiomas: {key}");
    }

    public static TheoryData<string> FlowCases()
    {
        var data = new TheoryData<string>();
        foreach (var key in FlowKeys)
        {
            data.Add(key);
        }

        return data;
    }

    [Fact]
    public void No_localization_key_is_present_in_only_one_language()
    {
        // Regla general (ADR-0040), no solo para OAuth: una clave huesped en un idioma se ve en
        // crudo al cambiar de locale.
        var missing = Localization.SpanishKeys.Except(Localization.EnglishKeys).Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(missing);

        var extra = Localization.EnglishKeys.Except(Localization.SpanishKeys).Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(extra);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void Placeholders_survive_resolution_in_every_locale(string locale)
    {
        // Un placeholder sin sustituir en la cadena resuelta es un bug de presentacion que solo se
        // ve en pantalla: se comprueba aqui con los argumentos que pasa el llamador real.
        var localization = new Localization(locale);
        var active = localization.Resolve("doctor.claude.oauth.active", new Dictionary<string, string>
        {
            ["provider"] = "anthropic",
            ["account"] = "u@example.com",
            ["plan"] = "max",
            ["tier"] = "tier-5x",
            ["expires"] = "2027-01-01T00:00:00Z",
        });

        Assert.DoesNotContain("{provider}", active, StringComparison.Ordinal);
        Assert.DoesNotContain("{account}", active, StringComparison.Ordinal);
        Assert.DoesNotContain("{plan}", active, StringComparison.Ordinal);
        Assert.DoesNotContain("{tier}", active, StringComparison.Ordinal);
        Assert.DoesNotContain("{expires}", active, StringComparison.Ordinal);
        Assert.Contains("anthropic", active, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void The_unknown_key_diagnostic_renders_without_a_crashing_placeholder(string locale)
    {
        var localization = new Localization(locale);
        var text = localization.Resolve("config.oauth.userAgentNeedsVersion", new Dictionary<string, string>
        {
            ["path"] = "providers.anthropic.oauth.userAgent",
        });

        // La clave lleva {{version}} literal (el marcador del user-agent, no un argumento de
        // localizacion): tiene que sobrevivir al formateo.
        Assert.Contains("{version}", text, StringComparison.Ordinal);
        Assert.Contains("providers.anthropic.oauth.userAgent", text, StringComparison.Ordinal);
    }
}
