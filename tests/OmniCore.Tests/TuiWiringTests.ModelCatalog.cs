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
                draft.RecommendedUsableContext, draft.MaxOutput, draft.ParametersBillions, draft.Aliases, true, [],
                draft.InputModalities, draft.OutputModalities, draft.ImageGenerationMaxCostUsd));
            else
            {
                var index = Models.FindIndex(row => row.Id == draft.ExistingId);
                Models[index] = new(draft.Id, draft.ProviderId, draft.Context, draft.RecommendedUsableContext,
                    draft.MaxOutput, draft.ParametersBillions, draft.Aliases, true, [],
                    draft.InputModalities, draft.OutputModalities, draft.ImageGenerationMaxCostUsd);
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
    [InlineData("es", "0.25")]
    [InlineData("en", "0.25")]
    [InlineData("fr", "0,25")]
    [InlineData("it", "0,25")]
    public void Model_catalog_editor_saves_image_cost_as_invariant_decimal_in_every_locale(string locale, string value) => RunTuiTest(fx =>
    {
        var host = new ModelCatalogFixture();
        fx.StartTui(modelCatalogPreferences: host, localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(fx.App.ShowModelCatalogPreferences);
        ClickId(fx, "model-catalog-add");
        fx.Invoke(() =>
        {
            SetField(fx.App.Overlay!, "model-catalog-id", "cost-model");
            SetField(fx.App.Overlay!, "model-catalog-output-modalities", "text,image");
            SetField(fx.App.Overlay!, "model-catalog-image-cost", value);
        });
        ClickId(fx, "model-catalog-save");
        var saved = Assert.Single(host.Models, model => model.Id == "cost-model");
        Assert.Equal(0.25m, saved.ImageGenerationMaxCostUsd);
        Assert.Equal(["text", "image"], saved.OutputModalities);
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

    [Fact]
    public void Model_catalog_creates_and_selects_a_separate_openrouter_image_route_from_the_editor()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-tui-image-route-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = Path.Combine(root, "config"); var workspace = Path.Combine(root, "workspace");
            var data = Path.Combine(root, "workspace-data");
            Directory.CreateDirectory(config); Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(config, "providers.yaml"),
                "providers:\n  text-route: { family: OpenAiChatCompatible, baseUrl: https://openrouter.ai/api/v1, authRef: shared-key, billingMode: MeteredCurrency }\n");
            File.WriteAllText(Path.Combine(config, "models.yaml"), "models: {}\n");
            var host = ModelCatalogPreferencesHost.Create(config, workspace, data);
            RunTuiTest(fx =>
            {
                fx.StartTui(modelCatalogPreferences: host, initialColumns: 120, initialRows: 35);
                fx.Invoke(fx.App.ShowModelCatalogPreferences);
                ClickId(fx, "model-catalog-add");
                fx.Invoke(() =>
                {
                    SetField(fx.App.Overlay!, "model-catalog-id", "image-model");
                    SetField(fx.App.Overlay!, "model-catalog-provider", "text-route");
                });
                ClickId(fx, "model-catalog-create-openrouter-image-route");
                Assert.Equal("text-route-images", Assert.IsType<TextField>(
                    Descendants(fx.App.Overlay!).Single(view => view.Id == "model-catalog-provider")).Text.ToString());
                Assert.Equal("text,image", Assert.IsType<TextField>(
                    Descendants(fx.App.Overlay!).Single(view => view.Id == "model-catalog-output-modalities")).Text.ToString());
                fx.Invoke(() =>
                {
                    SetField(fx.App.Overlay!, "model-catalog-context", "8192");
                    SetField(fx.App.Overlay!, "model-catalog-usable-context", "6144");
                    SetField(fx.App.Overlay!, "model-catalog-image-cost", "0.25");
                });
                ClickId(fx, "model-catalog-save");
                var loaded = new ConfigLoader().Load(File.ReadAllText(Path.Combine(config, "providers.yaml")),
                    File.ReadAllText(Path.Combine(config, "models.yaml")));
                Assert.Equal("text-route-images", loaded.Registry.Model("image-model")!.ProviderId);
                Assert.Equal("openrouter-images", loaded.Registry.Provider("text-route-images")!.Profile);
                Assert.Contains("authRef: shared-key", File.ReadAllText(Path.Combine(config, "providers.yaml")));
                Assert.DoesNotContain("apiKey", File.ReadAllText(Path.Combine(config, "providers.yaml")), StringComparison.OrdinalIgnoreCase);
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Model_catalog_empty_output_field_saves_no_limit_in_every_locale(string locale) => RunTuiTest(fx =>
    {
        var host = new ModelCatalogFixture();
        fx.StartTui(modelCatalogPreferences: host,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine), initialColumns: 80, initialRows: 25);
        fx.Invoke(fx.App.ShowModelCatalogPreferences);
        ClickId(fx, "model-catalog-add");
        fx.Invoke(() =>
        {
            Assert.Empty(Assert.IsType<TextField>(Descendants(fx.App.Overlay!)
                .Single(view => view.Id == "model-catalog-max-output")).Text.ToString());
            SetField(fx.App.Overlay!, "model-catalog-id", "uncapped-model");
            SetField(fx.App.Overlay!, "model-catalog-provider", "fixture");
            SetField(fx.App.Overlay!, "model-catalog-context", "8192");
            SetField(fx.App.Overlay!, "model-catalog-usable-context", "6144");
        });
        AssertModelCatalogLayoutFits(fx);
        fx.Invoke(() =>
        {
            var fields = Descendants(fx.App.Overlay!).Single(view => view.Id == "model-catalog-editor-fields");
            var aliases = Descendants(fields).Single(view => view.Id == "model-catalog-aliases");
            aliases.SetFocus();
            Assert.True(fields.Viewport.Y > 0, "los últimos campos desplazan el formulario corto");
            Assert.True(aliases.Frame.Top >= fields.Viewport.Top && aliases.Frame.Bottom <= fields.Viewport.Bottom,
                "el campo enfocado queda visible sin cubrir Guardar");
        });
        ClickId(fx, "model-catalog-save");
        Assert.Equal(1, host.Saves);
        Assert.Null(host.Models.Single(model => model.Id == "uncapped-model").MaxOutput);
    });

    private static void SetField(View root, string id, string value) =>
        Assert.IsType<TextField>(Descendants(root).Single(view => view.Id == id)).Text = value;

    private static void SetMultilineField(View root, string id, string value) =>
        Assert.IsType<TextView>(Descendants(root).Single(view => view.Id == id)).Text = value;

    private static void AssertModelCatalogLayoutFits(TuiFixture fx)
    {
        fx.Invoke(() =>
        {
            var frame = fx.App.Overlay!;
            CheckControls(frame, frame.Viewport.Width, frame.Viewport.Height);
            foreach (var panel in Descendants(frame).Where(view => view.Id == "model-catalog-editor-fields"))
                CheckControls(panel, panel.GetContentSize().Width, panel.GetContentSize().Height);
        });

        static void CheckControls(View container, int width, int height)
        {
            foreach (var child in container.SubViews)
            {
                Assert.True(child.Frame.Bottom <= height, $"{child.Id} is within its container: {child.Frame}");
                Assert.True(child.Frame.Right <= width, $"{child.Id} is within its container: {child.Frame}");
            }
            var controls = container.SubViews.ToArray();
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
        }
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
