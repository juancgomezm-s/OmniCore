using System.Text;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

#pragma warning disable CS0618 // Pinned Terminal.Gui TextView, consistent with the existing inspection screens.
public sealed partial class TuiApp
{
    private ConfiguredKeyBindings _keyBindings = null!;
    private KeyBindingSession _keyBindingSession = null!;
    private string? _keyBindingsConfigDirectory;
    private ExtensionKeyBindingSnapshot _extensionKeyBindingSnapshot = new([], []);
    private (string? Session, string? Run, View? Focus) _keyBindingScope;
    private CancellationTokenSource? _keyBindingTurn;
    private bool _keyBindingInterruptPending;
    private string? _keyboardRunState;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<View, object> _keyboardFocusSubscriptions = new();
    internal ConfiguredKeyBindings KeyboardConfiguration => _keyBindings;

    private void InitializeKeyBindings(string? configDirectory)
    {
        _keyBindingsConfigDirectory = configDirectory;
        ReloadKeyBindings();
    }

    private void ReloadKeyBindings()
    {
        var extensionDefinitions = new List<KeyBindingDefinition>();
        var extensionDiagnostics = new List<KeyBindingSourceDiagnostic>();
        ExtensionKeyBindingSnapshot? extensionSnapshot = null;
        try
        {
            var query = _client.Query("keybindings.extensions", CancellationToken.None)?.Json;
            if (query is not null) extensionSnapshot = ExtensionKeyBindingProtocol.Decode(query);
            _extensionKeyBindingSnapshot = extensionSnapshot ?? new([], []);
            if (extensionSnapshot is not null)
            {
                foreach (var binding in extensionSnapshot.Bindings)
                {
                    if (KeyChord.TryParseSequence(binding.Keys, out var sequence) && sequence.Length > 0
                        && (sequence[0].Modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0
                        && !(sequence[0].Key.StartsWith('F') && int.TryParse(sequence[0].Key.AsSpan(1), out var functionKey)
                            && functionKey is >= 1 and <= 24))
                    {
                        extensionDiagnostics.Add(new("extension_reserved_prefix", binding.Source, 0, 0, binding.Keys));
                        continue;
                    }
                    extensionDefinitions.Add(new(binding.Keys,
                        new(KeyBindingTargetKind.Command, binding.Command, binding.Arguments),
                        "input && !overlayOpen && !awaitingPermission && (" + binding.When + ")", 0,
                        KeyBindingSource.Extension, binding.Source));
                }
                extensionDiagnostics.AddRange(extensionSnapshot.Diagnostics.Select(issue => new KeyBindingSourceDiagnostic(
                    "extension_" + issue.Code, issue.Source, 0, 0, issue.ExtensionId)));
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            extensionDiagnostics.Add(new("extension_catalog_unavailable", "Host", 0, 0, "keybindings.extensions"));
        }
        var initial = KeyBindingConfiguration.Load(CancellationToken.None,
            configDirectoryOverride: _keyBindingsConfigDirectory, bindingSupported: TerminalKeyChord.Supports);
        _keyBindings = initial with { Diagnostics = initial.Diagnostics.Concat(extensionDiagnostics).ToArray() };
        _hostKeyBindingCommands = new Dictionary<string, CommandPaletteEntry>(StringComparer.Ordinal);
        if (_keyBindings.Configuration.Bindings.Any(binding => binding.Command?.Contains(':') == true)
            || extensionDefinitions.Count > 0)
        {
            try
            {
                _hostKeyBindingCommands = ReadHostKeyBindingCommands(CancellationToken.None);
                var catalog = KeyBindingTargetCatalog.Core.WithTargets(_hostKeyBindingCommands.Values.Select(entry =>
                    new KeyValuePair<string, string>(entry.Target.CatalogKey,
                        entry.Source.StartsWith("BuiltIn:", StringComparison.Ordinal) ? "Core" : entry.Source)));
                var ownedDefinitions = extensionDefinitions.Where(binding =>
                {
                    if (_hostKeyBindingCommands.TryGetValue(binding.Target.Id, out var entry)
                        && entry.Source == binding.SourceId) return true;
                    extensionDiagnostics.Add(new("extension_command_not_owned", binding.SourceId, 0, 0, binding.Target.Id));
                    return false;
                }).ToArray();
                var loaded = KeyBindingConfiguration.Load(CancellationToken.None, configDirectoryOverride: _keyBindingsConfigDirectory,
                    extensions: ownedDefinitions, catalog: catalog, bindingSupported: TerminalKeyChord.Supports);
                _keyBindings = loaded with { Diagnostics = loaded.Diagnostics.Concat(extensionDiagnostics).ToArray() };
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _keyBindings = _keyBindings with { Diagnostics = _keyBindings.Diagnostics.Append(new KeyBindingSourceDiagnostic(
                    "host_catalog_unavailable", _keyBindings.Configuration.Path, 0, 0, "commands")).ToArray() };
            }
        }
        _keyBindingSession = new(_keyBindings.Service, TimeSpan.FromSeconds(1));
        if (_help is not null) _help.Text = HelpText();
    }

    private KeyBindingContext KeyboardContext()
    {
        var snapshot = _sidebarProjection.Snapshot;
        var focus = _window is null ? null : FindFocusedPreferenceView(_window);
        if (focus is not null && _keyboardFocusSubscriptions.TryAdd(focus, new object()))
            focus.HasFocusChanged += (_, _) => _keyBindingSession.Reset();
        var scope = (Session: _cursorSession, Run: snapshot?.RunId, Focus: focus);
        if (_keyBindingScope != scope)
        {
            _keyBindingSession.Reset();
            if (_keyBindingScope.Session != scope.Session || _keyBindingScope.Run != scope.Run)
                _keyBindingInterruptPending = false;
            _keyBindingScope = scope;
        }
        if (_keyBindingTurn != _turnCancellation)
        {
            _keyBindingTurn = _turnCancellation;
            if (_localActivitySession == _cursorSession) _keyBindingInterruptPending = false;
        }
        if (_keyboardRunState == "AwaitingInput" && snapshot?.RunState == "Running") _keyBindingInterruptPending = false;
        _keyboardRunState = snapshot?.RunState;
        return new(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["input"] = _overlay is null && _activeInteraction is null && _composer?.HasFocus == true,
            ["runActive"] = snapshot is { RunId: not null } && snapshot.SessionId == _cursorSession
                && snapshot.RunState is "Created" or "Running" or "AwaitingInput" or "Validating",
            ["interruptPending"] = _keyBindingInterruptPending || (_turnBusy && _localActivitySession == _cursorSession
                ? _turnCancellation?.IsCancellationRequested == true : snapshot?.RunState == "AwaitingInput"),
            ["awaitingPermission"] = _activeInteraction is not null,
            ["paletteOpen"] = _overlay?.Id == "global-command-palette",
            ["overlayOpen"] = _overlay is not null,
            ["view.plan"] = _overlay?.Id == "keybindings-view-plan" || _sidebarOpen && _sidebarComposition?.SelectedId == "core.plan",
            ["view.tasks"] = _overlay?.Id == "keybindings-view-tasks",
            ["view.context"] = _overlay?.Id == SessionPreferencesOverlayId && _sessionPreferencesQuery == "context"
                || _sidebarOpen && _sidebarComposition?.SelectedId == "core.context"
        });
    }

    private void MarkKeyboardView(string actionId)
    {
        if (_overlay is not null && actionId is ClientActionIds.ViewPlan or ClientActionIds.ViewTasks)
            _overlay.Id = actionId == ClientActionIds.ViewPlan ? "keybindings-view-plan" : "keybindings-view-tasks";
    }

    private bool HandleKeyBinding(Key key)
    {
        // Always preserve Esc's default/deny semantics, including while a chord is pending.
        if (key.KeyCode == KeyCode.Esc) { _keyBindingSession.Reset(); return false; }
        if (!TerminalKeyChord.TryConvert(key.KeyCode, out var chord)) { _keyBindingSession.Reset(); return false; }
        var resolution = _keyBindingSession.Push(chord, KeyboardContext(), DateTimeOffset.UtcNow);
        if (resolution.Status == KeyBindingResolutionStatus.None) return false;
        key.Handled = true;
        if (resolution.Status == KeyBindingResolutionStatus.Conflict)
        {
            if (_activeInteraction is null) ShowKeyBindings();
            return true;
        }
        if (resolution.Status == KeyBindingResolutionStatus.Pending) return true;
        var dispatcher = new KeyBindingDispatcher(new TuiActionHandler(this), hostCommands: new TuiHostCommandHandler(this, resolution));
        dispatcher.ExecuteAsync(resolution, new(_client, _localization, CliApp.PermissionsListing), CancellationToken.None)
            .GetAwaiter().GetResult();
        return true;
    }

    private sealed class TuiActionHandler(TuiApp app) : IClientActionHandler
    {
        public ValueTask<ClientActionResult> ExecuteAsync(ClientActionInvocation action, ClientContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A pending approval is only replaced by its server-provided response or an explicit run control.
            if (app._activeInteraction is { } interaction)
            {
                if (action.ActionId == "overlay.close") app.RespondDefault(interaction);
                else if (action.ActionId is ClientActionIds.RunInterrupt or ClientActionIds.RunCancel) app.ExecuteKeyboardAction(action);
                return ValueTask.FromResult(new ClientActionResult(""));
            }
            switch (action.ActionId)
            {
                case "palette.open": app.ShowCommandPalette(); break;
                case "session.search": app.ShowConversationSearch(); break;
                case "agent.inspect": app.ShowAgentInspector(); break;
                case "keybindings.open": app.ShowKeyBindings(); break;
                case "updates.open": app.ShowReleaseUpdates(); break;
                case "sidebar.toggle": app.ToggleSidebar(); break;
                case "model.select": if (app._overlay is null) app.ShowModelPicker(); break;
                case "preferences.open":
                    if (app._overlay?.Id == "theme-preferences") app.ReturnFromPreferencesSubpage();
                    else app.ShowPreferences();
                    break;
                case "overlay.close": app.CloseOverlay(); break;
                default: app.ExecuteKeyboardAction(action); break;
            }
            return ValueTask.FromResult(new ClientActionResult(""));
        }
    }

    private void ExecuteKeyboardAction(ClientActionInvocation action)
    {
        if (action.ActionId == ClientActionIds.RunInterrupt) _keyBindingInterruptPending = true;
        if (action.ActionId == ClientActionIds.RunCancel)
            foreach (var task in _delegationTasks.Values.Where(task => task.SessionId == _cursorSession
                && task.RunId == _sidebarProjection.Snapshot?.RunId)) task.Cancellation.Cancel();
        RunClientAction(action);
    }

    private bool HandleKeyBindingsSlash(string input)
    {
        if (input != "/keybindings" && !input.StartsWith("/keybindings ", StringComparison.Ordinal)) return false;
        if (input is "/keybindings" or "/keybindings reload")
        {
            if (input.EndsWith(" reload", StringComparison.Ordinal)) ReloadKeyBindings();
            _composer!.Text = "";
            ShowKeyBindings();
        }
        else ShowMessage(KeyBindingText("usage"));
        return true;
    }

    private string KeyBindingText(string key) => _localization.Resolve("keybindings." + key);

    private string KeyboardHelp(string text)
    {
        if (_keyBindings.Diagnostics.Count > 0) text = KeyBindingText("configuration_notice") + " · /keybindings · " + text;
        var ids = new[] { "sidebar.toggle", "model.select", "preferences.open" };
        for (var i = 0; i < ids.Length; i++) text = text.Replace("F" + (i + 2), "{keyboard" + i + "}", StringComparison.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            var keys = _keyBindings.Service.Bindings.Where(binding => binding.Definition.Target.Id == ids[i])
                .OrderByDescending(binding => binding.Definition.Priority).ThenByDescending(binding => binding.Definition.Source)
                .Select(binding => binding.Definition.Keys).Distinct().ToArray();
            text = text.Replace("{keyboard" + i + "}", keys.FirstOrDefault() ?? "—", StringComparison.Ordinal);
        }
        return text;
    }

    private string KeyBindingsReport()
    {
        var report = new StringBuilder().AppendLine(_keyBindings.Configuration.Path).AppendLine()
            .AppendLine(KeyBindingText("intro")).AppendLine();
        report.AppendLine(KeyBindingText("keys") + " | " + KeyBindingText("target") + " | "
            + KeyBindingText("source") + " | " + KeyBindingText("when") + " | " + KeyBindingText("priority"));
        foreach (var binding in _keyBindings.Service.Bindings)
        {
            var definition = binding.Definition;
            report.AppendLine($"{definition.Keys} → {definition.Target.Id} {string.Join(' ', definition.Target.Arguments)}")
                .AppendLine($"    {KeyBindingText("source." + definition.Source)} ({definition.SourceId}) · {definition.When} · {definition.Priority}");
        }
        report.AppendLine().AppendLine(KeyBindingText("diagnostics"));
        if (_keyBindings.Diagnostics.Count == 0) report.AppendLine(KeyBindingText("none"));
        foreach (var issue in _keyBindings.Diagnostics)
        {
            var localized = KeyBindingText("error." + issue.Code);
            report.AppendLine($"{issue.Source}:{issue.Line}:{issue.Column} · {localized} [{issue.Code}]")
                .AppendLine("    " + issue.Subject + (issue.Detail is null ? "" : " · " + issue.Detail));
        }
        return report.ToString();
    }

    private void ShowKeyBindings()
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(KeyBindingText("title"), _window.Frame.Height - 3);
        frame.Id = "keybindings-screen";
        frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var content = new TextView { Id = "keybindings-report", X = 2, Y = 1, Width = Dim.Fill(4), Height = Dim.Fill(4),
            ReadOnly = true, WordWrap = true, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar, Text = KeyBindingsReport() };
        var reload = new Button { Id = "keybindings-reload", X = 2, Y = Pos.AnchorEnd(2), Text = KeyBindingText("reload") };
        reload.Accepted += (_, e) => { e.Handled = true; ReloadKeyBindings(); ShowKeyBindings(); };
        var close = new Button { Id = "keybindings-close", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(2), Text = KeyBindingText("close") };
        close.Accepted += (_, e) => { e.Handled = true; CloseOverlay(); };
        frame.Add(content, reload, close); ApplyTheme(frame); _overlay = frame; _window.Add(frame); content.SetFocus();
    }
}
