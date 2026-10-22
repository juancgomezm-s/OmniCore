using System.Globalization;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 120, 35)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 120, 35)]
    public void Extension_form_edits_typed_fields_preserves_draft_and_saves_through_real_Core(string locale, int width, int height) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var manager = new ExtensionPackageManager(paths);
        manager.Install(ExtensionPlatformTests.PreferenceExample(Path.Combine(fx.Root, "example-source")), CancellationToken.None);
        manager.Change("example.preferences", "enable", true);
        fx.StartTui(initialColumns: width, initialRows: height, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.ShowExtensionView(new("example.preferences", "preferences")));
        fx.Wait(() => fx.App.Overlay?.Id == "extension-typed-preferences", "typed form renderer opened");
        var title = Descendants(fx.App.Overlay!).OfType<TextField>().Single(field => field.Id == "extension-field-title");
        var interval = Descendants(fx.App.Overlay!).OfType<TextField>().Single(field => field.Id == "extension-field-interval");
        var option = Descendants(fx.App.Overlay!).OfType<ListView>().Single(field => field.Id == "extension-field-detail");
        Assert.Equal(2.5m.ToString(PreferenceInput.Culture(locale)), fx.Read(() => interval.Text.ToString()));
        Assert.NotNull(Descendants(fx.App.Overlay!).OfType<Label>().Single(field => field.Id == "extension-field-api"));
        fx.Invoke(() => title.SetFocus());
        Assert.True(fx.Read(() => title.HasFocus), "Form input must receive keyboard focus through its parent containers.");
        ExportPreferenceFrame(fx, width, height, "extension-form-" + locale, new Localization(locale).Resolve("extension.form.title"));
        fx.Invoke(() => title.Text = "");
        fx.InjectKey(new Key('Z'));
        fx.Wait(() => title.Text.ToString() == "Z", "Physical input reaches the editable field, not the composer.");
        fx.Invoke(() =>
        {
            title.Text = "Nombre Ñandú";
            interval.Text = 3.5m.ToString(PreferenceInput.Culture(locale));
            option.SelectedItem = 2;
        });
        ClickId(fx, "extension-view-refresh");
        var local = new Localization(locale);
        Assert.Contains(local.Resolve("extension.form.draft"), fx.Read(() => OverlayText(fx.App.Overlay!)));
        Assert.Equal("Nombre Ñandú", fx.Read(() => title.Text.ToString()));
        ClickId(fx, "extension-form-save");
        fx.Wait(() => fx.App.Overlay is { } overlay && OverlayText(overlay).Contains(local.Resolve("extension.form.saved"), StringComparison.Ordinal), "Core acknowledged typed values");
        var result = fx.Server.Query("extensions.configuration:" + Uri.EscapeDataString("{\"extensionId\":\"example.preferences\",\"key\":\"display\"}"), CancellationToken.None)!;
        using var saved = JsonDocument.Parse(result.Json);
        Assert.Equal("Nombre Ñandú", saved.RootElement.GetProperty("title").GetString());
        Assert.Equal(3.5m, saved.RootElement.GetProperty("interval").GetDecimal());
        Assert.Equal("wide", saved.RootElement.GetProperty("detail").GetString());
        Assert.Equal("1.0", saved.RootElement.GetProperty("api").GetString());
    });

    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 120, 35)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 120, 35)]
    public void Extension_indicator_renderer_shows_localized_states_and_unavailable_values(string locale, int width, int height) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        var manager = new ExtensionPackageManager(paths);
        manager.Install(ExtensionPlatformTests.PreferenceExample(Path.Combine(fx.Root, "example-source")), CancellationToken.None);
        manager.Change("example.preferences", "enable", true);
        fx.StartTui(initialColumns: width, initialRows: height, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.ShowExtensionView(new("example.preferences", "indicators")));
        fx.Wait(() => fx.App.Overlay?.Id == "extension-typed-preferences", "indicator renderer opened");
        var local = new Localization(locale);
        var view = Descendants(fx.App.Overlay!).OfType<SidebarView>().Single(indicator => indicator.Id == "extension-indicators");
        Assert.Contains("—", fx.Read(() => view.Text));
        Assert.Contains(local.Resolve("extension.indicator.unknown"), fx.Read(() => view.Text));
        ExportPreferenceFrame(fx, width, height, "extension-indicators-" + locale, local.Resolve("extension.indicator.unknown"));
    });
}
