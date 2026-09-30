using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Terminal.Gui renderer. All conversation state is reduced from IOmniClient wire events.</summary>
public sealed class TuiApp
{
    private readonly IOmniClient _client;
    private readonly string _locale;
    private readonly Localization _localization;
    private readonly ClientProjection _projection;
    private readonly ModelPolicyHost _policies;
    private ClientState _state = ClientState.Empty();
    private long _lastSequence;
    private Window? _window;
    private Label? _conversation;
    private TextField? _composer;
    private Label? _header;
    private Label? _status;
    private FrameView? _sidebar;
    private Label? _sidebarContent;
    private Label? _completion;
    private View? _overlay;
    private InteractionOverlayModel? _activeInteraction;
    private bool _sidebarOpen;

    public TuiApp(IOmniClient client, string locale = "es", ModelPolicyHost? policies = null)
    {
        _client = client;
        _locale = locale == "en" ? "en" : "es";
        _localization = _locale == "en" ? Localization.English() : Localization.Spanish();
        _projection = new ClientProjection(_localization);
        _policies = policies ?? ModelPolicyHost.Create();
        _state = RefreshState();
    }

    public static int Run(IOmniClient client, string locale = "es")
    {
        var app = new TuiApp(client, locale);
        using var application = Application.Create();
        application.Init();
        var window = app.BuildMainWindow();
        application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            app.PollEvents();
            return true;
        });
        application.Keyboard.KeyDown += (_, key) =>
        {
            var code = key.KeyCode.ToString();
            if (code == "F2") app.ToggleSidebar();
            else if (code == "F3") app.ShowModelPolicies();
            else if (code == "Esc" && app._activeInteraction is { } interaction) app.RespondDefault(interaction);
            else if (code == "Esc" && app._overlay is not null) app.CloseOverlay();
        };
        application.Run(window);
        return 0;
    }

    /// <summary>Creates the main window without starting a terminal session; useful for headless smoke tests.</summary>
    public Window BuildMainWindow()
    {
        _window = new Window { Title = " OmniCore · TUI ", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, Text = HeaderText() };
        var conversationFrame = new FrameView { X = 0, Y = 1, Width = Dim.Fill(31), Height = Dim.Fill(5), Title = " CONVERSATION " };
        _conversation = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = ConversationText() };
        conversationFrame.Add(_conversation);
        _sidebar = new FrameView { X = Pos.AnchorEnd(31), Y = 1, Width = 31, Height = Dim.Fill(5), Title = " SIDEBAR " };
        _sidebarContent = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = SidebarText() };
        _sidebar.Add(_sidebarContent);
        var composerFrame = new FrameView { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 3, Title = " COMPOSER · Enter to send · / commands · @ files · F2 sidebar · F3 models " };
        _composer = new TextField { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, Text = "" };
        _completion = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 1, Text = "" };
        _composer.TextChanged += (_, _) => UpdateAutocomplete();
        _composer.Accepted += (_, _) => SubmitComposer();
        composerFrame.Add(_composer, _completion);
        _status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Text = StatusText() };
        _window.Add(_header, conversationFrame, _sidebar, composerFrame, _status);
        _sidebarOpen = Console.WindowWidth >= 90;
        _window.FrameChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
        RenderState();
        return _window;
    }

    private void ApplyResponsiveLayout()
    {
        if (_window is null || _sidebar is null) return;
        var layout = TuiLayoutModel.ForWidth(_window.Frame.Width, _sidebarOpen);
        _sidebar.Visible = layout.SidebarVisible;
        _sidebar.Width = layout.SidebarWidth;
        _sidebar.X = layout.Mode == TuiLayoutMode.Overlay && layout.SidebarVisible
            ? Pos.Center() : layout.SidebarVisible ? Pos.AnchorEnd(layout.SidebarWidth) : Pos.AnchorEnd(0);
        if (_window.SubViews.Count > 1)
        {
            var conversation = _window.SubViews.ElementAt(1);
            conversation.Width = layout.SidebarVisible && layout.Mode != TuiLayoutMode.Overlay
                ? Dim.Fill(layout.SidebarWidth) : Dim.Fill();
        }
    }

    private void ToggleSidebar()
    {
        _sidebarOpen = !_sidebarOpen;
        ApplyResponsiveLayout();
    }

    private void UpdateAutocomplete()
    {
        if (_composer is null || _completion is null) return;
        var draft = _composer.Text?.ToString() ?? "";
        if (draft.Length == 0 || draft[0] is not ('/' or '@')) { _completion.Text = ""; return; }
        var names = ReadStringArray(_client.Query("commands", CancellationToken.None)?.Json, "commands")
            .Concat(new[] { "context", "tools", "plan", "cancel", "interrupt", "preferences", "models", "sidebar" });
        var suggestions = ComposerAutocomplete.Complete(draft,
            names, ReadStringArray(_client.Query("complete:" + draft[1..], CancellationToken.None)?.Json, "paths"));
        _completion.Text = string.Join("   ", suggestions.Take(5).Select(suggestion => suggestion.Value));
    }

    private void SubmitComposer()
    {
        if (_composer is null) return;
        var input = _composer.Text?.ToString()?.Trim() ?? "";
        if (input.Length == 0) return;
        if (input == "/preferences" || input == "/models") { ShowModelPolicies(); _composer.Text = ""; return; }
        if (input == "/sidebar") { ToggleSidebar(); _composer.Text = ""; return; }
        if (input is "/context" or "/tools" or "/plan")
        {
            var query = input switch { "/plan" => "workingState", _ => input.Substring(1) };
            var result = _client.Query(query, CancellationToken.None)?.Json ?? (_locale == "en" ? "No data available." : "Sin datos disponibles.");
            ShowMessage(result.Length <= 1800 ? result : result.Substring(0, 1800) + "…");
            _composer.Text = "";
            return;
        }
        if (input == "/cancel") { SendCommand("run.cancel", "{}"); _composer.Text = ""; PollEvents(); return; }
        if (input == "/interrupt") { SendCommand("run.interrupt", "{}"); _composer.Text = ""; PollEvents(); return; }
        if (input[0] == '/')
        {
            if (!CommandLineParser.TryParse(input, out var invocation) || invocation is null) return;
            var ack = _client.Send(WireEnvelope.Command(Ids.NewV7(), CommandInvocationJson.Encode(invocation)), CancellationToken.None);
            if (ack.Status == "ok")
            {
                var outcome = _client.Query("commandOutcome", CancellationToken.None)?.Json ?? "{}";
                var text = JsonObj.Parse(outcome);
                if (text.TryGetValue("outcome", out var _) && TryReadOutcome(outcome, out var expanded))
                    SendCommand("session.input", "\"text\":" + ("\"" + JsonObj.Escape(expanded) + "\"") + ",\"mode\":\"act\"");
            }
            else ShowMessage("/" + invocation.Name + ": " + (ack.Error ?? "command failed"));
        }
        else if (input[0] == '@')
        {
            ShowMessage(_locale == "en" ? "Select a workspace suggestion; reference resolution is not available in this Host version." : "Selecciona una sugerencia del workspace; este Host aún no resuelve referencias @.");
        }
        else
        {
            SendCommand("session.input", "\"text\":" + ("\"" + JsonObj.Escape(input) + "\"") + ",\"mode\":\"act\"");
        }
        _composer.Text = "";
        PollEvents();
    }

    private void PollEvents()
    {
        foreach (var envelope in _client.SubscribeSince(_lastSequence + 1))
        {
            _state = _projection.Apply(_state, envelope);
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(envelope.PayloadJson);
                if (document.RootElement.TryGetProperty("seq", out var seq)
                    && long.TryParse(seq.GetString(), out var sequence)) _lastSequence = Math.Max(_lastSequence, sequence);
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (_client.Query("workspaceStatus", CancellationToken.None) is { } statusQuery)
            _state = _projection.ApplyQuery(_state, statusQuery);
        RenderState();
        ShowCurrentInteraction();
    }

    private ClientState RefreshState()
    {
        var state = ClientState.Empty();
        foreach (var envelope in _client.SubscribeSince(0))
        {
            state = _projection.Apply(state, envelope);
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(envelope.PayloadJson);
                if (document.RootElement.TryGetProperty("seq", out var seq)
                    && long.TryParse(seq.GetString(), out var sequence)) _lastSequence = Math.Max(_lastSequence, sequence);
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (_client.Query("workspaceStatus", CancellationToken.None) is { } statusQuery)
            state = _projection.ApplyQuery(state, statusQuery);
        return state;
    }

    private void RenderState()
    {
        if (_conversation is not null) _conversation.Text = ConversationText();
        if (_header is not null) _header.Text = HeaderText();
        if (_status is not null) _status.Text = StatusText();
        if (_sidebarContent is not null) _sidebarContent.Text = SidebarText();
    }

    private string ConversationText() => string.Join("\n\n", _state.Conversation.Blocks.Select(block => block.Role switch
    {
        ConversationRole.User => "◉ " + block.Text,
        ConversationRole.Assistant => "◆ " + block.Text,
        ConversationRole.Tool => "● " + (block.ToolName ?? "tool") + " · " + block.Text,
        ConversationRole.Interaction => "! " + block.Text,
        _ => "○ " + block.Text,
    }));

    private string HeaderText() => (_state.Header.WorkingDirectory.Length == 0 ? Environment.CurrentDirectory : _state.Header.WorkingDirectory)
        + (_state.Header.GitBranch is null ? "" : "   " + _state.Header.GitBranch + " " + (_state.Header.GitDirty ?? ""));

    private string StatusText()
    {
        var presentation = StatusLinePresentation.From(_state.StatusLine);
        return " " + presentation.Left + "                                        " + presentation.Right;
    }

    private string SidebarText()
    {
        var widgets = new SidebarHost(new ISidebarWidget[]
        {
            new SessionSidebarWidget(new SessionWidgetData(_state.Header.WorkingDirectory.Length == 0 ? "Session" : Path.GetFileName(_state.Header.WorkingDirectory), _state.StatusLine.Mode)),
            new PlanSidebarWidget(new PlanWidgetData("PLAN", _state.Sidebar.WidgetIds.Contains("core.plan")
                ? new[] { new WidgetRowModel(_locale == "en" ? "No active plan" : "Sin plan activo", ThemeRole.Muted) } : Array.Empty<WidgetRowModel>())),
            new ChangedFilesSidebarWidget(new ChangedFileWidgetData(_state.Sidebar.WidgetIds.Contains("core.files")
                ? new[] { new WidgetRowModel(_locale == "en" ? "No changed files" : "Sin archivos modificados", ThemeRole.Muted) } : Array.Empty<WidgetRowModel>())),
        });
        return string.Join("\n\n", widgets.Build(_state, WidgetSize.Normal).SelectMany(item => item.Model is ListWidgetModel model
            ? new[] { model.Title }.Concat(model.Rows.Select(row => ThemeGlyphs.For(row.Role) + " " + row.Text)) : Array.Empty<string>()));
    }

    private void ShowCurrentInteraction()
    {
        if (_overlay is not null || _state.Overlays.Count == 0 || _window is null) return;
        var interaction = _state.Overlays[0];
        if (interaction.Questionnaire is not null) ShowQuestionnaire(interaction);
        else ShowChoiceInteraction(interaction);
    }

    private void ShowChoiceInteraction(InteractionOverlayModel interaction)
    {
        var overlay = OverlayFrame(interaction.Title, 7 + interaction.Options.Count);
        _activeInteraction = interaction;
        overlay.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(2), Height = 2,
            Text = interaction.Subject.Length == 0 ? interaction.Kind : interaction.Subject });
        Button? defaultButton = null;
        for (var index = 0; index < interaction.Options.Count; index++)
        {
            var optionId = interaction.OptionIds.ElementAtOrDefault(index) ?? "";
            var button = new Button { X = 1, Y = 2 + index, Text = interaction.Options[index],
                IsDefault = optionId == interaction.DefaultOptionId };
            button.Accepted += (_, _) => RespondChoice(interaction, optionId);
            overlay.Add(button);
            if (optionId == interaction.DefaultOptionId) defaultButton = button;
        }
        overlay.Add(new Label { X = 1, Y = 3 + interaction.Options.Count, Text = _locale == "en" ? "Esc: default / deny" : "Esc: opción predeterminada / denegar" });
        _overlay = overlay;
        _window!.Add(overlay);
        ((View?)defaultButton ?? overlay).SetFocus();
    }

    private void ShowQuestionnaire(InteractionOverlayModel interaction)
    {
        var model = interaction.Questionnaire!;
        _activeInteraction = interaction;
        var overlay = OverlayFrame(model.Title, Math.Min(24, 8 + model.Questions.Sum(question => question.Choices.Count + 3)));
        var inputs = new Dictionary<string, QuestionnairePlainFormInput>(StringComparer.Ordinal);
        var checkBoxes = new Dictionary<string, List<(string Id, CheckBox Control)>>(StringComparer.Ordinal);
        var textFields = new Dictionary<string, TextField>(StringComparer.Ordinal);
        var otherFields = new Dictionary<string, TextField>(StringComparer.Ordinal);
        var y = 0;
        foreach (var question in model.Questions)
        {
            overlay.Add(new Label { X = 1, Y = y++, Width = Dim.Fill(2), Text = question.Prompt });
            if (!string.IsNullOrEmpty(question.HelpText)) overlay.Add(new Label { X = 2, Y = y++, Width = Dim.Fill(3), Text = question.HelpText });
            if (question.Kind == QuestionnaireQuestionKind.FreeText)
            {
                var field = new TextField { X = 2, Y = y++, Width = Dim.Fill(3), Text = "" };
                textFields[question.Id] = field; overlay.Add(field);
            }
            else
            {
                var checkList = new List<(string, CheckBox)>();
                var choices = question.Choices.Select(choice => (choice.Id, choice.Label)).ToList();
                if (question.Other is not null) choices.Add((question.Other.OptionId, question.Other.Label));
                foreach (var choice in choices)
                {
                    var checkbox = new CheckBox { X = 2, Y = y++, Text = choice.Label,
                        RadioStyle = question.Kind == QuestionnaireQuestionKind.SingleChoice };
                    if (question.Kind == QuestionnaireQuestionKind.SingleChoice)
                        checkbox.Accepted += (_, _) =>
                        {
                            foreach (var otherChoice in checkList)
                                if (!ReferenceEquals(otherChoice.Item2, checkbox)) otherChoice.Item2.Value = CheckState.UnChecked;
                        };
                    overlay.Add(checkbox); checkList.Add((choice.Id, checkbox));
                    if (question.Other?.OptionId == choice.Id)
                    {
                        if (!string.IsNullOrWhiteSpace(question.Other.Placeholder))
                            overlay.Add(new Label { X = 5, Y = y++, Width = Dim.Fill(3), Text = question.Other.Placeholder });
                        var other = new TextField { X = 5, Y = y++, Width = Dim.Fill(3), Text = "" };
                        otherFields[question.Id] = other; overlay.Add(other);
                    }
                }
                checkBoxes[question.Id] = checkList;
            }
        }
        var error = new Label { X = 1, Y = Math.Min(y, 18), Width = Dim.Fill(2), Height = 1, Text = "" };
        var submit = new Button { X = Pos.AnchorEnd(22), Y = Math.Min(y + 1, 20), Text = model.SubmitLabel, IsDefault = true };
        var cancel = new Button { X = Pos.AnchorEnd(12), Y = Math.Min(y + 1, 20), Text = model.CancelLabel };
        submit.Accepted += (_, _) =>
        {
            foreach (var question in model.Questions)
            {
                var selected = checkBoxes.TryGetValue(question.Id, out var list)
                    ? string.Join(" ", list.Where(item => item.Control.Value == CheckState.Checked).Select(item => item.Id)) : null;
                inputs[question.Id] = new QuestionnairePlainFormInput(selected,
                    textFields.TryGetValue(question.Id, out var text) ? text.Text?.ToString() : null,
                    otherFields.TryGetValue(question.Id, out var other)
                        && question.Other is { } otherModel
                        && selected?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(otherModel.OptionId, StringComparer.Ordinal) == true
                            ? other.Text?.ToString() : null);
            }
            var result = QuestionnaireTuiForm.Submit(model, inputs);
            if (!result.IsValid)
            {
                error.Text = string.Join("; ", result.Errors.Select(_localization.ResolveQuestionnaireValidationError));
                return;
            }
            RespondQuestionnaire(interaction, result.Response!);
        };
        cancel.Accepted += (_, _) => RespondQuestionnaire(interaction,
            new OmniCore.Client.QuestionnaireResponseDto(Array.Empty<OmniCore.Client.QuestionnaireAnswerDto>(), true));
        overlay.Add(error, submit, cancel);
        _overlay = overlay;
        _window!.Add(overlay);
        overlay.SetFocus();
    }

    private FrameView OverlayFrame(string title, int height)
    {
        var frame = new FrameView { X = Pos.Center(), Y = Pos.Center(), Width = Dim.Percent(80), Height = height,
            Title = " " + title + " ", CanFocus = true };
        return frame;
    }

    private void RespondDefault(InteractionOverlayModel interaction)
    {
        if (interaction.Questionnaire is not null)
            RespondQuestionnaire(interaction, new OmniCore.Client.QuestionnaireResponseDto(Array.Empty<OmniCore.Client.QuestionnaireAnswerDto>(), true));
        else RespondChoice(interaction, interaction.DefaultOptionId);
    }

    private void RespondChoice(InteractionOverlayModel interaction, string optionId)
    {
        var payload = "{\"cmd\":\"interaction.respond\"," + JsonObj.Field("interactionId", interaction.Id)
            + "," + JsonObj.Field("optionId", optionId) + "}";
        _client.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        CloseOverlay(); PollEvents();
    }

    private void RespondQuestionnaire(InteractionOverlayModel interaction, OmniCore.Client.QuestionnaireResponseDto response)
    {
        var answers = response.Answers.Select(answer => "{" + JsonObj.Field("questionId", answer.QuestionId) + ","
            + "\"selectedOptionIds\":[" + string.Join(",", answer.SelectedOptionIds.Select(id => ("\"" + JsonObj.Escape(id) + "\""))) + "],"
            + JsonObj.FieldRaw("text", answer.Text is null ? "null" : "\"" + JsonObj.Escape(answer.Text) + "\"") + ","
            + JsonObj.FieldRaw("otherText", answer.OtherText is null ? "null" : "\"" + JsonObj.Escape(answer.OtherText) + "\"") + "}");
        var payload = "{\"cmd\":\"interaction.respond\",\"responseType\":\"questionnaire\"," + JsonObj.Field("interactionId", interaction.Id)
            + ",\"answers\":[" + string.Join(",", answers) + "],\"cancelled\":" + (response.Cancelled ? "true" : "false") + "}";
        _client.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        CloseOverlay(); PollEvents();
    }

    private void ShowModelPolicies()
    {
        if (_window is null || _overlay is not null) return;
        var frame = OverlayFrame(_locale == "en" ? "Preferences > Models" : "Preferencias > Modelos", 18);
        var y = 0;
        foreach (var descriptor in _policies.Models)
        {
            var key = new ModelPolicyKeyDto(descriptor.ProviderId, descriptor.Id);
            var identity = key.ToString();
            var record = _policies.Get(key, CancellationToken.None);
            var label = descriptor.ProviderId + "/" + descriptor.Id + " · " + (record?.Category ?? (_locale == "en" ? "setup required" : "requiere configuración"));
            var select = new Button { X = 1, Y = y++, Text = label };
            select.Accepted += (_, _) =>
            {
                var selected = _policies.Select("cli|" + Environment.CurrentDirectory, key, descriptor.Id, false, CancellationToken.None);
                if (selected.NeedsOnboarding && selected.Draft is not null)
                {
                    CloseOverlay(); ShowModelPolicySetup(selected.Draft);
                }
                else ShowMessage(_locale == "en" ? "Model policy selected." : "Política del modelo seleccionada.");
            };
            frame.Add(select);
            if (record is not null)
            {
                var delete = new Button { X = 3, Y = y++, Text = _locale == "en" ? "Delete policy" : "Eliminar política" };
                delete.Accepted += (_, _) =>
                {
                    _policies.Delete(key, record.Revision, CancellationToken.None);
                    CloseOverlay(); ShowModelPolicies();
                };
                frame.Add(delete);
            }
        }
        var close = new Button { X = Pos.AnchorEnd(12), Y = 15, Text = _locale == "en" ? "Close" : "Cerrar" };
        close.Accepted += (_, _) => CloseOverlay();
        frame.Add(close);
        _overlay = frame; _window.Add(frame); frame.SetFocus();
    }

    private void ShowModelPolicySetup(ModelPolicyDraftDto draft)
    {
        if (_window is null) return;
        var model = ModelPolicySetupModel.From(draft.Key.ToString(), draft.RecommendedCategory, draft.Warnings);
        var frame = OverlayFrame(_locale == "en" ? "Model setup" : "Configurar modelo", 14);
        frame.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(2), Text = model.ModelIdentity });
        var y = 1;
        foreach (var warning in model.Warnings) frame.Add(new Label { X = 1, Y = y++, Width = Dim.Fill(2), Text = "! " + warning });
        foreach (var choice in model.Choices)
        {
            var button = new Button { X = 1, Y = y++, Text = (choice.Recommended ? "[recommended] " : "") + choice.Category + " — " + choice.Description };
            button.Accepted += (_, _) =>
            {
                _policies.Set(draft.Key, 0, choice.Category, null, CancellationToken.None);
                _policies.Select("cli|" + Environment.CurrentDirectory, draft.Key, draft.Key.ModelId, false, CancellationToken.None);
                CloseOverlay(); ShowModelPolicies();
            };
            frame.Add(button);
        }
        var once = new Button { X = 1, Y = y++, Text = _locale == "en" ? "Use once safely (ObserveOnly)" : "Usar una vez de forma segura (ObserveOnly)" };
        once.Accepted += (_, _) =>
        {
            _policies.Select("cli|" + Environment.CurrentDirectory, draft.Key, draft.Key.ModelId, true, CancellationToken.None);
            CloseOverlay(); ShowMessage("ObserveOnly");
        };
        var cancel = new Button { X = 1, Y = y, Text = _locale == "en" ? "Cancel" : "Cancelar" };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(); };
        frame.Add(once, cancel); _overlay = frame; _window!.Add(frame); frame.SetFocus();
    }

    private void ShowMessage(string message)
    {
        if (_window is null) return;
        var frame = OverlayFrame(_locale == "en" ? "Notice" : "Aviso", 5);
        frame.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(2), Text = message });
        var ok = new Button { X = Pos.AnchorEnd(12), Y = 2, Text = "OK" };
        ok.Accepted += (_, _) => CloseOverlay(); frame.Add(ok); _overlay = frame; _window.Add(frame); frame.SetFocus();
    }

    private void CloseOverlay()
    {
        if (_overlay is null || _window is null) return;
        _window.Remove(_overlay); _overlay.Dispose(); _overlay = null; _activeInteraction = null;
        _composer?.SetFocus();
    }

    private void SendCommand(string command, string fields) => _client.Send(
        WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":" + ("\"" + JsonObj.Escape(command) + "\"") + (fields.Length == 0 ? "" : "," + fields) + "}"),
        CancellationToken.None);

    private static string[] ReadStringArray(string? json, string property)
    {
        if (json is null) return Array.Empty<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var array) && array.ValueKind == System.Text.Json.JsonValueKind.Array
                ? array.EnumerateArray().Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String).Select(item => item.GetString() ?? "").ToArray()
                : Array.Empty<string>();
        }
        catch (System.Text.Json.JsonException) { return Array.Empty<string>(); }
    }

    private static bool TryReadOutcome(string json, out string text)
    {
        text = "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            text = outcome.GetProperty("text").GetString() ?? "";
            return true;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
}
