using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 80, 25)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 80, 25)]
    [InlineData("es", 120, 35)]
    [InlineData("en", 120, 35)]
    [InlineData("fr", 120, 35)]
    [InlineData("it", 120, 35)]
    public void Model_profile_limits_layout_scrolls_and_keeps_save_cancel_available(
        string locale, int columns, int rows) => RunTuiTest(fx =>
    {
        var key = new ModelPolicyKeyDto("local", "profile-layout-model");
        var policyRoot = Path.Combine(fx.Root, "profile-layout-policies");
        var config = Path.Combine(policyRoot, "config");
        Assert.Equal(config, OmniHost.CreatePlatformPaths(policyRoot).ConfigDirectory);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "providers.yaml"),
            "providers:\n  local: { baseUrl: http://127.0.0.1:8099/v1, auth: none }\n");
        File.WriteAllText(Path.Combine(config, "models.yaml"),
            "models:\n  profile-layout-model: { provider: local, context: 8192, maxOutput: 2048 }\n");
        var policies = ModelPolicyHost.Create(policyRoot);
        Assert.True(policies.SupportsDiscovery);
        var account = new TestAccount { Current = new(true, "fixture", null, false) };
        policies.Set(key, 0, "FullAgent", null, CancellationToken.None);
        var initial = policies.Get(key, CancellationToken.None)!;
        fx.StartTui(policies, account: account, initialColumns: columns, initialRows: rows,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        OpenProfileLimits(fx, locale);

        var labels = ModelPolicyProfileLabels.For(locale);
        var panel = fx.App.Overlay!.SubViews.Single(view => view.Id == "model-profile-limits-panel");
        var frame = fx.App.Overlay;
        Assert.Equal(columns, fx.Application.Driver!.Contents!.GetLength(1));
        Assert.Equal(rows, fx.Application.Driver.Contents.GetLength(0));
        Assert.True(panel.Frame.Width > 0 && panel.Frame.Height > 0);
        Assert.True(panel.Frame.Bottom < frame.Frame.Height - 2,
            "The scroll viewport must stop above the fixed footer.");
        Assert.Equal(
            Descendants(frame).OfType<Button>().Single(button => button.Id == "profile-limits-save").Frame.Y,
            Descendants(frame).OfType<Button>().Single(button => button.Id == "profile-limits-cancel").Frame.Y);

        var panelLabels = new[]
        {
            labels.Steps, labels.ToolCalls, labels.Bounds, labels.VisibleTools, labels.Capabilities,
            ModelPolicyProfileText.Get(locale, "basics"), ModelPolicyProfileText.Get(locale, "advanced"),
            labels.Mutation, labels.MaxFiles, labels.MaxLines, labels.RewriteRatio, labels.MutationMode,
            labels.DeletePolicy, labels.MovePolicy, labels.PriorRead, labels.VersionToken, labels.Validation,
            labels.GenerationDeadline, labels.AllowOrchestration, labels.ProviderReasoning,
        };
        foreach (var text in panelLabels)
        {
            var matching = Descendants(panel).Where(view => string.Equals(view.Text?.ToString(), text, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(matching);
            Assert.All(matching, view => Assert.True(view.Frame.Width >= text!.Length,
                $"{locale}: '{text}' needs {text.Length} columns but has {view.Frame.Width}."));
        }
        Assert.Contains(labels.Title, frame.Title, StringComparison.Ordinal);
        Assert.Equal(labels.Save, Descendants(frame).OfType<Button>()
            .Single(button => button.Id == "profile-limits-save").Text.ToString());
        foreach (var label in Descendants(panel).OfType<Label>())
        {
            var text = label.Text.ToString() ?? "";
            Assert.True(label.Frame.Width >= text.Length,
                $"{locale}: label '{text}' is clipped at {label.Frame.Width} columns.");
        }
        foreach (var checkbox in Descendants(panel).OfType<CheckBox>())
        {
            var text = checkbox.Text.ToString() ?? "";
            Assert.True(checkbox.Frame.Width >= text.Length + 4,
                $"{locale}: checkbox '{text}' is clipped at {checkbox.Frame.Width} columns.");
        }
        var controls = panel.SubViews.ToArray();
        var contentHeight = panel.GetContentSize().Height;
        foreach (var control in controls)
            Assert.True(control.Frame.Bottom <= contentHeight,
                $"{locale}: {control.Id} exceeds scroll content: {control.Frame} / {contentHeight}.");
        for (var left = 0; left < controls.Length; left++)
        for (var right = left + 1; right < controls.Length; right++)
        {
            var a = controls[left].Frame; var b = controls[right].Frame;
            if (a.X < b.Right && b.X < a.Right)
                Assert.True(a.Bottom <= b.Y || b.Bottom <= a.Y,
                    $"{locale}: controls overlap: {controls[left].Id} {a} and {controls[right].Id} {b}.");
        }

        var lastControl = Descendants(panel).OfType<CheckBox>()
            .Single(view => view.Id == "profile-limits-orchestration");
        fx.Invoke(() => lastControl.SetFocus());
        fx.Wait(() => panel.Viewport.Y > 0, "scroll follows focused controls to the bottom");
        Assert.True(panel.Viewport.Y > 0);

        var previousSnapshot = Environment.GetEnvironmentVariable("OMNICORE_TUI_SNAPSHOT_DIR");
        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "omnicore-model-profile-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_TUI_SNAPSHOT_DIR", snapshotDirectory);
            ExportPreferenceFrame(fx, columns, rows, $"model-profile-{locale}", labels.Title);
            var svgPath = Path.Combine(snapshotDirectory, $"settings-model-profile-{locale}-{columns}x{rows}.svg");
            var svg = File.ReadAllText(svgPath);
            Assert.Contains("<svg", svg, StringComparison.Ordinal);
            var driverFrame = File.ReadAllText(Path.ChangeExtension(svgPath, ".txt"));
            Assert.Contains(labels.Title, driverFrame, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable("OMNICORE_TUI_SNAPSHOT_DIR", previousSnapshot); }

        // Saving a narrower step limit must reach the existing persistence path.
        var steps = Descendants(panel).OfType<TextField>().Single(view => view.Id == "profile-limits-steps");
        var ratio = Descendants(panel).OfType<TextField>().Single(view => view.Id == "profile-limits-rewrite-ratio");
        fx.Invoke(() => { panel.Viewport = new System.Drawing.Rectangle(panel.Viewport.X, 0,
            panel.Viewport.Width, panel.Viewport.Height); steps.Text = "2";
            ratio.Text = 0.5d.ToString("R", PreferenceInput.Culture(locale)); });
        fx.Invoke(() => steps.Text = "0");
        ClickProfileButtonWhenAvailable(fx, "profile-limits-save");
        Assert.Equal("model-profile-limits", fx.App.Overlay!.Id);
        Assert.Equal(labels.Invalid, Descendants(fx.App.Overlay!).OfType<Label>()
            .Single(view => view.Id == "profile-limits-status").Text.ToString());
        Assert.Equal("0", steps.Text.ToString());
        Assert.Equal(0.5d.ToString("R", PreferenceInput.Culture(locale)), ratio.Text.ToString());
        Assert.Equal(initial.Revision, policies.Get(key, CancellationToken.None)!.Revision);
        fx.Invoke(() => steps.Text = "2");
        ClickProfileButtonWhenAvailable(fx, "profile-limits-save");
        fx.Wait(() => fx.App.Overlay?.Id != "model-profile-limits", "save closes the profile editor");
        var saved = policies.Get(key, CancellationToken.None)!;
        Assert.Equal(2, saved.MaxModelStepsPerTurn);
        Assert.Equal(0.5d, saved.MaxRewriteRatio);
        Assert.True(saved.Revision > initial.Revision);
        AssertNoProfileDiscovery();

        // A subsequent draft can be canceled without changing the persisted value.
        OpenProfileLimitsInMaintenance(fx);
        var savedCalls = policies.Get(key, CancellationToken.None)!.MaxToolCallsPerTurn;
        Assert.True(savedCalls > 1);
        var calls = Descendants(fx.App.Overlay!).OfType<TextField>()
            .Single(view => view.Id == "profile-limits-tool-calls");
        var reopenedRatio = Descendants(fx.App.Overlay!).OfType<TextField>()
            .Single(view => view.Id == "profile-limits-rewrite-ratio");
        Assert.Equal(0.5d.ToString("R", PreferenceInput.Culture(locale)), reopenedRatio.Text.ToString());
        fx.Invoke(() => { calls.Text = "1";
            reopenedRatio.Text = 0.25d.ToString("R", PreferenceInput.Culture(locale)); });
        var concurrent = policies.SetProfileControls(key, saved.Revision, saved.MaxModelStepsPerTurn,
            savedCalls - 1, saved.MaxVisibleTools, saved.CapabilityCeiling, saved.MutationMode,
            saved.MaxFilesPerTurn, saved.MaxChangedLinesPerTurn, saved.MaxRewriteRatio,
            saved.DeletePolicy, saved.MoveOrRenamePolicy, saved.RequirePriorRead,
            saved.RequireExpectedVersionToken, saved.RequirePostEditValidation,
            saved.AllowOrchestration, saved.MaxGenerationSeconds, CancellationToken.None);
        ClickProfileButtonWhenAvailable(fx, "profile-limits-save");
        Assert.Equal("model-profile-limits", fx.App.Overlay!.Id);
        Assert.Equal(new Localization(locale).Resolve("shell.could_not_save_the_policy_reopen_the_model"),
            Descendants(fx.App.Overlay!).OfType<Label>().Single(view => view.Id == "profile-limits-status").Text.ToString());
        Assert.Equal("1", calls.Text.ToString());
        Assert.Equal(0.25d.ToString("R", PreferenceInput.Culture(locale)), reopenedRatio.Text.ToString());
        Assert.Equal(concurrent.Revision, policies.Get(key, CancellationToken.None)!.Revision);
        ClickProfileButtonWhenAvailable(fx, "profile-limits-cancel");
        fx.Wait(() => fx.App.Overlay?.Id != "model-profile-limits", "cancel closes the profile editor");
        Assert.Equal(concurrent.MaxToolCallsPerTurn, policies.Get(key, CancellationToken.None)!.MaxToolCallsPerTurn);
        Assert.Equal(0.5d, policies.Get(key, CancellationToken.None)!.MaxRewriteRatio);
        AssertNoProfileDiscovery();

        void AssertNoProfileDiscovery()
        {
            // Inspect the owned refresh handle too, so a queued task cannot make an
            // immediate zero-call assertion pass before discovery begins.
            fx.Invoke(() => Assert.Null(fx.App.GetType().GetField("_catalogCancellation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fx.App)));
            Assert.Equal(0, account.CatalogCalls);
        }
    });

    private static void OpenProfileLimits(TuiFixture fx, string locale)
    {
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
        ChoosePreferenceCategory(fx, "models_execution", locale);
        var action = locale switch
        {
            "en" => "Routing and spending limits…",
            "fr" => "Routage et limites de dépenses…",
            "it" => "Instradamento e limiti di spesa…",
            _ => "Rutas y límites de gasto…",
        };
        Click(fx, action);
        fx.Wait(() => fx.App.Overlay?.Id == "policy-preferences", "model execution settings visible");
        ClickProfileButtonWhenAvailable(fx, "policy-preferences-model-policies");
        OpenProfileLimitsInMaintenance(fx);
    }

    private static void OpenProfileLimitsInMaintenance(TuiFixture fx)
    {
        ClickProfileButtonWhenAvailable(fx, "model-policy-limits-profile-layout-model");
        fx.Wait(() => fx.App.Overlay?.Id == "model-profile-limits", "profile controls visible");
        fx.Wait(() => Descendants(fx.App.Overlay!).OfType<TextField>()
            .Any(field => field.Id == "profile-limits-steps" && field.Frame.Width > 0),
            "profile controls laid out");
    }

    private static void ClickProfileButtonWhenAvailable(TuiFixture fx, string id) => fx.Wait(() =>
    {
        var clicked = false;
        fx.Invoke(() =>
        {
            var button = Descendants(fx.App.Overlay!).OfType<Button>().FirstOrDefault(view => view.Id == id);
            if (button is null) return;
            button.InvokeCommand(Terminal.Gui.Input.Command.Accept);
            clicked = true;
        });
        return clicked;
    }, "button available and accepted: " + id);
}
