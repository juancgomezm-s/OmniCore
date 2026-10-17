namespace OmniCore.Cli;

using System.Collections.ObjectModel;
using OmniCore.Host;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    internal void ShowPolicyPreferences(string queryName)
    {
        if (_window is null || _overlay is not null || queryName != "models_execution") return;
        var host = _policyPreferencesOverride ?? PolicyPreferencesHost.Create(Environment.CurrentDirectory);
        PolicyPreferencesSnapshot snapshot;
        try { snapshot = host.Read(); }
        catch (Exception) { ShowPreferenceText(PolicyPref("title"), PolicyPref("unavailable")); return; }
        var frame = OverlayFrame(PolicyPref("title"), _window.Frame.Height - 3);
        frame.Id = "policy-preferences"; frame.Width = Dim.Percent(92); frame.Height = Dim.Fill(2);
        var panel = new View { Id = "policy-preferences-form", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = Dim.Fill(9), ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var note = new Label { Id = "policy-preferences-notice", X = 2, Y = Pos.AnchorEnd(6),
            Width = Dim.Fill(4), Height = 1, Text = snapshot.Available ? PolicyPref("scope_note") : PolicyPref("unavailable") };
        var fieldHelp = new Label { Id = "omni-detail", X = 2, Y = Pos.AnchorEnd(8), Width = Dim.Fill(4), Height = 2 };
        void Explain(View field, string key)
        {
            field.HasFocusChanged += (_, _) => { if (field.HasFocus) fieldHelp.Text = PolicyPref("help." + key); };
        }
        const int fieldX = MenuIndent.Level1 + 29;
        var y = 0;
        panel.Add(MenuIndent.Heading(PolicyPref("section_defaults"), y++));
        panel.Add(new Label { X = MenuIndent.Level1, Y = y++, Width = Dim.Fill(4), Height = 1,
            Text = PolicyPref("effective_model") + ": " + (snapshot.EffectiveDefaultModel ?? PolicyPref("not_set"))
                + " · " + PolicyPref("source") + ": " + (snapshot.DefaultModelSource is { } modelSource
                    ? UiCompletionResources.EnumLabel("source", modelSource, _locale) : PolicyPref("default")) });
        var defaultLabel = new Label { X = MenuIndent.Level1, Y = y, Width = 27, Height = 1, Text = PolicyPref("default_model") };
        var model = new TextField { Id = "policy-default-model", X = fieldX, Y = y++, Width = Dim.Fill(4), Height = 1,
            Text = snapshot.UserDefaultModel ?? "" };
        panel.Add(defaultLabel, model);
        var sessionLabel = new Label { X = MenuIndent.Level1, Y = y, Width = 27, Height = 1, Text = PolicyPref("session_cap") };
        var session = new TextField { Id = "policy-session-cap", X = fieldX, Y = y++, Width = Dim.Percent(30), Height = 1,
            Text = snapshot.SessionCapUsd?.ToString(PreferenceInput.Culture(_locale)) ?? "" };
        var dailyLabel = new Label { X = MenuIndent.Level1, Y = y, Width = 27, Height = 1, Text = PolicyPref("daily_cap") };
        var daily = new TextField { Id = "policy-daily-cap", X = fieldX, Y = y++, Width = Dim.Percent(30), Height = 1,
            Text = snapshot.DailyCapUsd?.ToString(PreferenceInput.Culture(_locale)) ?? "" };
        panel.Add(sessionLabel, session, dailyLabel, daily);
        Explain(model, "default_model"); Explain(session, "session_cap"); Explain(daily, "daily_cap");
        y++;
        panel.Add(MenuIndent.Heading(PolicyPref("section_routes"), y++));
        var route = snapshot.Routing;
        TextField Route(string id, string label, IReadOnlyList<string> aliases, string helpKey)
        {
            panel.Add(new Label { X = MenuIndent.Level1, Y = y, Width = 27, Height = 1, Text = label });
            var field = new TextField { Id = id, X = fieldX, Y = y++, Width = Dim.Fill(4), Height = 1,
                Text = string.Join(", ", aliases) };
            Explain(field, helpKey);
            panel.Add(field); return field;
        }
        var exploration = Route("policy-route-exploration", PolicyPref("route_exploration"), route.Exploration, "route_exploration");
        var implementation = Route("policy-route-implementation", PolicyPref("route_implementation"), route.Implementation, "route_implementation");
        var preferLocal = Route("policy-route-prefer-local", PolicyPref("prefer_local"),
            route.PreferLocal is { } local ? [local ? "true" : "false"] : [], "prefer_local");
        var meta = Route("policy-route-meta", PolicyPref("route_meta"), route.Meta, "route_meta");
        var reasoning = Route("policy-route-reasoning", PolicyPref("route_reasoning"), route.Reasoning, "route_reasoning");
        var architecture = Route("policy-route-architecture", PolicyPref("route_architecture"), route.Architecture, "route_architecture");
        y++;
        panel.Add(MenuIndent.Heading(PolicyPref("section_escalation"), y++));
        var escalationMode = Route("policy-escalation-mode", PolicyPref("escalation_mode"), [route.EscalationMode], "escalation_mode");
        var escalationChain = Route("policy-escalation-chain", PolicyPref("escalation_chain"), route.EscalationChain, "escalation_chain");
        var save = new Button { Id = "policy-preferences-save", X = 2, Y = Pos.AnchorEnd(4), Text = PolicyPref("save") };
        var saveRoutes = new Button { Id = "policy-preferences-save-routes", X = Pos.Percent(36), Y = Pos.AnchorEnd(4), Text = PolicyPref("save_routes") };
        save.Enabled = saveRoutes.Enabled = snapshot.Available;
        save.Accepted += (_, _) =>
        {
            if (!PreferenceInput.TryMoney(session.Text.ToString(), _locale, out var sessionUsd)
                || !PreferenceInput.TryMoney(daily.Text.ToString(), _locale, out var dailyUsd))
            { note.Text = PolicyPref("invalid_values"); return; }
            try
            {
                var next = host.SaveUserSettings(snapshot.Revision,
                    string.IsNullOrWhiteSpace(model.Text.ToString()) ? null : model.Text.ToString(), sessionUsd, dailyUsd);
                if (!next.Available) { note.Text = PolicyPref("saved_but_invalid"); return; }
                snapshot = next; note.Text = PolicyPref("saved");
            }
            catch (InvalidOperationException exception) when (exception.Message == "policyPreferences.error.stale")
            { note.Text = PolicyPref("stale"); }
            catch (ArgumentException) { note.Text = PolicyPref("invalid_values"); }
            catch (Exception) { note.Text = PolicyPref("save_failed"); }
        };
        saveRoutes.Accepted += (_, _) =>
        {
            if (!TryBoolean(preferLocal.Text.ToString(), out var local)
                || string.IsNullOrWhiteSpace(escalationMode.Text.ToString()))
            { note.Text = PolicyPref("invalid_values"); return; }
            try
            {
                var updated = host.SaveRouting(snapshot.Revision, new ModelRoutingPreferences(
                    ParseAliases(meta.Text.ToString()), ParseAliases(exploration.Text.ToString()),
                    ParseAliases(implementation.Text.ToString()), ParseAliases(reasoning.Text.ToString()),
                    ParseAliases(architecture.Text.ToString()), local, escalationMode.Text.ToString().Trim().ToLowerInvariant(),
                    ParseAliases(escalationChain.Text.ToString())));
                note.Text = updated.Available ? PolicyPref("saved") : PolicyPref("saved_but_invalid");
                snapshot = updated;
            }
            catch (InvalidOperationException exception) when (exception.Message == "policyPreferences.error.stale")
            { note.Text = PolicyPref("stale"); }
            catch (ArgumentException) { note.Text = PolicyPref("invalid_values"); }
            catch (Exception) { note.Text = PolicyPref("save_failed"); }
        };
        var models = new Button { Id = "policy-preferences-model-policies", X = 2, Y = Pos.AnchorEnd(2),
            Text = PolicyPref("model_policies") };
        models.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(refreshCatalog: false); };
        var back = new Button { Id = "policy-preferences-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = PolicyPref("back") };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("models_execution", categoryPage: true); };
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; CloseOverlay(); ShowPreferences("models_execution", categoryPage: true); } };
        panel.Add(new Label { Id = "policy-preferences-help", X = MenuIndent.Level1, Y = y + 1,
            Width = Dim.Fill(4), Height = 2, Text = PolicyPref("route_help") });
        var resetReasoning = new Button { Id = "policy-reasoning-reset", X = MenuIndent.Level1, Y = y + 4,
            Text = PolicyPref("reasoning_reset") };
        resetReasoning.Accepted += (_, _) => HandleReasoningCommand("default reset");
        panel.Add(resetReasoning, new Label { Id = "policy-reasoning-note", X = MenuIndent.Level1, Y = y + 6,
            Width = Dim.Fill(4), Height = 2, Text = PolicyPref("reasoning_note") });
        ConfigurePreferenceScroll(panel, y + 9);
        fieldHelp.Text = PolicyPref("help.default_model");
        frame.Add(panel, fieldHelp, note, save, saveRoutes, models, back);
        if (!snapshot.Available) { model.Enabled = session.Enabled = daily.Enabled = false; }
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); model.SetFocus();
    }

    private string PolicyPref(string key) => _localization.Resolve("policyPreferences." + key);
    private static IReadOnlyList<string> ParseAliases(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static bool TryBoolean(string text, out bool? value)
    {
        if (string.IsNullOrWhiteSpace(text)) { value = null; return true; }
        if (bool.TryParse(text, out var parsed)) { value = parsed; return true; }
        value = null; return false;
    }
}
