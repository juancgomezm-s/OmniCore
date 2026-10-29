using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

#pragma warning disable CS0618 // Use the existing pinned TextView editor/presenter; no framework migration for this panel.

public sealed partial class TuiApp
{
    private static void CompactCompanionButtons(View parent)
    {
        foreach (var button in parent.SubViews.OfType<Button>().Where(b => b.Height is DimAbsolute { Size: 1 }))
        {
            button.NoPadding = true;
            button.Margin.Thickness = new(0);
            button.Border.Thickness = new(0);
            button.Padding.Thickness = new(0);
        }
    }
    private readonly string _fallbackCompanionInstance = Guid.NewGuid().ToString("N");
    private string _companionInstance => _turnHost?.CompanionInstanceId ?? _fallbackCompanionInstance;
    private CompanionHubSnapshot? _companionSnapshot;
    private CompanionReaction? _companionToolReaction;
    private FrameView? _companionPanel;
    private Button? _companionOpen, _companionRestrict;
    private TextView? _companionBody, _companionInput;
    private Label? _companionInfo;
    private Button? _companionAction;
    private ListView? _companionList;
    private string _companionTab = "chat", _companionChatKey = "";
    private string _companionMemoryVersion = "", _companionMemoryDraft = "";
    private string[] _companionRows = [];
    private CompanionChatMessage[] _companionChat = [];
    private DateTimeOffset _companionPolledAt;
    private bool _companionPolling, _companionChatBusy, _companionStopped, _companionSending;
    private bool _companionQuiet, _companionQuietLoaded;
    private int _companionPetHeight = 12, _companionControlsHeight;
    private string? _companionLayoutTab;
    private System.Drawing.Size _companionPanelWindowSize;
    private CancellationTokenSource? _companionChatCancellation;

    private void PollCompanionHub()
    {
        var hub = _turnHost?.CompanionHub;
        if (hub is null || _companionStopped || _companionPolling
            || DateTimeOffset.UtcNow - _companionPolledAt < TimeSpan.FromSeconds(2)) return;
        _companionPolledAt = DateTimeOffset.UtcNow; _companionPolling = true;
        var session = _cursorSession ?? ""; var run = _sidebarProjection.Snapshot?.RunId;
        var busy = _turnBusy || _state.Overlays.Count > 0;
        var decisions = _state.Overlays.Where(o => o.Questionnaire is null).Select(o =>
            new CompanionDecision(o.Id, o.Title, o.Kind, o.OptionIds.ToArray(), o.Options.ToArray())).ToArray();
        var instance = new CompanionInstance(_companionInstance, _turnHost!.CompanionWorkspace, session, run,
            decisions.Length > 0 ? "Espera una decisión" : busy ? "Trabajando" : "Disponible", DateTimeOffset.UtcNow, decisions, SupportsLateral: true);
        // SQLite, filesystem and provider work never run in Terminal.Gui's input/drawing loop.
        _ = Task.Run(() =>
        {
            try
            {
                hub.Heartbeat(instance);
                foreach (var decision in decisions)
                    hub.Notify(new("decision/" + session + "/" + decision.Id, instance.Id, session,
                        "Decisión pendiente · " + decision.Title, DateTimeOffset.UtcNow));
                var snapshot = hub.Read();
                var quiet = hub.ReadQuiet();
                var delivery = hub.Claim(instance.Id, session, run, busy);
                var chatKey = instance.Workspace + "\n" + session;
                var chat = _companionChatKey == chatKey ? null : hub.Chat(chatKey);
                _uiActions.Enqueue(() =>
                {
                    _companionPolling = false;
                    if (_companionStopped) return;
                    _companionSnapshot = snapshot;
                    _companionToolReaction = snapshot.Reaction;
                    if (!_companionQuietLoaded) { _companionQuiet = quiet; _companionQuietLoaded = true; }
                    if (chat is not null && !_companionChatBusy)
                    { _companionChatKey = chatKey; _companionChat = chat; }
                    RenderCompanionHubControls(); RefreshCompanionHub();
                    if (delivery is not null) ReceiveCompanionDelivery(delivery);
                });
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
            {
                _uiActions.Enqueue(() => { _companionPolling = false;
                    if (_companionInfo is not null) _companionInfo.Text = "Canal local no disponible; se reintentará en el próximo sondeo."; });
            }
        });
    }
    private void StopCompanionHub()
    {
        _companionStopped = true;
        try { _companionChatCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        var hub = _turnHost?.CompanionHub;
        if (hub is not null) _ = Task.Run(() => { try { hub.Disconnect(_companionInstance); } catch (Exception) { } });
    }
    private void NotifyCompanionTurn(int code, string? session)
    {
        if (code == 3 || session is null || _turnHost?.CompanionHub is not { } hub || _pet?.Discovered != true) return;
        var id = "turn/" + session + "/" + Guid.NewGuid().ToString("N");
        var notice = new CompanionNotice(id, _companionInstance, session,
            code == 0 ? "El agente principal terminó su turno" : code == 2 ? "Turno cancelado" : "El agente principal encontró un error", DateTimeOffset.UtcNow);
        _ = Task.Run(() => { try { hub.Notify(notice); } catch (Exception) { } });
    }
    private void ReceiveCompanionDelivery(CompanionDelivery delivery)
    {
        var hub = _turnHost!.CompanionHub!;
        var accepted = false; var detail = "La sesión o la decisión cambiaron antes de recibir el mensaje.";
        if (delivery.Session == _cursorSession && delivery.Run == _sidebarProjection.Snapshot?.RunId)
        {
            if (delivery.Kind == "instruction" && !_turnBusy && _state.Overlays.Count == 0)
            {
                // StartModelTurn leaves the editor and its draft intact; keep the reader's scroll position too.
                var follow = _followConversationEnd;
                accepted = StartModelTurn(delivery.Text);
                _followConversationEnd = follow;
                detail = accepted ? "Instrucción admitida por el agente principal; su resultado llegará como aviso." : "El agente no pudo admitir la instrucción.";
            }
            else if (delivery.Kind == "answer")
            {
                var interaction = _state.Overlays.FirstOrDefault(o => o.Id == delivery.Interaction && o.OptionIds.Contains(delivery.Option));
                if (interaction is not null)
                {
                    var focus = _window is null ? null : FindFocusedPreferenceView(_window);
                    accepted = RespondChoice(interaction, delivery.Option!);
                    if (focus?.Visible == true) focus.SetFocus();
                    detail = accepted ? "Respuesta validada por el Core de la sesión de origen." : "El Core rechazó la respuesta.";
                }
            }
        }
        _ = Task.Run(() => { try { hub.Complete(delivery, accepted, detail); } catch (Exception) { } });
    }
    private void RenderCompanionHubControls()
    {
        if (_sidebar is null || _pet?.Discovered != true) return;
        if (_companionOpen is null)
        {
            _companionOpen = new CompanionRowButton { Id = "companion-hub-open", X = 2, Y = Pos.AnchorEnd(14), Height = 1, Text = "Conversar", NoDecorations = true, ShadowStyle = ShadowStyles.None };
            _companionOpen.Accepted += (_, args) => { args.Handled = true; OpenCompanionHub("chat"); };
            _companionRestrict = new CompanionRowButton { Id = "companion-restrict", X = Pos.Right(_companionOpen) + 1,
                Y = Pos.AnchorEnd(14), Height = 1, Text = "Restringir", NoDecorations = true, ShadowStyle = ShadowStyles.None };
            _companionRestrict.Accepted += (_, args) => { args.Handled = true; ToggleCompanionRestriction(); };
            _sidebar.Add(_companionOpen, _companionRestrict); ApplyTheme(_companionOpen); ApplyTheme(_companionRestrict);
            CompactCompanionButtons(_sidebar);
        }
        var unread = _companionSnapshot?.Notices.Count(n => !n.Read && n.Instance != _companionInstance) ?? 0;
        _companionOpen.Text = !_companionQuiet && unread > 0 ? $"Avisos ({unread})" : "Conversar";
        _companionRestrict!.Text = _pet.Preferences.Restricted ? "Permitir" : "Restringir";
        if (_companionControlsHeight != _companionPetHeight)
        {
            _companionControlsHeight = _companionPetHeight;
            _companionOpen.Y = _companionRestrict.Y = Pos.AnchorEnd(_companionPetHeight + 2);
            if (_companionPanel is not null && _companionPanel.SuperView == _sidebar)
                _companionPanel.Height = Dim.Fill(_companionPetHeight + 3);
        }
        _companionOpen.Visible = _companionRestrict.Visible = _pet.Preferences.Visible;
    }
    private void ToggleCompanionRestriction()
    {
        RunPetAction(_pet?.Preferences.Restricted == true ? "unrestrict" : "restrict", false);
        if (_pet?.Preferences.Restricted == true) { _petPlayground.Home(); _petTyping.Clear(); RenderCompanion(); }
        RenderCompanionHubControls();
    }
    private void OpenCompanionHub(string tab)
    {
        if (_window is null || _turnHost?.CompanionHub is null) { ShowMessage("El canal local necesita una sesión interactiva de OmniCore."); return; }
        if (_window.Frame.Height < 22) { ShowMessage("Amplía la terminal a 22 filas para abrir el mini chat de la mascota."); return; }
        if (_pet?.Discovered != true) RunPetAction("discover", false);
        if (_companionPanel is not null) { if (_companionTab != tab) SelectCompanionTab(tab); _companionInput?.SetFocus(); return; }
        CloseOverlay(); ApplyResponsiveLayout();
        var wide = _sidebar?.Visible == true && _window.Frame.Width >= 100 && _window.Frame.Height >= 32;
        HideCommandHelper();
        var frame = new FrameView { Id = "companion-hub", Title = "Mascota", CanFocus = true, BorderStyle = LineStyle.Single,
            X = wide ? 1 : Pos.Center(), Y = wide ? 1 : Pos.Center(), Width = wide ? Dim.Fill(1) : Dim.Percent(90),
            Height = wide ? Dim.Fill(_companionPetHeight + 3) : Math.Max(18, _window.Frame.Height - 2) };
        frame.Id = "companion-hub"; _companionPanel = frame;
        _companionPanelWindowSize = _window.Frame.Size;
        _companionLayoutTab = null;
        void ButtonAt(string id, string text, int x, int y, Action click)
        {
            var button = new CompanionRowButton { Id = id, Text = text, X = x, Y = y, Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None };
            button.Accepted += (_, args) => { args.Handled = true; click(); }; frame.Add(button);
        }
        ButtonAt("companion-chat-tab", "Chat", 0, 0, () => SelectCompanionTab("chat"));
        ButtonAt("companion-notices-tab", "Avisos", 10, 0, () => SelectCompanionTab("notices"));
        ButtonAt("companion-sessions-tab", "Sesiones", 0, 1, () => SelectCompanionTab("sessions"));
        ButtonAt("companion-memory-tab", "Memoria", 14, 1, () => SelectCompanionTab("memory"));
        _companionInfo = new() { Id = "companion-hub-info", X = 0, Y = 2, Width = Dim.Fill(), Height = 1 };
        _companionBody = new() { Id = "companion-chat-history", X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(6), ReadOnly = true, WordWrap = true };
        _companionList = new() { Id = "companion-hub-list", X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(6), Visible = false };
        _companionInput = new() { Id = "companion-chat-input", X = 0, Y = Pos.AnchorEnd(6), Width = Dim.Fill(), Height = 3, WordWrap = true,
            BorderStyle = LineStyle.Single, Title = "Mensaje" };
        _companionInput.KeyDown += (_, args) => { if (args.KeyCode == KeyCode.Enter && _companionTab == "chat") { args.Handled = true; SendCompanionChat(); } };
        var action = new CompanionRowButton { Id = "companion-hub-action", X = 0, Y = Pos.AnchorEnd(3), Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = "Enviar / Ver" };
        _companionAction = action;
        action.Accepted += (_, args) => { args.Handled = true; CompanionHubAction(); };
        var model = new CompanionRowButton { Id = "companion-model-open", X = Pos.Right(action) + 1, Y = Pos.AnchorEnd(3), Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = "Modelo" };
        model.Accepted += (_, args) => { args.Handled = true; ShowCompanionModelSettings(); };
        var close = new CompanionRowButton { Id = "companion-hub-close", X = 0, Y = Pos.AnchorEnd(1), Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = "Cerrar" };
        close.Accepted += (_, args) => { args.Handled = true; CloseCompanionHub(); };
        var quiet = new CompanionRowButton { Id = "companion-hub-quiet", X = Pos.Right(close) + 1, Y = Pos.AnchorEnd(1), Height = 1, NoDecorations = true, ShadowStyle = ShadowStyles.None, Text = _companionQuiet ? "Avisar" : "Silenciar" };
        quiet.Accepted += (_, args) => { args.Handled = true; _companionQuiet = !_companionQuiet; quiet.Text = _companionQuiet ? "Avisar" : "Silenciar"; RenderCompanionHubControls();
            var value = _companionQuiet; var hub = _turnHost!.CompanionHub!; _ = Task.Run(() => { try { hub.SetQuiet(value); } catch (Exception) { } }); };
        frame.Add(_companionInfo, _companionBody, _companionList, _companionInput, action, model, close, quiet);
        if (wide) _sidebar!.Add(frame); else { _overlay = frame; _window.Add(frame); }
        ApplyTheme(frame); CompactCompanionButtons(frame); SelectCompanionTab(tab); _companionInput.SetFocus(); _companionPolledAt = default; PollCompanionHub();
    }
    private void ReflowCompanionHub()
    {
        if (_companionPanel is null || _window is null || _companionPanelWindowSize == _window.Frame.Size) return;
        _companionPanelWindowSize = _window.Frame.Size;
        var wide = _sidebar?.Visible == true && _window.Frame.Width >= 100 && _window.Frame.Height >= 32;
        if (wide == (_companionPanel.SuperView == _sidebar) && _window.Frame.Height >= 22) return;
        var draft = _companionInput?.Text?.ToString() ?? ""; var tab = _companionTab;
        var focus = FindFocusedPreferenceView(_window); var focusInside = false;
        for (var view = focus; view is not null; view = view.SuperView) if (view == _companionPanel) focusInside = true;
        CloseCompanionHub(); OpenCompanionHub(tab);
        if (_companionInput is not null) _companionInput.Text = draft;
        if (!focusInside && focus?.Visible == true) focus.SetFocus();
    }
    private void CloseCompanionHub()
    {
        if (_companionPanel is not { } panel) return;
        if (_companionTab == "memory") _companionMemoryDraft = _companionInput?.Text?.ToString() ?? "";
        if (_overlay == panel) CloseOverlay();
        else { _sidebar?.Remove(panel); _retiredOverlays.Add(panel); }
        _companionPanel = null; _companionBody = null; _companionInput = null; _companionList = null; _companionInfo = null;
        _composer?.SetFocus();
    }
    private void SelectCompanionTab(string tab)
    {
        if (_companionTab == "memory" && _companionInput is not null) _companionMemoryDraft = _companionInput.Text?.ToString() ?? "";
        _companionTab = tab;
        if (_companionInput is not null)
        {
            if (tab == "memory")
            {
                if (_companionMemoryVersion.Length == 0 && _companionSnapshot is { } snapshot)
                { _companionMemoryVersion = snapshot.Memory.Version; _companionMemoryDraft = snapshot.Memory.Text; }
                _companionInput.Text = _companionMemoryDraft;
            }
            else _companionInput.Text = "";
        }
        _companionRows = []; RefreshCompanionHub();
    }
    private void RefreshCompanionHub()
    {
        if (_companionPanel is null || _companionBody is null || _companionList is null || _companionInput is null || _companionInfo is null) return;
        if (_companionLayoutTab != _companionTab)
        {
            _companionLayoutTab = _companionTab;
            _companionBody.Visible = _companionTab is "chat" or "memory";
            _companionList.Visible = !_companionBody.Visible;
            _companionInput.Visible = _companionTab is "chat" or "memory";
            _companionInput.Title = _companionTab == "memory" ? "Recuerdos" : "Mensaje";
            _companionInput.Height = _companionTab == "memory" ? Dim.Fill(3) : 3;
            _companionInput.Y = _companionTab == "memory" ? 4 : Pos.AnchorEnd(6);
            _companionBody.Height = _companionTab == "memory" ? 1 : Dim.Fill(6);
        }
        if (_companionAction is not null) _companionAction.Text = _companionTab switch { "chat" => "Enviar", "memory" => "Guardar memoria", "sessions" => "Elegir destino", _ => "Responder aquí" };
        if (_companionAction is not null) _companionAction.Enabled = _companionTab != "chat" || _companionChatKey.Length > 0 && !_companionChatBusy;
        if (_companionTab == "chat")
        {
            _companionInfo.Text = _companionChatBusy ? "Pensando…" : "Mascota · Enter envía";
            var text = _companionChat.Length == 0 ? "Hola. Puedes pedirme que recuerde algo, consultar avisos o enviar una instrucción a la sesión que me indiques."
                : string.Join("\n", _companionChat.Select(m => (m.Role == "user" ? "Tú: " : (_pet?.Name ?? "Omni") + ": ") + m.Text));
            if (_companionBody.Text?.ToString() != text) { _companionBody.Text = text; _companionBody.MoveEnd(); }
        }
        else if (_companionTab == "memory")
        {
            _companionInfo.Text = "Memoria local · Guardar cambios";
            _companionBody.Text = _companionSnapshot?.Memory.Path ?? "Cargando…";
            if (_companionMemoryVersion.Length == 0 && _companionSnapshot is { } snapshot)
            { _companionMemoryVersion = snapshot.Memory.Version; _companionInput.Text = _companionMemoryDraft = snapshot.Memory.Text; }
        }
        else
        {
            _companionInfo.Text = _companionTab == "notices" ? "Avisos · Responder aquí" : "Destino: agente principal";
            var rows = _companionTab == "notices" ? (_companionSnapshot?.Notices.Select(n => (n.Read ? "  " : "* ") + n.Title + " · " + Short(n.Session)).ToArray() ?? [])
                : (_companionSnapshot?.Instances.Select(i => Path.GetFileName(i.Workspace) + " · " + i.State + " · " + Short(i.Session) + " @" + Short(i.Id)).ToArray() ?? []);
            if (!_companionRows.SequenceEqual(rows))
            {
                var index = _companionList.SelectedItem ?? 0;
                _companionRows = rows; _companionList.SetSource(new ObservableCollection<string>(rows));
                if (rows.Length > 0) _companionList.SelectedItem = Math.Min(index, rows.Length - 1);
            }
        }
    }
    private static string Short(string text) => text[..Math.Min(8, text.Length)];
    private void SendCompanionChat()
    {
        if (_companionChatBusy || _turnHost is null || _companionInput is null) return;
        if (_companionChatKey.Length == 0) { if (_companionInfo is not null) _companionInfo.Text = "Cargando el mini chat…"; return; }
        var text = _companionInput.Text?.ToString()?.Trim() ?? "";
        if (text.Length is < 1 or > 4000) return;
        _companionChatBusy = true; _companionInput.Text = "";
        var history = _companionChat.Append(new CompanionChatMessage("user", text)).TakeLast(24).ToArray();
        _companionChat = history; var key = _companionChatKey; var hub = _turnHost.CompanionHub!;
        var cancellation = _companionChatCancellation = new(); RefreshCompanionHub();
        _ = Task.Run(async () =>
        {
            string reply;
            try { reply = await _turnHost.CompanionChatAsync(history, cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { reply = "La consulta se canceló o agotó su tiempo de espera."; }
            catch (Exception error) { reply = "No pude conversar: " + OmniCore.Host.OmniCliRuntime.RedactSensitive(error.Message); }
            var result = history.Append(new CompanionChatMessage("assistant", reply)).TakeLast(24).ToArray();
            CompanionHubSnapshot? snapshot = null;
            try { snapshot = hub.Read(); } catch (Exception) { }
            try { hub.SaveChat(key, result); } catch (Exception) { }
            _uiActions.Enqueue(() => { _companionChatBusy = false; if (_companionChatKey == key) _companionChat = result;
                if (snapshot is not null) { _companionSnapshot = snapshot; _companionToolReaction = snapshot.Reaction;
                    if (_companionPanel is null || _companionTab != "memory") { _companionMemoryVersion = ""; _companionMemoryDraft = ""; } }
                RefreshCompanionHub(); RenderCompanion(); });
            _uiActions.Enqueue(() => { if (_companionChatCancellation == cancellation) _companionChatCancellation = null; cancellation.Dispose(); });
        });
    }
    private void CompanionHubAction()
    {
        if (_companionTab == "chat") { SendCompanionChat(); return; }
        if (_companionTab == "memory")
        {
            var text = _companionInput?.Text?.ToString() ?? ""; var version = _companionMemoryVersion;
            _ = Task.Run(() =>
            {
                try
                {
                    var memory = _turnHost!.CompanionHub!.SaveMemory(text, version);
                    _uiActions.Enqueue(() => { _companionMemoryVersion = memory.Version; _companionMemoryDraft = text;
                        if (_companionInfo is not null) _companionInfo.Text = "Memoria guardada localmente."; });
                }
                catch (Exception error) { _uiActions.Enqueue(() => ShowMessage(error.Message + "\n/pet memory reload para recargar.")); }
            }); return;
        }
        var index = _companionList?.SelectedItem ?? -1;
        if (_companionSnapshot is null || index < 0) return;
        if (_companionTab == "sessions" && index < _companionSnapshot.Instances.Length)
            ShowCompanionTarget(_companionSnapshot.Instances[index]);
        else if (_companionTab == "notices" && index < _companionSnapshot.Notices.Length)
        {
            var notice = _companionSnapshot.Notices[index];
            var target = _companionSnapshot.Instances.FirstOrDefault(i => i.Id == notice.Instance && i.Session == notice.Session);
            var hub = _turnHost!.CompanionHub!; _ = Task.Run(() => { try { hub.MarkRead(notice.Id); } catch (Exception) { } });
            if (target is null) ShowMessage("La instancia de origen ya no está conectada. El aviso se conserva; no se abrirá una sesión duplicada.");
            else ShowCompanionTarget(target);
        }
    }
    private void ShowCompanionTarget(CompanionInstance target)
    {
        CloseCompanionHub(); CloseOverlay();
        var frame = OverlayFrame("Agente principal · " + Short(target.Session), Math.Min(25, (_window?.Frame.Height ?? 30) - 2));
        frame.Id = "companion-target";
        if (frame.SubViews.FirstOrDefault(v => v.Id == "omni-menu-title") is { } heading) { frame.Remove(heading); heading.Dispose(); }
        frame.BorderStyle = LineStyle.Single;
        var location = new TextView { X = 1, Y = 0, Width = Dim.Fill(1), Height = 4, ReadOnly = true, WordWrap = true,
            Text = target.Workspace + "\nSesión: " + target.Session + "\nInstancia: " + target.Id + "\n" + target.State };
        var input = new TextView { Id = "companion-target-input", X = 1, Y = 5, Width = Dim.Fill(1), Height = 3, WordWrap = true };
        var send = new CompanionRowButton { Id = "companion-target-send", X = 1, Y = 8, Text = "Enviar", Height = 2 };
        send.Accepted += (_, args) => { args.Handled = true; QueueCompanionDelivery(target, "instruction", input.Text?.ToString() ?? ""); };
        frame.Add(location, input, send);
        var y = 10;
        foreach (var decision in target.Decisions.Take(1))
        {
            frame.Add(new Label { X = 1, Y = y++, Width = Dim.Fill(1), Text = decision.Title });
            for (var n = 0; n < decision.OptionIds.Length && n < 4; n++)
            {
                var option = decision.OptionIds[n];
                var button = new CompanionRowButton { Id = "companion-target-option-" + n, X = 1, Y = y, Height = 2, Text = decision.Labels.ElementAtOrDefault(n) ?? option };
                button.Accepted += (_, args) => { args.Handled = true; QueueCompanionDelivery(target, "answer", "", decision.Id, option); };
                frame.Add(button); y += 2;
            }
        }
        var close = new CompanionRowButton { X = 1, Y = Pos.AnchorEnd(2), Text = "Cerrar", Height = 2 };
        close.Accepted += (_, args) => { args.Handled = true; CloseOverlay(); }; frame.Add(close);
        _overlay = frame; ApplyTheme(frame); _window!.Add(frame); input.SetFocus();
    }
    private void QueueCompanionDelivery(CompanionInstance target, string kind, string text, string? interaction = null, string? option = null)
    {
        if (_companionSending) return;
        _companionSending = true;
        var delivery = new CompanionDelivery(Guid.NewGuid().ToString(), target.Id, target.Session, target.Run, kind, text, interaction, option);
        var hub = _turnHost!.CompanionHub!;
        _ = Task.Run(() =>
        {
            try { hub.Send(delivery); _uiActions.Enqueue(() => { _companionSending = false; ShowMessage("Enviado a " + Short(target.Session) + ". Se recibirá aquí, sin enfocar PowerShell.\nLas instrucciones esperan si el principal está ocupado."); }); }
            catch (Exception error) { _uiActions.Enqueue(() => { _companionSending = false; ShowMessage(error.Message); }); }
        });
    }
}
