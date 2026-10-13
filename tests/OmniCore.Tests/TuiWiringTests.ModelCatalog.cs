#pragma warning disable CS0618 // Assert the pinned multiline editor used by the model-alias form.
namespace OmniCore.Tests;

using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    private sealed class ModelCatalogFixture : IModelCatalogPreferencesHost
    {
        private long _revision = 1;
        public int Reads { get; private set; }
        public int Saves { get; private set; }
        public int Deletes { get; private set; }
        public List<ModelCatalogModel> Models { get; } = [
            new("fixture-model", "fixture", 8192, 6144, 2048, null, ["fixture-alias"], true, [])];

        public ModelCatalogPreferencesSnapshot Read(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            return Snapshot();
        }

        public ModelCatalogPreferencesSnapshot Save(ModelCatalogDraft draft, string expectedRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Saves++;
            Assert.Equal(_revision.ToString(), expectedRevision);
            if (draft.ExistingId is null) Models.Add(new(draft.Id, draft.ProviderId, draft.Context,
                draft.RecommendedUsableContext, draft.MaxOutput, draft.ParametersBillions, draft.Aliases, true, []));
            else
            {
                var index = Models.FindIndex(row => row.Id == draft.ExistingId);
                Models[index] = new(draft.Id, draft.ProviderId, draft.Context, draft.RecommendedUsableContext,
                    draft.MaxOutput, draft.ParametersBillions, draft.Aliases, true, []);
            }
            _revision++;
            return Snapshot();
        }

        public ModelCatalogPreferencesSnapshot Delete(string modelId, string expectedRevision, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Deletes++;
            Assert.Equal(_revision.ToString(), expectedRevision);
            Models.RemoveAll(row => row.Id == modelId);
            _revision++;
            return Snapshot();
        }

        private ModelCatalogPreferencesSnapshot Snapshot() => new(true, _revision.ToString(),
            [new("fixture", "OpenAiChatCompatible")], Models.ToArray(), []);
    }

    [Fact]
    public void Model_catalog_resources_have_complete_four_locale_coverage()
    {
        foreach (var locale in new[] { "es", "en", "fr", "it" })
        foreach (var key in ModelCatalogPreferencesResources.Keys)
            Assert.True(ModelCatalogPreferencesResources.TryTranslate(locale, key, out var text)
                && !string.IsNullOrWhiteSpace(text), $"{locale}: {key}");
        Assert.True(ModelCatalogPreferencesResources.TryTranslate("es", "modelCatalogPreferences.open", out _));
        Assert.False(ModelCatalogPreferencesResources.TryTranslate("de", "modelCatalogPreferences.open", out _));
    }

    [Theory]
    [InlineData("es", "1.5")]
    [InlineData("en", "1.5")]
    [InlineData("fr", "1,5")]
    [InlineData("it", "1,5")]
    public void Model_catalog_editor_accepts_locale_decimal_parameters(string locale, string value) => RunTuiTest(fx =>
    {
        var host = new ModelCatalogFixture();
        fx.StartTui(modelCatalogPreferences: host, localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(fx.App.ShowModelCatalogPreferences);
        ClickId(fx, "model-catalog-add");
        fx.Invoke(() =>
        {
            SetField(fx.App.Overlay!, "model-catalog-id", "decimal-model");
            SetField(fx.App.Overlay!, "model-catalog-parameters", value);
        });
        ClickId(fx, "model-catalog-save");
        Assert.Equal(1, host.Saves);
        Assert.Equal(1.5, Assert.Single(host.Models, row => row.Id == "decimal-model").ParametersBillions);
    });

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Model_catalog_home_has_real_80x25_frames_in_every_supported_locale(string locale) => RunTuiTest(fx =>
    {
        var host = new ModelCatalogFixture();
        fx.StartTui(modelCatalogPreferences: host, localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine),
            initialColumns: 80, initialRows: 25);
        fx.Invoke(fx.App.ShowModelCatalogPreferences);
        Assert.Equal("model-catalog-preferences", fx.App.Overlay!.Id);
        Assert.True(ModelCatalogPreferencesResources.TryTranslate(locale, "modelCatalogPreferences.title", out var title));
        ExportPreferenceFrame(fx, 80, 25, "model-catalog-" + locale, title);
        AssertModelCatalogLayoutFits(fx);
        ClickId(fx, "model-catalog-add");
        Assert.True(ModelCatalogPreferencesResources.TryTranslate(locale, "modelCatalogPreferences.newModel", out var editorTitle));
        ExportPreferenceFrame(fx, 80, 25, "model-catalog-editor-" + locale, editorTitle);
        AssertModelCatalogLayoutFits(fx);
    });

    [Fact]
    public void Model_catalog_open_is_read_only_until_explicit_save_and_editor_cancel_discards_draft()
    {
        RunTuiTest(fx =>
        {
            var host = new ModelCatalogFixture();
            fx.StartTui(modelCatalogPreferences: host, initialColumns: 80, initialRows: 25);
            fx.Invoke(fx.App.ShowModelCatalogPreferences);
            Assert.Equal(1, host.Reads);
            Assert.Equal(0, host.Saves);
            Assert.Equal(0, host.Deletes);
            Assert.Equal("model-catalog-preferences", fx.App.Overlay!.Id);
            KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "model-catalog-editor",
                "Enter edits the focused model");
            Assert.Equal("model-catalog-editor", fx.App.Overlay!.Id);
            KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "model-catalog-preferences",
                "Escape cancels the editor and returns to the list");
            Assert.Equal("model-catalog-preferences", fx.App.Overlay!.Id);
            ClickId(fx, "model-catalog-add");
            Assert.Equal("model-catalog-editor", fx.App.Overlay!.Id);
            Assert.True(ModelCatalogPreferencesResources.TryTranslate("es", "modelCatalogPreferences.newModel", out var editorTitle));
            ExportPreferenceFrame(fx, 80, 25, "model-catalog-editor", editorTitle);
            AssertModelCatalogLayoutFits(fx);
            fx.Invoke(() =>
            {
                SetField(fx.App.Overlay!, "model-catalog-id", "draft-model");
                SetField(fx.App.Overlay!, "model-catalog-provider", "fixture");
                SetField(fx.App.Overlay!, "model-catalog-context", "8192");
                SetField(fx.App.Overlay!, "model-catalog-usable-context", "8192");
                SetField(fx.App.Overlay!, "model-catalog-max-output", "2048");
                SetMultilineField(fx.App.Overlay!, "model-catalog-aliases", "draft-alias");
            });
            ClickId(fx, "model-catalog-cancel");
            Assert.Equal(0, host.Saves);
            Assert.DoesNotContain(host.Models, row => row.Id == "draft-model");
        });
    }

    [Fact]
    public void Model_catalog_explicit_save_registers_id_and_alias_and_delete_requires_confirmation()
    {
        RunTuiTest(fx =>
        {
            var host = new ModelCatalogFixture();
            fx.StartTui(modelCatalogPreferences: host, initialColumns: 120, initialRows: 35);
            fx.Invoke(fx.App.ShowModelCatalogPreferences);
            ClickId(fx, "model-catalog-add");
            fx.Invoke(() =>
            {
                SetField(fx.App.Overlay!, "model-catalog-id", "draft-model");
                SetField(fx.App.Overlay!, "model-catalog-provider", "fixture");
                SetField(fx.App.Overlay!, "model-catalog-context", "8192");
                SetField(fx.App.Overlay!, "model-catalog-usable-context", "6144");
                SetField(fx.App.Overlay!, "model-catalog-max-output", "2048");
                SetMultilineField(fx.App.Overlay!, "model-catalog-aliases", "draft-alias");
            });
            ClickId(fx, "model-catalog-save");
            Assert.Equal(1, host.Saves);
            Assert.Contains(host.Models, row => row.Id == "draft-model" && row.Aliases.Contains("draft-alias"));

            fx.Invoke(() =>
            {
                var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "model-catalog-list");
                list.SelectedItem = host.Models.FindIndex(row => row.Id == "draft-model");
            });
            fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("draft-alias"),
                "selection updates aliases/details without reopening the page");
            Assert.True(Descendants(fx.App.Overlay!).OfType<Button>().Single(button => button.Id == "model-catalog-edit").Enabled);
            Assert.True(Descendants(fx.App.Overlay!).OfType<Button>().Single(button => button.Id == "model-catalog-delete").Enabled);
            ClickId(fx, "model-catalog-delete");
            Assert.Equal("model-catalog-confirm", fx.App.Overlay!.Id);
            Assert.Equal(0, host.Deletes);
            ClickId(fx, "model-catalog-cancel-delete");
            Assert.Equal(0, host.Deletes);
            ClickId(fx, "model-catalog-delete");
            ClickId(fx, "model-catalog-confirm-delete");
            Assert.Equal(1, host.Deletes);
            Assert.DoesNotContain(host.Models, row => row.Id == "draft-model");
        });
    }

    private static void SetField(View root, string id, string value) =>
        Assert.IsType<TextField>(Descendants(root).Single(view => view.Id == id)).Text = value;

    private static void SetMultilineField(View root, string id, string value) =>
        Assert.IsType<TextView>(Descendants(root).Single(view => view.Id == id)).Text = value;

    private static void AssertModelCatalogLayoutFits(TuiFixture fx)
    {
        fx.Invoke(() =>
        {
            var frame = fx.App.Overlay!;
            foreach (var child in frame.SubViews)
            {
                Assert.True(child.Frame.Bottom <= frame.Viewport.Height, $"{child.Id} is within the viewport: {child.Frame}");
                Assert.True(child.Frame.Right <= frame.Viewport.Width, $"{child.Id} is within the viewport: {child.Frame}");
            }
            var controls = frame.SubViews.ToArray();
            for (var left = 0; left < controls.Length; left++)
            for (var right = left + 1; right < controls.Length; right++)
            {
                var a = controls[left].Frame; var b = controls[right].Frame;
                if (a.X < b.Right && b.X < a.Right)
                    Assert.True(a.Bottom <= b.Y || b.Bottom <= a.Y,
                        $"model catalog controls overlap: {controls[left].Id} {a} and {controls[right].Id} {b}");
            }
            foreach (var label in controls.OfType<Label>().Where(label => label.Id is null && label.Frame.Y >= 2))
                if (label.Frame.Height == 1)
                    Assert.True(label.Text.ToString().Length <= label.Frame.Width,
                        $"label should remain readable at this width: {label.Text}");
        });
    }

    [Fact]
    public void Model_catalog_filters_and_reports_position_with_wraparound()
    {
        RunTuiTest(fx =>
        {
            var host = new ModelCatalogFixture();
            for (var index = 0; index < 40; index++)
                host.Models.Add(new($"bulk-{index:00}", "fixture", 8192, 6144, 2048, null, [], true, []));
            fx.StartTui(modelCatalogPreferences: host, initialColumns: 120, initialRows: 35);
            fx.Invoke(fx.App.ShowModelCatalogPreferences);
            var overlay = fx.App.Overlay!;
            var list = Descendants(overlay).OfType<ListView>().Single(view => view.Id == "model-catalog-list");
            var position = Descendants(overlay).OfType<Label>().Single(view => view.Id == "model-catalog-position");
            Assert.StartsWith("1 de 41", position.Text.ToString());
            fx.Invoke(() => { list.SetFocus(); list.SelectedItem = 40; });
            fx.Wait(() => position.Text.ToString().Contains("41 de 41"), "posición final informada");
            Assert.Contains("fin", position.Text.ToString());
            fx.Invoke(() => list.NewKeyDownEvent(Terminal.Gui.Input.Key.CursorDown));
            fx.Wait(() => list.SelectedItem == 0, "bajar desde el final vuelve al inicio");
            fx.Invoke(() => Assert.IsType<TextField>(Descendants(overlay).Single(view => view.Id == "model-catalog-filter")).Text = "bulk-1");
            fx.Wait(() => list.Source!.Count == 10, "el filtro reduce la lista");
            Assert.Contains("(41)", position.Text.ToString());
            AssertModelCatalogLayoutFits(fx);
        });
    }
}
#pragma warning restore CS0618
