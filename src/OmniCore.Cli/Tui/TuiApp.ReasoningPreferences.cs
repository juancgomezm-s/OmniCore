namespace OmniCore.Cli;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    private readonly ReasoningDisplaySettingsStore _reasoningDisplayStore;
    internal bool ShowReportedReasoning { get; private set; } = true;

    private string ReadControlQuery(string name)
    {
        try { return _client.Query(name, CancellationToken.None)?.Json ?? "null"; }
        catch (Exception) { return "null"; }
    }

    private static string? ControlJsonString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal void ShowModePreferences()
    {
        if (_window is null || _overlay is not null) return;
        var preference = ReadControlQuery("modePreference");
        var authority = ReadControlQuery("modeAuthority");
        long defaultRevision = 0;
        var hasDefault = preference != "null" && TryReadLong(preference, "revision", out defaultRevision);
        var runId = ControlJsonString(authority, "runId");
        var runMode = ControlJsonString(authority, "mode");
        var defaultMode = ControlJsonString(preference, "mode");
        var forRun = runId is not null;
        var modes = new[] { "plan", "act", "orq" };
        var frame = ControlFrame(ControlText("mode"), "mode-preferences");
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 3, Text = ControlText("mode_scope") });
        var current = new Label { Id = "mode-current", X = 2, Y = 6, Width = Dim.Fill(2), Height = 1 };
        var options = new ListView { Id = "mode-options", X = 2, Y = 8, Width = Dim.Fill(2), Height = 3, ShowMarks = false };
        options.SetSource(new ObservableCollection<string>(modes.Select(mode => ControlText("mode_name." + mode))));
        var detail = new Label { Id = "mode-detail", X = 2, Y = 12, Width = Dim.Fill(2), Height = 4 };
        var active = new Button { Id = "mode-scope-run", X = 2, Y = 4, Enabled = runId is not null };
        var future = new Button { Id = "mode-scope-default", X = Pos.Right(active) + 1, Y = 4 };
        var apply = new Button { Id = "mode-apply", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(2), Text = ControlText("apply") };
        void Sync()
        {
            active.Text = (forRun ? "● " : "○ ") + ControlText("active_run");
            future.Text = (!forRun ? "● " : "○ ") + ControlText("future_runs");
            var mode = forRun ? runMode : defaultMode;
            current.Text = ControlText("current") + ": " + (mode is null ? "—" : ControlText("mode_name." + mode));
            options.SelectedItem = Math.Max(0, Array.IndexOf(modes, mode));
            detail.Text = ControlText("mode." + modes[options.SelectedItem ?? 0]);
            apply.Enabled = _client is ITrustedUserActionClient && (forRun ? runId is not null : hasDefault);
        }
        active.Accepted += (_, _) => { forRun = true; Sync(); };
        future.Accepted += (_, _) => { forRun = false; Sync(); };
        options.ValueChanged += (_, _) => detail.Text = ControlText("mode." + modes[options.SelectedItem ?? 0]);
        apply.Accepted += (_, _) =>
        {
            if (forRun && ControlJsonString(ReadControlQuery("modeAuthority"), "runId") != runId)
            { detail.Text = ControlText("reasoning_changed"); return; }
            var chosen = modes[options.SelectedItem ?? 0];
            var fields = JsonObj.Field("mode", chosen) + (forRun ? ",\"effort\":\"standard\""
                : ",\"expectedRevision\":" + defaultRevision.ToString(CultureInfo.InvariantCulture));
            CloseOverlay(); SendTrustedModeCommand(forRun ? "run.mode.select" : "mode.default.set", fields); PollEvents();
        };
        Sync(); frame.Add(active, future, current, options, detail, apply); ControlBack(frame); OpenControlFrame(frame, options);
    }

    internal void ShowReasoningVisibility()
    {
        if (_window is null || _overlay is not null) return;
        var frame = ControlFrame(ControlText("visibility"), "reasoning-visibility");
        var draft = ShowReportedReasoning;
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 5, Text = ControlText("visibility_help") },
            new Label { X = 2, Y = 13, Width = Dim.Fill(2), Height = 2, Text = ControlText("visibility_scope") });
        var show = new Button { Id = "reasoning-visible", X = 2, Y = 7, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        var hide = new Button { Id = "reasoning-hidden", X = 2, Y = 9, Width = Dim.Fill(2), TextAlignment = Alignment.Start };
        void Sync() { show.Text = (draft ? "● " : "○ ") + ControlText("visible"); hide.Text = (!draft ? "● " : "○ ") + ControlText("hidden"); }
        show.Accepted += (_, _) => { draft = true; Sync(); };
        hide.Accepted += (_, _) => { draft = false; Sync(); };
        var apply = new Button { Id = "reasoning-visibility-apply", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(2), Text = ControlText("apply") };
        apply.Accepted += (_, _) => SetReasoningVisibility(draft);
        Sync(); frame.Add(show, hide, apply); ControlBack(frame); OpenControlFrame(frame, show);
    }

    internal void SetReasoningVisibility(bool show)
    {
        if (!_reasoningDisplayStore.Save(show))
        {
            CloseOverlay(); ShowMessage(_localization.Resolve("shell.could_not_save_unchanged")); return;
        }
        ShowReportedReasoning = show;
        _renderedConversation = null;
        CloseOverlay(); RenderState();
    }

    private static (long? Revision, string Kind, int? Budget) ReadReasoningSelection(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("revision", out var revision) || !revision.TryGetInt64(out var number))
                return (null, "reset", null);
            var selected = root.TryGetProperty("hasSelection", out var has) && has.ValueKind == JsonValueKind.True;
            if (!selected) return (number, "reset", null);
            if (!root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
                return (number, "off", null);
            var kind = request.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
            int? budget = request.TryGetProperty("budgetTokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number
                && tokens.TryGetInt32(out var parsed) ? parsed : null;
            return (number, kind ?? "off", budget);
        }
        catch (JsonException) { return (null, "reset", null); }
    }

    internal void ShowReasoningPreferences(bool forModelSelection = false)
    {
        if (_window is null || _overlay is not null) return;
        var modelId = _selectedModel ?? _observedModel;
        ModelReasoningCapabilityDto? capability = null;
        try { if (modelId is not null) capability = _policies.ReadReasoningCapability(modelId); }
        catch (Exception) { /* Unknown declarations must never turn into inferred effort options. */ }
        var user = ReadReasoningSelection(ReadControlQuery("reasoningPreference"));
        var runJson = ReadControlQuery("runReasoningPreference");
        var run = ReadReasoningSelection(runJson);
        var runId = ControlJsonString(ReadControlQuery("modeAuthority"), "runId");
        // A Run with no reasoning event has revision zero, not an unavailable scope.
        if (runJson == "null" && runId is not null) run = (0, "reset", null);
        var sessionId = ReadSecurityPolicyPresentation().SessionId;
        var forRun = run.Revision is not null && runId is not null;
        var kinds = new List<string> { "reset", "off" };
        if (capability?.Supported == true && capability.EffortLevels is not null)
            kinds.AddRange(capability.EffortLevels.Where(kind => kind is not ("reset" or "off")));
        var frame = ControlFrame(ControlText("reasoning"), "reasoning-preferences");
        var heading = new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 3,
            Text = (forModelSelection ? ControlText("model_selected") + "\n" : "") + (modelId ?? "—") };
        var active = new Button { Id = "reasoning-scope-run", X = 2, Y = 4, Enabled = run.Revision is not null && runId is not null };
        var future = new Button { Id = "reasoning-scope-default", X = Pos.Right(active) + 1, Y = 4 };
        var status = new Label { Id = "reasoning-current", X = 2, Y = 6, Width = Dim.Fill(2), Height = 1 };
        var options = new ListView { Id = "reasoning-options", X = 2, Y = 8, Width = Dim.Fill(2), Height = 4, ShowMarks = false };
        string KindLabel(string kind) => kind switch
        {
            "reset" or "off" => ControlText(kind),
            "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "budget" or "adaptive"
                => ControlText("effort." + kind) + " (" + kind + ")",
            _ => kind,
        };
        options.SetSource(new ObservableCollection<string>(kinds.Select(KindLabel)));
        var budgetLabel = new Label { X = 2, Y = 12, Width = 40, Height = 1, Text = ControlText("tokens") };
        var budget = new TextField { Id = "reasoning-budget", X = 44, Y = 12, Width = Dim.Fill(2), Height = 1 };
        var detail = new Label { Id = "reasoning-detail", X = 2, Y = 14, Width = Dim.Fill(2), Height = 3,
            Text = capability?.Supported == false ? ControlText("unsupported")
                : capability?.EffortLevels is not { Count: > 0 } ? ControlText("unknown") : ControlText("reasoning_intro") };
        var apply = new Button { Id = "reasoning-apply", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(2), Text = ControlText("apply") };
        void BudgetState() { var enabled = kinds[options.SelectedItem ?? 0] == "budget"; budget.Visible = budgetLabel.Visible = enabled; }
        void Sync()
        {
            active.Text = (forRun ? "● " : "○ ") + ControlText("active_run");
            future.Text = (!forRun ? "● " : "○ ") + ControlText("future_runs");
            var selection = forRun ? run : user;
            status.Text = ControlText("current") + ": " + KindLabel(selection.Kind) + (selection.Budget is { } tokens ? " · " + tokens : "");
            options.SelectedItem = Math.Max(0, kinds.IndexOf(selection.Kind));
            budget.Text = selection.Budget?.ToString(CultureInfo.InvariantCulture) ?? "";
            apply.Enabled = _client is ITrustedUserActionClient && selection.Revision is not null;
            BudgetState();
        }
        active.Accepted += (_, _) => { forRun = true; Sync(); detail.Text = ControlText("reasoning_scope"); };
        future.Accepted += (_, _) => { forRun = false; Sync(); detail.Text = ControlText("reasoning_scope"); };
        options.ValueChanged += (_, _) =>
        {
            BudgetState();
            if (capability?.Supported == true && capability.EffortLevels is { Count: > 0 })
                detail.Text = ControlText("effort_hint");
        };
        apply.Accepted += (_, _) =>
        {
            var selected = kinds[options.SelectedItem ?? 0];
            if (modelId != (_selectedModel ?? _observedModel) || sessionId != ReadSecurityPolicyPresentation().SessionId
                || forRun && runId != ControlJsonString(ReadControlQuery("modeAuthority"), "runId"))
            { detail.Text = ControlText("reasoning_changed"); return; }
            if (selected is not ("reset" or "off"))
            {
                ModelReasoningCapabilityDto? latest = null;
                try { if (modelId is not null) latest = _policies.ReadReasoningCapability(modelId); }
                catch (Exception) { }
                if (latest?.RouteId != capability?.RouteId || latest?.Supported != true || latest.EffortLevels?.Contains(selected, StringComparer.Ordinal) != true)
                { detail.Text = ControlText("reasoning_changed"); return; }
            }
            int? tokenBudget = null;
            if (selected == "budget")
            {
                if (!int.TryParse(budget.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1024)
                { detail.Text = _localization.Resolve("shell.invalid_reasoning_kind_or_budget"); return; }
                tokenBudget = number;
            }
            var revision = (forRun ? run : user).Revision;
            if (revision is null || _client is not ITrustedUserActionClient trusted) return;
            var command = selected == "reset" ? (forRun ? "run.reasoning.revoke" : "reasoning.default.revoke")
                : forRun ? "run.reasoning.select" : "reasoning.default.set";
            var payload = "{" + JsonObj.Field("cmd", command) + ",\"expectedRevision\":" + revision.Value.ToString(CultureInfo.InvariantCulture)
                + (selected == "reset" ? "" : "," + JsonObj.Field("kind", selected))
                + (tokenBudget is { } tokens ? ",\"budgetTokens\":" + tokens.ToString(CultureInfo.InvariantCulture) : "") + "}";
            try
            {
                var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
                if (ack.Status != "ok") { detail.Text = _localization.ResolveWire(ack.Error ?? "shell.reasoning_change_rejected"); return; }
                if (ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Deferred)
                { detail.Text = _localization.Resolve("shell.the_active_turn_still_has_a_model_step_retry_after_it_completes"); return; }
                CloseOverlay(); PollEvents();
                if (forModelSelection) ShowReasoningVisibility();
            }
            catch (Exception) { detail.Text = _localization.Resolve("shell.reasoning_change_rejected"); }
        };
        Sync(); frame.Add(heading, active, future, status, options, budgetLabel, budget, detail, apply); ControlBack(frame);
        OpenControlFrame(frame, options);
    }
}
