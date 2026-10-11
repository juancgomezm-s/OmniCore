namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

public sealed class UiLocalePreferencesTests
{
    [Theory]
    [InlineData("es", "es")]
    [InlineData("ES-mx", "es")]
    [InlineData("en_US", "en")]
    [InlineData("fr-FR", "fr")]
    [InlineData("it-IT", "it")]
    public void Supported_locale_tags_normalize_to_the_catalog_language(string input, string expected)
    {
        Assert.True(UiLocaleCodes.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pt-BR")]
    [InlineData("fr--FR")]
    [InlineData("-fr")]
    public void Unsupported_locale_tags_are_rejected(string input) =>
        Assert.False(UiLocaleCodes.TryNormalize(input, out _));

    [Fact]
    public void Locale_precedence_records_source_and_invalid_override_falls_back_explicitly_to_spanish()
    {
        var cli = LocaleSelectionResolver.Resolve("fr-FR", "it", "en");
        Assert.Equal("fr", cli.Locale);
        Assert.Equal(LocaleSelectionSource.CommandLine, cli.Source);
        Assert.True(cli.HasProcessOverride);

        var environment = LocaleSelectionResolver.Resolve(null, "IT-it", "en");
        Assert.Equal("it", environment.Locale);
        Assert.Equal(LocaleSelectionSource.Environment, environment.Source);

        var user = LocaleSelectionResolver.Resolve(null, null, "en-US");
        Assert.Equal("en", user.Locale);
        Assert.Equal(LocaleSelectionSource.UserSetting, user.Source);
        Assert.True(user.HasCompleteLegacyCatalog);

        var invalid = LocaleSelectionResolver.Resolve("pt", "it", "en");
        Assert.Equal("es", invalid.Locale);
        Assert.Equal(LocaleSelectionSource.CommandLine, invalid.Source);
        Assert.Equal("pt", invalid.InvalidValue);
        Assert.True(invalid.HasCompleteLegacyCatalog);

        var fallback = LocaleSelectionResolver.Resolve(null, null, null);
        Assert.Equal(("es", LocaleSelectionSource.Default), (fallback.Locale, fallback.Source));
    }

    [Fact]
    public void New_preferences_category_copy_is_real_in_all_four_locales_and_legacy_fallback_is_reported()
    {
        Assert.Equal(9, PreferenceCategoryCatalog.All.Count);
        Assert.Equal(27, PreferenceCategoryCatalog.Keys.Count);
        Assert.Equal(27, PreferenceCategoryCatalog.Keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var locale in UiLocaleCodes.Supported)
        {
            var localization = new Localization(locale);
            foreach (var key in PreferenceCategoryCatalog.Keys)
            {
                var resolved = localization.ResolveDetailed(key);
                Assert.False(resolved.UsedFallback, $"{locale} fell back for {key}");
                Assert.Equal(locale, resolved.ResourceLocale);
                Assert.NotEqual(key, resolved.Text);
            }
            Assert.Equal(9, PreferenceCategoryCatalog.Resolve(localization).Count);
        }

        var frenchLegacy = new Localization("fr").ResolveDetailed("tool.process.exec.running");
        Assert.True(frenchLegacy.UsedFallback);
        Assert.Equal("en", frenchLegacy.ResourceLocale);
        Assert.Equal("Running {executable}", frenchLegacy.Text);
        Assert.False(new Localization("fr").ResolveDetailed(PreferenceCategoryCatalog.All[0].TitleKey).UsedFallback);
    }

    [Fact]
    public void User_locale_settings_roundtrip_with_revision_and_preserve_other_settings()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Paths.ConfigDirectory, "settings.yaml");
        File.WriteAllText(file, "budget: { session: 3, daily: 9 }\nsidebar: { mode: stacked }\n"
            + "widgets:\n  core.context: { visible: auto, priority: 70 }\n");
        var settings = new UserLocaleConfiguration(fixture.Paths);
        var initial = settings.Read();
        Assert.Empty(initial.Diagnostics);
        Assert.Equal("es", initial.Locale);
        settings.Set(new UiLocaleUpdateRequest("User", "fr-CA", initial.Revision), CancellationToken.None);

        var reopened = new UserLocaleConfiguration(fixture.Paths).Read();
        Assert.Empty(reopened.Diagnostics);
        Assert.Equal("fr", reopened.Locale);
        Assert.NotEqual(initial.Revision, reopened.Revision);
        var loaded = new ConfigLoader().Load(null, null, File.ReadAllText(file));
        Assert.Equal("fr", loaded.Settings!.Ui!.Locale);
        Assert.Equal(3, loaded.Settings.Budget!.Session);
        Assert.Equal("stacked", loaded.Settings.Sidebar!.Mode);
        Assert.Equal(70, loaded.Settings.Widgets!["core.context"].Priority);

        Assert.Throws<InvalidOperationException>(() => settings.Set(
            new UiLocaleUpdateRequest("User", "it", initial.Revision), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => settings.Set(
            new UiLocaleUpdateRequest("Project", "it", reopened.Revision), CancellationToken.None));
        Assert.Throws<ArgumentException>(() => settings.Set(
            new UiLocaleUpdateRequest("User", "pt-BR", reopened.Revision), CancellationToken.None));
        Assert.Equal("fr", settings.Read().Locale);
    }

    [Theory]
    [InlineData("ui: { locale: pt }")]
    [InlineData("ui: { locale: null }")]
    [InlineData("ui: { locale: en, other: yes }")]
    [InlineData("ui: en")]
    public void Invalid_user_locale_settings_are_reported_and_cannot_be_overwritten(string yaml)
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Paths.ConfigDirectory, "settings.yaml");
        File.WriteAllText(file, yaml);
        var settings = new UserLocaleConfiguration(fixture.Paths);
        var snapshot = settings.Read();
        Assert.NotEmpty(snapshot.Diagnostics);
        Assert.Equal("es", snapshot.Locale);
        Assert.Throws<InvalidOperationException>(() => settings.Set(
            new UiLocaleUpdateRequest("User", "en", snapshot.Revision), CancellationToken.None));
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null, null, yaml));
        Assert.Equal(yaml, File.ReadAllText(file));
    }

    [Fact]
    public void Locale_setting_is_rejected_in_project_and_workspace_files()
    {
        using var fixture = new Fixture();
        var project = Path.Combine(fixture.Workspace, ".omnicore");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "settings.yaml"), "ui: { locale: fr }\n");
        var projectError = Assert.Throws<ConfigValidationException>(() =>
            WorkspaceConfigurationLoader.Load(fixture.Workspace, trusted: true));
        Assert.Contains(projectError.Diagnostics, diagnostic => diagnostic.KeyPath == "ui");
        File.Delete(Path.Combine(project, "settings.yaml"));

        var localSettings = Path.Combine(OmniHost.WorkspaceDataDirectory(fixture.Paths, fixture.Workspace), "settings.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(localSettings)!);
        File.WriteAllText(localSettings, "ui: { locale: it }\n");
        var localError = Assert.Throws<ConfigValidationException>(() =>
            ScopedSettingsLoader.Load(fixture.Paths, fixture.Workspace, trusted: true));
        Assert.Contains(localError.Diagnostics, diagnostic => diagnostic.File == "workspace/settings.yaml"
            && diagnostic.KeyPath == "ui");
    }

    [Fact]
    public void Cancelled_locale_write_does_not_create_or_change_settings()
    {
        using var fixture = new Fixture();
        var settings = new UserLocaleConfiguration(fixture.Paths);
        var initial = settings.Read();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => settings.Set(
            new UiLocaleUpdateRequest("User", "it", initial.Revision), cancellation.Token));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.ConfigDirectory, "settings.yaml")));
    }

    [Fact]
    public void Protocol_query_and_save_require_a_trusted_user_action()
    {
        using var fixture = new Fixture();
        var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink(),
            platformPathsFactory: () => fixture.Paths);
        var initialResult = server.Query(UiLocalePreferencesProtocol.QueryName, CancellationToken.None);
        var initial = UiLocalePreferencesProtocol.Decode(initialResult!.Json)!;
        Assert.Equal("es", initial.Locale);
        Assert.Empty(initial.Diagnostics);

        var command = UiLocalePreferencesProtocol.Configure("User", "it-IT", initial.Revision);
        Assert.Equal("error", server.Send(command, CancellationToken.None).Status);
        Assert.False(File.Exists(Path.Combine(fixture.Paths.ConfigDirectory, "settings.yaml")));
        Assert.Equal("ok", server.SendUserAction(command, CancellationToken.None).Status);
        var saved = UiLocalePreferencesProtocol.Decode(server.Query(
            UiLocalePreferencesProtocol.QueryName, CancellationToken.None)!.Json)!;
        Assert.Equal("it", saved.Locale);
        Assert.NotEqual(initial.Revision, saved.Revision);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-ui-locale-" + Guid.NewGuid().ToString("N"));
        public DefaultPlatformPaths Paths { get; }
        public string Workspace { get; }

        public Fixture()
        {
            Paths = new DefaultPlatformPaths(Path.Combine(_root, "data"), Path.Combine(_root, "config"));
            Directory.CreateDirectory(Paths.ConfigDirectory);
            Workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(Workspace);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
