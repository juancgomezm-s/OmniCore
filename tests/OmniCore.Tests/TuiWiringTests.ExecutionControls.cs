namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    private static void ConfirmSelectedModelPreferences(TuiFixture fx, string modelId)
    {
        fx.Wait(() => fx.App.Overlay?.Id != "omni-model-palette", "model selection completed");
        if (fx.App.Overlay?.Id == "model-effort-picker") ClickId(fx, "reasoning-default");
        fx.Wait(() => fx.App.Overlay is null && fx.App.Composer!.HasFocus, "model selection returns directly to the conversation");
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
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "model-effort-picker", "model choice offers only declared efforts");
        Assert.Equal(before, fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json);
        Assert.True(fx.App.Overlay!.Frame.Width <= 72);
        Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id is "reasoning-scope-run" or "reasoning-scope-default");
        var choices = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        var rows = choices.Source!.ToList().Cast<string>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Contains("Alto", rows);
        Assert.Contains("provider_special", rows);
        fx.Invoke(() => choices.SelectedItem = 1);
        ExportPreferenceFrame(fx, 80, 25, "effort-declared-es", ExecutionControlsResources.Get("es", "reasoning"));
        ClickId(fx, "reasoning-apply");
        fx.Wait(() => fx.App.Overlay is null, "applying effort returns to the conversation");
        Assert.Contains("\"kind\":\"high\"", fx.Server.Query("runReasoningPreference", TestContext.Current.CancellationToken)!.Json);
        Assert.Equal(before, fx.Server.Query("reasoningPreference", TestContext.Current.CancellationToken)?.Json);
        fx.Invoke(() => fx.App.ShowReasoningVisibility());
        ClickId(fx, "visibility-reasoning");
        ClickId(fx, "reasoning-visibility-apply");
        Assert.False(fx.App.ShowReportedReasoning);
        fx.Invoke(() => fx.App.ShowReasoningPreferences());
        Assert.Contains("Alto (high)", OverlayText(fx.App.Overlay!));
    }, noColor: false);

    [Fact]
    public void Reasoning_subscription_levels_can_be_selected_and_saved_using_the_keyboard() => RunTuiTest(fx =>
    {
        var ct = TestContext.Current.CancellationToken;
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "subscription-reasoning"));
        var account = new TestAccount { Current = new(true, "***1234", null, false),
            Catalog = [new("subscription-model", "Subscription model",
                ReasoningEffortLevels: ["low", "medium", "high", "xhigh", "max", "ultra"])] };
        fx.StartTui(policies, account, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "F3 opens the picker");
        fx.Wait(() => fx.Read(() => PickerItems(fx).Any(item => item.Contains("Subscription model"))), "the catalog is refreshed");
        ChoosePickerItem(fx, "Subscription model");
        ConfirmSelectedModelPreferences(fx, "subscription-model");
        fx.Invoke(() => fx.App.ShowReasoningPreferences());
        fx.Wait(() => fx.App.Overlay?.Id == "reasoning-preferences", "effort menu opened");
        var options = fx.Read(() => Descendants(fx.App.Overlay!).OfType<ListView>().Single());
        var rows = fx.Read(() => options.Source!.ToList().Cast<string>().ToArray());
        Assert.Equal(new[] { "Usar predeterminado", "Sin solicitud de esfuerzo", "Bajo (low)", "Medio (medium)",
            "Alto (high)", "Muy alto (xhigh)", "Máximo (max)", "Ultra (ultra)" }, rows);
        fx.InjectKey(new Key(KeyCode.End));
        fx.Wait(() => options.SelectedItem == 7, "End scrolls to Ultra");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.Read(() => fx.App.Overlay is { } overlay
            && Descendants(overlay).OfType<Button>().Any(b => b.Id == "reasoning-apply" && b.HasFocus)),
            "Enter on Ultra focuses Apply");
        var run = fx.Server.Query("runReasoningPreference", ct)?.Json;
        Assert.DoesNotContain("\"ultra\"", run ?? "");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is null, "second Enter applies the effort and returns to the conversation");
        Assert.Contains("\"kind\":\"ultra\"", fx.Server.Query("runReasoningPreference", ct)!.Json);
    });

    [Fact]
    public void Reasoning_Claude_manual_budget_uses_Enter_to_edit_then_confirm() => RunTuiTest(fx =>
    {
        var ct = TestContext.Current.CancellationToken;
        var data = Path.Combine(fx.Root, "claude-budget");
        var config = Path.Combine(data, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "providers.yaml"),
            "providers:\n  fixture: {family: AnthropicMessages, baseUrl: https://fixture.invalid}\n");
        File.WriteAllText(Path.Combine(config, "models.yaml"),
            "models:\n  claude-fixture:\n    provider: fixture\n    reasoning: {supported: true, effortLevels: [low, adaptive, budget]}\n");
        var policies = ModelPolicyHost.Create(data);
        policies.Select(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), new("fixture", "claude-fixture"),
            "claude-fixture", true, ct);
        fx.StartTui(policies: policies, initialColumns: 80, initialRows: 25);
        fx.Invoke(() => fx.App.ShowReasoningPreferences());
        var options = fx.Read(() => Descendants(fx.App.Overlay!).OfType<ListView>().Single());
        fx.InjectKey(new Key(KeyCode.End));
        fx.Wait(() => options.SelectedItem == 4, "End selects the manual budget");
        fx.InjectKey(new Key(KeyCode.Enter));
        var budget = fx.Read(() => Descendants(fx.App.Overlay!).OfType<TextField>().Single());
        fx.Wait(() => budget.HasFocus, "Enter focuses the budget field before Apply");
        fx.Invoke(() => budget.Text = "2048");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.Read(() => fx.App.Overlay is { } overlay
            && Descendants(overlay).OfType<Button>().Any(button => button.Id == "reasoning-apply" && button.HasFocus)),
            "Enter advances from budget to Apply");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is null, "Enter on Apply saves the budget");
        Assert.Contains("\"budgetTokens\":2048", fx.Server.Query("runReasoningPreference", ct)!.Json);
    });

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
    public void F7_collapses_reasoning_and_tools_independently_without_changing_the_session() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 80, initialRows: 25);
        var cancellation = TestContext.Current.CancellationToken;
        var before = fx.Server.Query("reasoningPreference", cancellation)?.Json;
        var session = fx.Server.LastSessionId()!;
        var store = fx.Server.AcquireStore();
        var sequenceBefore = store.CurrentSequence(session);
        string Transcript() => fx.Read(() => fx.App.Conversation!.Text);
        void OpenVisibility() => KeyWithEffect(fx, KeyCode.F7, () => fx.App.Overlay?.Id == "reasoning-visibility", "F7 opens visibility");
        bool Focused(string id) => fx.Read(() => Descendants(fx.App.Overlay!).Single(view => view.Id == id).HasFocus);
        void Apply()
        {
            ClickId(fx, "reasoning-visibility-apply");
            fx.Wait(() => fx.App.Overlay is null, "apply closes visibility");
        }
        OpenVisibility();
        fx.InjectKey(new Key(KeyCode.Enter));
        Assert.True(fx.App.ShowReportedReasoning);
        KeyWithEffect(fx, KeyCode.CursorDown, () => Focused("visibility-tools"), "down moves between switches");
        fx.InjectKey(new Key(KeyCode.Enter));
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Escape discards both changes");
        Assert.True(fx.App.ShowReportedReasoning);
        Assert.Equal(sequenceBefore, store.CurrentSequence(session));

        OpenVisibility();
        ClickId(fx, "visibility-reasoning");
        ClickId(fx, "visibility-tools");
        Apply();
        Assert.Equal(sequenceBefore, store.CurrentSequence(session));

        var run = fx.Server.LastRunId()!;
        var root = fx.Decoded<RunCreated>().Last().RootTask;
        var lane = fx.Decoded<LaneCreated>().Single(created => created.TaskId == root).LaneId;
        var stream = new EventStream(store, fx.Server.AcquireCodecs(), session);
        var turn = TurnId.New();
        using (ExecutionScope.Begin(new(run, root, lane)))
        {
            var reasoning = fx.Artifacts.PutText("""
                {"visibleContent":{"version":1,"blocks":[{"kind":"reasoning","visibility":"Full","text":"razonamiento de prueba\nsegunda línea"}]}}
                """, "application/json", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
            stream.Append(new ModelStepCompleted(turn, 0, new(1, 2, 0, 0, 0), StopReason.ToolUse, reasoning, "2026-11-09", null));
            stream.Append(new ToolCallRequested(ToolCallId.New(), "fixture", "fixture.tool", "{}"));
            var answer = fx.Artifacts.PutText("respuesta visible de prueba", "text/plain", ArtifactKind.ModelResponse, Sensitivity.Normal);
            stream.Append(new AssistantMessageRecorded(run, lane, turn, answer));
        }
        fx.Invoke(() => fx.App.PollOnce());
        fx.Wait(() => Transcript().Contains("respuesta visible de prueba"), "new transcript blocks arrive while hidden");
        Assert.Single(Transcript().Split('\n'), line => line.Contains("Razonamiento oculto"));
        Assert.Single(Transcript().Split('\n'), line => line.Contains("Herramienta oculta"));
        Assert.DoesNotContain("razonamiento de prueba", Transcript());
        Assert.DoesNotContain("fixture.tool", Transcript());
        var sequence = store.CurrentSequence(session);

        OpenConversationSearch(fx);
        SearchConversation(fx, "razonamiento de prueba");
        Assert.Empty(fx.Read(() => Descendants(fx.App.Overlay!).OfType<ListView>().Single().Source!.ToList()));
        SearchConversation(fx, "respuesta visible de prueba");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is null && fx.App.Conversation!.HasFocus, "search jumps past both markers");
        fx.Read(() => Assert.Contains("respuesta visible de prueba",
            string.Concat(fx.App.Conversation!.GetAllLines()[fx.App.Conversation.CurrentRow + 1].Select(cell => cell.Grapheme))));

        OpenVisibility();
        ClickId(fx, "visibility-tools");
        Apply();
        Assert.Contains("fixture.tool", Transcript());
        Assert.Contains("Razonamiento oculto", Transcript());
        OpenVisibility();
        ClickId(fx, "visibility-reasoning");
        Apply();
        Assert.Contains("razonamiento de prueba", Transcript());
        Assert.Equal(sequence, store.CurrentSequence(session));
        Assert.Equal(before, fx.Server.Query("reasoningPreference", cancellation)?.Json);
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
