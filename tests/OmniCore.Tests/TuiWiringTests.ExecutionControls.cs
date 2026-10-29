namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    // Selecting a model now offers two explicit choices. Retain the current effort
    // and visibility in picker tests; dedicated controls tests exercise changing them.
    private static void ConfirmSelectedModelPreferences(TuiFixture fx, string modelId)
    {
        fx.Wait(() => fx.App.Overlay?.Id == "reasoning-preferences", "model selection offers effort configuration");
        Assert.Contains(modelId, fx.App.Header!.Text);
        var userDefault = fx.Server.Query("reasoningPreference", default)?.Json;
        var visible = fx.App.ShowReportedReasoning;
        Assert.True(Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "reasoning-apply").Enabled);
        ClickId(fx, "reasoning-apply");
        fx.Wait(() => fx.App.Overlay?.Id == "reasoning-visibility", "confirming effort offers provider reasoning visibility");
        Assert.Equal(userDefault, fx.Server.Query("reasoningPreference", default)?.Json);
        Assert.Equal(visible, fx.App.ShowReportedReasoning);
        ClickId(fx, visible ? "reasoning-visible" : "reasoning-hidden");
        ClickId(fx, "reasoning-visibility-apply");
        fx.Wait(() => fx.App.Overlay is null && fx.App.Composer!.HasFocus, "confirming visibility returns to the conversation");
        Assert.Equal(visible, fx.App.ShowReportedReasoning);
        Assert.Contains(modelId, fx.App.Header!.Text);
    }

    [Theory]
    [InlineData(80, 25, "es")]
    [InlineData(130, 38, "en")]
    [InlineData(80, 25, "fr")]
    [InlineData(130, 38, "it")]
    public void Execution_controls_fit_the_terminal_and_open_without_mutating_permissions(int columns, int rows, string locale) => RunTuiTest(fx =>
    {
        var security = SecurityPreferencesHost.Create(fx.Workspace, Path.Combine(fx.Root, "controls-data"), Path.Combine(fx.Root, "controls-config"));
        var before = security.Read();
        fx.StartTui(initialColumns: columns, initialRows: rows, securityPreferences: security,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        var defaultBefore = fx.Server.Query("sessionSecurityDefault", TestContext.Current.CancellationToken)?.Json;
        fx.Invoke(() => fx.App.Composer!.Text = "keep this task draft");
        fx.Invoke(() => fx.App.ShowExecutionPermissions());
        fx.Wait(() => fx.App.Overlay?.Id == "security-preferences", "permissions home");
        Assert.Equal(6, Descendants(fx.App.Overlay!).OfType<Button>().Count());
        Assert.All(Descendants(fx.App.Overlay!).OfType<Button>(), button =>
            Assert.True(button.Frame.Bottom <= fx.App.Overlay!.Viewport.Height, button.Id + " outside viewport"));
        ExportPreferenceFrame(fx, columns, rows, "execution-" + locale, ExecutionControlsResources.Get(locale, "permissions"));
        ClickId(fx, "session-security-default-change");
        fx.Wait(() => fx.App.Overlay?.Id == "security-ceiling", "explicit ceiling choices");
        var choices = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        Assert.Equal(4, choices.Source!.Count);
        ExportPreferenceFrame(fx, columns, rows, "ceiling-" + locale, new Localization(locale).Resolve("securityPreferences.default_ceiling"));
        fx.Invoke(() => choices.SelectedItem = 3);
        fx.InjectKey(new Key(KeyCode.Esc));
        Assert.Equal(before.Revision, security.Read().Revision);
        Assert.Equal(defaultBefore, fx.Server.Query("sessionSecurityDefault", TestContext.Current.CancellationToken)?.Json);
        ClickId(fx, "security-profile-open");
        ExportPreferenceFrame(fx, columns, rows, "profile-" + locale, new Localization(locale).Resolve("securityPreferences.level_heading"));
        ClickId(fx, "security-back");
        ClickId(fx, "security-rules-open");
        ExportPreferenceFrame(fx, columns, rows, "rules-" + locale, new Localization(locale).Resolve("securityPreferences.rules_heading"));
        ClickId(fx, "security-back");
        ClickId(fx, "security-workspace-open");
        ExportPreferenceFrame(fx, columns, rows, "workspace-" + locale, new Localization(locale).Resolve("securityPreferences.trust_heading"));
        ClickId(fx, "security-back");
        ClickId(fx, "security-back");
        Assert.Equal("keep this task draft", fx.App.Composer!.Text.ToString());
        fx.Invoke(() => fx.App.ShowModePreferences());
        ExportPreferenceFrame(fx, columns, rows, "mode-" + locale, ExecutionControlsResources.Get(locale, "mode"));
        ClickId(fx, "security-back");
        fx.Invoke(() => fx.App.ShowReasoningPreferences());
        ExportPreferenceFrame(fx, columns, rows, "effort-" + locale, ExecutionControlsResources.Get(locale, "reasoning"));
        ClickId(fx, "security-back");
        fx.Invoke(() => fx.App.ShowReasoningVisibility());
        ExportPreferenceFrame(fx, columns, rows, "visibility-" + locale, ExecutionControlsResources.Get(locale, "visibility"));
    }, noColor: false);

    [Fact]
    public void Reasoning_declared_options_keep_native_ids_and_apply_the_exact_selected_effort() => RunTuiTest(fx =>
    {
        var data = Path.Combine(fx.Root, "declared-reasoning");
        var config = Path.Combine(data, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "providers.yaml"), "providers:\n  fixture: {family: OpenAIResponses, baseUrl: https://fixture.invalid/v1, billingMode: Local}\n");
        File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  declared:\n    provider: fixture\n    context: 8192\n    maxOutput: 4096\n    reasoning: {supported: true, effortLevels: [low, high, provider_special]}\n");
        var policies = ModelPolicyHost.Create(data);
        policies.Select(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), new("fixture", "declared"), "declared", true, default);
        fx.StartTui(policies: policies, initialColumns: 80, initialRows: 25);
        var before = fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json;
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "real model picker opens");
        fx.Invoke(() => Descendants(fx.App.Overlay!).OfType<ListView>().Single().SetFocus());
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "reasoning-preferences", "model choice prompts for effort");
        var choices = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        var rows = choices.Source!.ToList().Cast<string>().ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Contains("Alto (high)", rows);
        Assert.Contains("provider_special", rows);
        fx.Invoke(() => choices.SelectedItem = 3);
        ExportPreferenceFrame(fx, 80, 25, "effort-declared-es", ExecutionControlsResources.Get("es", "reasoning"));
        ClickId(fx, "reasoning-apply");
        fx.Wait(() => fx.App.Overlay?.Id == "reasoning-visibility", "model selection offers visibility after effort");
        Assert.Contains("\"kind\":\"high\"", fx.Server.Query("runReasoningPreference", TestContext.Current.CancellationToken)!.Json);
        Assert.Equal(before, fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json);
        ClickId(fx, "reasoning-hidden");
        ClickId(fx, "reasoning-visibility-apply");
        Assert.False(fx.App.ShowReportedReasoning);
        fx.Invoke(() => fx.App.ShowReasoningPreferences());
        Assert.Contains("Alto (high)", OverlayText(fx.App.Overlay!));
    }, noColor: false);

    [Fact]
    public void Reasoning_run_off_is_applied_to_run_without_changing_the_user_default() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var before = fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json;
        fx.Invoke(() => fx.App.ShowReasoningPreferences());
        var choices = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        ClickId(fx, "reasoning-scope-run");
        fx.Invoke(() => choices.SelectedItem = 1);
        ClickId(fx, "reasoning-apply");
        fx.Wait(() => fx.App.Overlay is null, "reasoning request accepted");
        Assert.Equal(before, fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json);
        var selected = fx.Server.Query("runReasoningPreference", TestContext.Current.CancellationToken)!.Json;
        Assert.Contains("\"hasSelection\":true", selected);
        Assert.Contains("\"request\":null", selected);
    });

    [Fact]
    public void Reasoning_visibility_is_a_draft_until_apply_and_does_not_change_effort() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var before = fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json;
        fx.Invoke(() => fx.App.ShowReasoningVisibility());
        ClickId(fx, "reasoning-hidden");
        Assert.True(fx.App.ShowReportedReasoning);
        ClickId(fx, "security-back");
        Assert.True(fx.App.ShowReportedReasoning);
        fx.Invoke(() => fx.App.ShowReasoningVisibility());
        ClickId(fx, "reasoning-hidden");
        ClickId(fx, "reasoning-visibility-apply");
        Assert.False(fx.App.ShowReportedReasoning);
        Assert.Equal(before, fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json);
    });

    [Fact]
    public void Unknown_model_effort_menu_does_not_invent_levels_and_cancel_preserves_preferences() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var before = fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json;
        fx.Invoke(() => fx.App.ShowReasoningPreferences());
        fx.Wait(() => fx.App.Overlay?.Id == "reasoning-preferences", "effort menu");
        var choices = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        Assert.Equal(2, choices.Source!.Count);
        Assert.Contains(ExecutionControlsResources.Get("es", "unknown"), OverlayText(fx.App.Overlay!));
        ClickId(fx, "security-back");
        Assert.Equal(before, fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json);
    });

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Execution_controls_have_explicit_localized_copy(string locale)
    {
        Assert.All(ExecutionControlsResources.Keys, key =>
        {
            var text = ExecutionControlsResources.Get(locale, key);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(key, text);
        });
    }
}
