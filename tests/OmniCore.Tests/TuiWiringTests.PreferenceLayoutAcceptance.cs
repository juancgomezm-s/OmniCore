using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Local_engine_actions_and_help_do_not_overlap_in_a_narrow_localized_terminal(string locale) => RunTuiTest(fx =>
    {
        var host = new TuiLocalEngineHost(Path.Combine(fx.Root, "local-layout-fixture"));
        fx.StartTui(localEngines: host, localEngineSupervisor: new LocalEngineSupervisorFixture(),
            initialColumns: 80, initialRows: 24,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
        ChoosePreferenceCategory(fx, "local_engines", locale);
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "local-engine-runtime" && view.Frame.Height == 2),
            "localized engine layout complete");
        fx.Invoke(() =>
        {
            var views = Descendants(fx.App.Overlay!).Where(view => !string.IsNullOrEmpty(view.Id))
                .ToDictionary(view => view.Id!, StringComparer.Ordinal);
            var add = views["local-engine-add"].Frame;
            var edit = views["local-engine-edit"].Frame;
            var runtime = views["local-engine-runtime"].Frame;
            var delete = views["local-engine-delete"].Frame;
            var notice = views["local-engine-notice"].Frame;
            Assert.True(runtime.Top >= add.Bottom && delete.Top >= edit.Bottom);
            Assert.True(notice.Top > Math.Max(runtime.Bottom, delete.Bottom));
            Assert.True(views["preferences-panel"].GetContentSize().Height >= notice.Bottom);
            Assert.Equal(3, notice.Height);
            var runtimeLabel = views["local-engine-runtime"].Text.ToString()!;
            Assert.True(runtime.Width >= runtimeLabel.Length + 2,
                $"Runtime action must fit without clipping: {runtimeLabel}");
        });
        ExportPreferenceFrame(fx, 80, 24, "local-engines-" + locale, "127.0.0.1");
    }, noColor: false);

    [Fact]
    public void Italian_language_label_is_not_cut_to_the_previous_fixed_22_columns() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 80, initialRows: 24,
            localeSelection: new LocaleSelection("it", LocaleSelectionSource.CommandLine));
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
        ChoosePreferenceCategory(fx, "general", "it");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "preferences-locale-label" && view.Frame.Width > 22),
            "Italian language label has the available width");
        var label = Descendants(fx.App.Overlay!).OfType<Label>().Single(view => view.Id == "preferences-locale-label");
        Assert.Equal("Lingua dell’interfaccia", label.Text.ToString());
        Assert.True(label.Frame.Width >= label.Text.ToString()!.Length);
        ExportPreferenceFrame(fx, 80, 24, "general-it", "Lingua dell’interfaccia");
    }, noColor: false);

    [Theory]
    [InlineData("es", "Animaciones de carga", "animaciones de carga")]
    [InlineData("en", "Loading animations", "loading animations")]
    [InlineData("fr", "Animations de chargement", "animations de chargement")]
    [InlineData("it", "Animazioni di caricamento", "animazioni di caricamento")]
    public void General_preferences_explain_and_open_loading_animation_settings(
        string locale, string actionLabel, string descriptionText) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 100, initialRows: 30,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "configuración visible");
        ChoosePreferenceCategory(fx, "general", locale);

        var description = Descendants(fx.App.Overlay!).OfType<Label>()
            .Single(view => view.Id == "preferences-description").Text.ToString()!;
        Assert.Contains(descriptionText, description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/animation", description, StringComparison.Ordinal);
        Assert.Contains(actionLabel, Descendants(fx.App.Overlay!).OfType<Button>()
            .Single(view => view.Id == "preferences-animations-open").Text.ToString(), StringComparison.Ordinal);

        ClickId(fx, "preferences-animations-open");
        fx.Wait(() => fx.App.Overlay?.SubViews.Any(view => view.Id == "animation-preview") == true,
            "configuración y vista previa de animaciones visibles desde General");
    });
}
