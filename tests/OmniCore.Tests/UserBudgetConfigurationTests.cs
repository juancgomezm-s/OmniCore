namespace OmniCore.Tests;

using OmniCore.Host;

public sealed class UserBudgetConfigurationTests
{
    [Fact]
    public void Missing_settings_or_budget_children_use_legacy_defaults()
    {
        var noSettings = new ConfigLoader().Load(null, null);
        var emptyBudget = new ConfigLoader().Load(null, null, "budget: {}\n");
        var partial = new ConfigLoader().Load(null, null, "budget:\n  session: 1.25\n");

        Assert.Equal(5m, noSettings.SessionCapUsd);
        Assert.Equal(20m, noSettings.DailyCapUsd);
        Assert.Equal(5m, emptyBudget.SessionCapUsd);
        Assert.Equal(20m, emptyBudget.DailyCapUsd);
        Assert.Equal(1.25m, partial.SessionCapUsd);
        Assert.Equal(20m, partial.DailyCapUsd);
    }

    [Fact]
    public void Decimal_and_zero_caps_load_from_user_settings()
    {
        var loaded = new ConfigLoader().Load(null, null, "budget:\n  session: 0\n  daily: 12.375\n");

        Assert.Equal(0m, loaded.SessionCapUsd);
        Assert.Equal(12.375m, loaded.DailyCapUsd);
    }

    [Theory]
    [InlineData("budget:\n  session: -1\n", "budget.session", "config.outOfRange")]
    [InlineData("budget:\n  daily: nope\n", "budget.daily", "config.wrongType")]
    [InlineData("budget:\n  session: [1]\n", "budget.session", "config.wrongType")]
    [InlineData("budget:\n  surprise: 1\n", "budget.surprise", "config.unknownKey")]
    [InlineData("budget: false\n", "budget", "config.wrongType")]
    [InlineData("mystery: true\n", "mystery", "config.unknownKey")]
    public void Invalid_settings_fail_with_path_and_source_location(string yaml, string path, string diagnosticKey)
    {
        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null, null, yaml));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("settings.yaml", diagnostic.File);
        Assert.Equal(path, diagnostic.KeyPath);
        Assert.Equal(diagnosticKey, diagnostic.Message.Key);
        Assert.True(diagnostic.Line > 0);
        Assert.True(diagnostic.Column > 0);
    }

    [Fact]
    public void Host_reads_user_settings_only_from_the_config_directory_and_does_not_require_auth()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-user-budget-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(repo);
        try
        {
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "budget:\n  session: 2.5\n  daily: 7\n");
            File.WriteAllText(Path.Combine(repo, "settings.yaml"), "budget:\n  session: 0\n  daily: 0\n");

            var loaded = OmniHost.LoadUserConfiguration(config);

            Assert.Equal(2.5m, loaded.SessionCapUsd);
            Assert.Equal(7m, loaded.DailyCapUsd);
            Assert.Equal(OmniCore.Abstractions.AuthKind.None, loaded.Registry.Provider("local")!.Auth.Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
