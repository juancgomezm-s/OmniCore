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
    private readonly SessionSidebarProjection _sidebarProjection = new();
    private readonly AgentsProjection _agents = new();
    private DateTimeOffset _agentsPolledAt;
    private readonly ChangedFilesProjection _files = new();
    private SidebarPreferencesSnapshot _sidebarSettings = new("", new(), []);
    private DateTimeOffset _sidebarSettingsPolledAt;
    private string? _sidebarSelection;
    private SidebarView? _sidebarSession;
    private Label? _sidebarNavigation;
    private Label? _sidebarAttention;
    private ISidebarWidget[] _sidebarWidgets = [];
    private SidebarComposition? _sidebarComposition;
    private readonly SessionObservabilityProjection _observability = new();
    private readonly ModelPolicyHost _policies;
    private ClientState _state = ClientState.Empty();
    private long _lastSequence;
    private Window? _window;
    private ConversationView? _conversation;
    private string? _renderedConversation;
    private ComposerView? _composer;
    private int _composerRows = 1;
    private bool _followConversationEnd;
    private bool _renderingConversation;
    private FrameView? _composerFrame;
    private FrameView? _conversationFrame;
    private Label? _help;
    private int _mainColumnInset;
    private Label? _header;
    private Label? _status;
    private FrameView? _sidebar;
    private SidebarView? _sidebarContent;
    private Button? _sidebarDetails;
    private bool _sidebarExpanded;
    private Label? _completion;
    private FrameView? _commandHelper;
    private ListView? _commandList;
    private string[] _commandSuggestions = Array.Empty<string>();
    private View? _overlay;
    private InteractionOverlayModel? _activeInteraction;
    private bool _sidebarOpen;
    private readonly ITuiAccountHost? _accountOverride;
    private ITuiAccountHost? _account;
    private CancellationTokenSource? _loginCancellation;
    private CancellationTokenSource? _catalogCancellation;
    private string? _catalogNotice;
    private bool _catalogFresh;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _uiActions = new();
    private string WorkspaceSelectionId => ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
    private string? _selectedModel;
    private string? _observedModel;
    private bool _showMarkdownExamples;
    private readonly List<View> _retiredOverlays = new();
    private readonly List<View> _overlaysReadyForDisposal = new();
    private readonly bool _ownsPolicies;
    private readonly ITuiTurnHost? _turnHost;
    private CancellationTokenSource? _turnCancellation;
    private Task? _turnTask;
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, Task Task)> _delegationTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _turnBusy;
    private string? _pendingEscalationResume;
    private string? _pendingQuotaResume;
    private View? _activity;
    private Label? _activityLabel;
    private int _activityPhase;
    internal View? Activity => _activity;
    private string? _turnStatus;
    private string? _cursorSession;

    // Acceso interno para pruebas de cableado reales (Terminal.Gui real sobre IOmniClient real).
    internal Window? MainWindow => _window;
    internal ComposerView? Composer => _composer;
    internal ConversationView? Conversation => _conversation;
    internal FrameView? Sidebar => _sidebar;
    internal SidebarView? SidebarContent => _sidebarContent;
    internal SidebarView? SidebarSession => _sidebarSession;
    internal SidebarComposition? SidebarComposition => _sidebarComposition;
    internal Label? Completion => _completion;
    internal Label? Status => _status;
    internal Label? Header => _header;
    internal View? Overlay => _overlay;
    internal ClientState ProjectionState => _state;
    internal bool SidebarOpen => _sidebarOpen;
    internal ModelPolicyHost Policies => _policies;

    public TuiApp(IOmniClient client, string locale = "es", ModelPolicyHost? policies = null, ITuiAccountHost? account = null, ITuiTurnHost? turnHost = null)
    {
        _client = client;
        _locale = locale == "en" ? "en" : "es";
        _localization = _locale == "en" ? Localization.English() : Localization.Spanish();
        _projection = new ClientProjection(_localization);
        _policies = policies ?? ModelPolicyHost.Create();
        _ownsPolicies = policies is null;
        _accountOverride = account;
        _turnHost = turnHost;
        RefreshSidebarPreferences();
        _selectedModel = _policies.CurrentSelection(WorkspaceSelectionId, CancellationToken.None)?.ModelId;
        _state = RefreshState();
    }

    public static string RenderNonInteractive(IOmniClient client, string locale = "es")
    {
        var app = new TuiApp(client, locale);
        var state = app._state;
        var header = state.Header.WorkingDirectory.Length == 0 ? Environment.CurrentDirectory
            : state.Header.WorkingDirectory;
        var conversation = string.Join(Environment.NewLine, state.Conversation.Blocks.Select(block => block.Role switch
        {
            ConversationRole.User => "◉ " + block.Text,
            ConversationRole.Assistant => "◆ " + block.Text,
            ConversationRole.Tool => "● " + (block.ToolName ?? "tool") + " · " + block.Text,
            ConversationRole.Interaction => "! " + block.Text,
            _ => "○ " + block.Text,
        }));
        var status = StatusLinePresentation.From(state.StatusLine);
        return "OmniCore · TUI (sim)" + Environment.NewLine + header + Environment.NewLine
            + (conversation.Length == 0 ? (locale == "en" ? "No conversation yet." : "Todavía no hay conversación.")
                : conversation) + Environment.NewLine + status.Left + " · " + status.Right;
    }

    public static int Run(IOmniClient client, string locale = "es", ITuiTurnHost? turnHost = null)
    {
        var app = new TuiApp(client, locale, turnHost: turnHost);
        using var application = Application.Create();
        application.Init();
        return app.RunWith(application);
    }

    /// <summary>
    /// Construye la ventana, cablea teclado y polling, y ejecuta el bucle de sesión.
    /// Es el mismo cableado que usa <see cref="Run"/>; separado para pruebas de cableado reales
    /// que crean la aplicación con un driver controlado.
    /// </summary>
    internal int RunWith(IApplication application)
    {
        var window = BuildMainWindow();
        application.AddTimeout(TimeSpan.FromMilliseconds(500), PollOnce);
        application.AddTimeout(TimeSpan.FromMilliseconds(100), AnimateActivity);
        application.Keyboard.KeyDown += OnApplicationKeyDown;
        void AfterDraw(object? sender, EventArgs args)
        {
            // Complete a whole subsequent frame before releasing removed adornments:
            // the frame in which a panel was removed can still retain its draw context.
            foreach (var view in _overlaysReadyForDisposal) view.Dispose();
            _overlaysReadyForDisposal.Clear();
            _overlaysReadyForDisposal.AddRange(_retiredOverlays);
            _retiredOverlays.Clear();
        }
        application.LayoutAndDrawComplete += AfterDraw;
        try { application.Run(window); }
        finally
        {
            application.LayoutAndDrawComplete -= AfterDraw;
            try { _loginCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            try { _catalogCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            _catalogCancellation?.Dispose(); _catalogCancellation = null;
            _turnCancellation?.Cancel();
            var turnStopped = _turnTask is null;
            if (_turnTask is not null)
            {
                try { turnStopped = _turnTask.Wait(TimeSpan.FromSeconds(10)); }
                catch (AggregateException) { turnStopped = true; }
            }
            if (turnStopped) _turnCancellation?.Dispose();
            else
            {
                var pendingCancellation = _turnCancellation;
                _ = _turnTask!.ContinueWith(_ => pendingCancellation?.Dispose(), TaskScheduler.Default);
            }
            var delegations = _delegationTasks.Values.ToArray();
            foreach (var delegation in delegations)
                try { delegation.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            var delegationsStopped = delegations.Length == 0;
            if (delegations.Length > 0)
            {
                try { delegationsStopped = Task.WhenAll(delegations.Select(item => item.Task))
                    .Wait(TimeSpan.FromSeconds(10)); }
                catch (AggregateException) { delegationsStopped = true; }
            }
            foreach (var delegation in delegations)
            {
                if (delegationsStopped) delegation.Cancellation.Dispose();
                else
                {
                    var pendingCancellation = delegation.Cancellation;
                    _ = delegation.Task.ContinueWith(_ => pendingCancellation.Dispose(), TaskScheduler.Default);
                }
            }
            _delegationTasks.Clear();
            DisposeRetiredOverlays();
            if (_ownsPolicies) _policies.Dispose();
        }
        return 0;
    }

    /// <summary>Poll de eventos del cliente; devuelve true para repetir como AddTimeout.</summary>
    internal bool PollOnce()
    {
        while (_uiActions.TryDequeue(out var action)) action();
        PollEvents();
        return true;
    }

    internal bool AnimateActivity()
    {
        if (!HasActiveModelWork) return true;
        _activityPhase = (_activityPhase + 1) % 24;
        RenderActivity();
        return true;
    }

    private void RenderActivity()
    {
        if (_activity is null) return;
        _activity.Visible = HasActiveModelWork && (_window?.Frame.Width ?? 80) - _mainColumnInset >= 30;
        if (_activityLabel is not null) _activityLabel.Visible = _activity.Visible;
        if (_completion is not null) _completion.Visible = !_activity.Visible;
        if (!_activity.Visible) return;
        var head = _activityPhase <= 12 ? _activityPhase - 2 : 22 - _activityPhase;
        var colors = new[] { "#12303D", "#205061", "#30788B", "#4DBAC9", "#83C6DE", "#B47CE7" };
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        for (var index = 0; index < _activity.SubViews.Count; index++)
        {
            var intensity = Math.Clamp(5 - Math.Abs(index - head), 0, 5);
            var cell = _activity.SubViews.ElementAt(index);
            cell.Text = noColor ? (intensity > 3 ? "▮" : intensity > 1 ? "▪" : "·") : index < 2 ? "▪" : "▮";
            var color = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(colors[intensity]),
                noColor ? Color.None : new Color("#061822"));
            cell.SetScheme(new Scheme { Normal = color, Focus = color });
            cell.SetNeedsDraw();
        }
    }

    /// <summary>Teclas a nivel de aplicación: F2 sidebar, F3 modelos, Esc responde/overlay.</summary>
    internal void OnApplicationKeyDown(object? sender, Key key)
    {
        // Esc es la tecla Command.Quit POR DEFECTO en Terminal.Gui 2.6 (Application.DefaultKeyBindings):
        // si no se marca Handled tras consumirla, el framework además cierra toda la aplicación
        // (un Esc para cerrar un overlay mataría la TUI entera). F2/F3 igual: consumidas aquí.
        var code = key.KeyCode.ToString();
        if (code == "F2") { ToggleSidebar(); key.Handled = true; }
        else if (code == "F3") { ShowModelPicker(); key.Handled = true; }
        else if (code == "F4") { ShowPreferences(); key.Handled = true; }
        else if (code == "Esc" && _activeInteraction is { } interaction) { RespondDefault(interaction); key.Handled = true; }
        else if (code == "Esc" && _overlay is not null) { CloseOverlay(); key.Handled = true; }
        else if (code == "Esc" && _commandHelper is not null) { HideCommandHelper(); key.Handled = true; }
    }

    /// <summary>Creates the main window without starting a terminal session; useful for headless smoke tests.</summary>
    public Window BuildMainWindow()
    {
        _window = new Window { Title = "OmniCore", BorderStyle = LineStyle.None, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _header = new Label { Id = "omni-heading", X = 2, Y = 0, Width = Dim.Fill(2), Height = 1, Text = HeaderText() };
        var conversationFrame = _conversationFrame = new FrameView { X = 0, Y = 2, Width = Dim.Fill(31), Height = Dim.Fill(7), BorderStyle = LineStyle.None };
        _conversation = new ConversationView { X = 2, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(),
            ReadOnly = true, WordWrap = true, ScrollBars = true, Text = ConversationText() };
        _renderedConversation = null;
        // Keep user intent independent of content/layout changes, as in OmniCoder.
        _conversation.KeyDown += (_, key) =>
        {
            var code = key.KeyCode & ~(KeyCode.CtrlMask | KeyCode.ShiftMask | KeyCode.AltMask);
            if (code is KeyCode.Home or KeyCode.CursorUp or KeyCode.PageUp) _followConversationEnd = false;
            else if (code == KeyCode.End) _followConversationEnd = true;
        };
        _conversation.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.WheeledUp)) _followConversationEnd = false;
        };
        _conversation.VerticalScrollBar.MouseEvent += (_, mouse) =>
        {
            // A scrollbar gesture is explicit reading/navigation, not a layout notification.
            if (mouse.Flags != MouseFlags.None) _followConversationEnd = false;
        };
        _conversation.ViewportChanged += (_, _) => RenderConversation();
        ApplyTheme(_conversation);
        conversationFrame.Add(_conversation);
        _sidebar = new FrameView { Id = "omni-panel", X = Pos.AnchorEnd(31), Y = 0, Width = 31, Height = Dim.Fill(), Title = " Workspace ", BorderStyle = LineStyle.None };
        _sidebarContent = new SidebarView { X = 2, Y = 11, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        _sidebarContent.RowActivated += row => { if (row.Action == "diff.open" && row.Target is { } id) ShowFileDiff(id); };
        _sidebarSession = new SidebarView { X = 2, Y = 3, Width = Dim.Fill(1), Height = 5, CanFocus = false, ScrollBars = false };
        _sidebarDetails = new Button { Id = "sidebar-details", X = 2, Y = 1, Text = Ui("Más detalle", "More detail") };
        _sidebarDetails.Accepted += (_, _) =>
        {
            _sidebarExpanded = !_sidebarExpanded;
            _sidebarDetails.Text = _sidebarExpanded ? Ui("Menos detalle", "Less detail") : Ui("Más detalle", "More detail");
            RenderSidebar();
        };
        var settings = new Button { Id = "sidebar-settings", X = Pos.AnchorEnd(9), Y = 1, Text = Ui("Ajustes", "Settings") };
        settings.Accepted += (_, _) => ShowSidebarSettings();
        var previous = new Button { Id = "sidebar-prev", X = 1, Y = 8, Text = "‹" };
        var next = new Button { Id = "sidebar-next", X = Pos.AnchorEnd(3), Y = 8, Text = "›" };
        previous.Accepted += (_, _) => NavigateSidebar(-1); next.Accepted += (_, _) => NavigateSidebar(1);
        _sidebarNavigation = new Label { Id = "omni-heading", X = 5, Y = 8, Width = Dim.Fill(5), Height = 1 };
        _sidebarAttention = new Label { Id = "omni-help", X = 2, Y = 9, Width = Dim.Fill(1), Height = 1 };
        _sidebar.Add(new Label { Id = "omni-heading", X = 2, Y = 0, Text = "Workspace" }, _sidebarDetails, settings,
            _sidebarSession, previous, next, _sidebarNavigation, _sidebarAttention, _sidebarContent);
        var composerFrame = _composerFrame = new FrameView { Id = "omni-composer", X = 1, Y = Pos.AnchorEnd(6), Width = Dim.Fill(1), Height = 3, BorderStyle = LineStyle.None };
        composerFrame.Add(new View { Id = "omni-composer-accent", X = 0, Y = 0, Width = 1, Height = Dim.Fill() });
        _composer = new ComposerView { X = 4, Y = 1, Width = Dim.Fill(2), Height = 1, Text = "", Multiline = true,
            WordWrap = true, EnterKeyAddsLine = true };
        composerFrame.Add(new Label { Id = "omni-heading", X = 2, Y = 1, Text = "›" });
        _completion = new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Height = 1, Text = "" };
        _composer.TextChanged += (_, _) => { UpdateComposerHeight(); UpdateAutocomplete(); };
        _composer.ContentsChanged += (_, _) => { UpdateComposerHeight(); UpdateAutocomplete(); };
        _composer.KeyDown += (_, key) =>
        {
            if (key.KeyCode == (KeyCode.Enter | KeyCode.ShiftMask)) { _composer.InsertText("\n"); key.Handled = true; return; }
            if (key.KeyCode == KeyCode.Enter) { SubmitComposer(); key.Handled = true; return; }
            if (_commandList is null || _commandSuggestions.Length == 0) return;
            if (key.KeyCode is KeyCode.CursorDown or KeyCode.CursorUp)
            {
                var delta = key.KeyCode == KeyCode.CursorDown ? 1 : -1;
                _commandList.SelectedItem = ((_commandList.SelectedItem ?? 0) + delta + _commandSuggestions.Length) % _commandSuggestions.Length;
                _commandList.EnsureSelectedItemVisible(); key.Handled = true;
            }
            else if (key.KeyCode == KeyCode.Tab) { CompleteCommand(); key.Handled = true; }
        };
        composerFrame.Add(_composer);
        _status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Text = StatusText() };
        _window.Add(_header, conversationFrame, composerFrame, _status);
        _window.Add(_completion);
        _help = new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(2), Width = Dim.Fill(2), Height = 1,
            Text = _locale == "en" ? "Enter send · Shift+Enter newline · / commands · F2 panel · F3 models · F4 settings" : "Enter enviar · Shift+Enter salto · / comandos · F2 panel · F3 modelos · F4 ajustes" };
        _window.Add(_help, _sidebar);
        _activity = new View { Id = "omni-activity", X = 2, Y = Pos.AnchorEnd(3), Width = 12, Height = 1,
            CanFocus = false, Visible = false };
        for (var index = 0; index < 11; index++)
            _activity.Add(new Label { X = index, Y = 0, Width = 1, Height = 1, Text = "▮" });
        _activityLabel = new Label { Id = "omni-heading", X = 16, Y = Pos.AnchorEnd(3), Width = 14, Height = 1,
            Text = Ui("Procesando…", "Processing…"), CanFocus = false, Visible = false };
        _window.Add(_activity, _activityLabel);
        ApplyTheme(_window);
        var initialWidth = 80;
        try { initialWidth = Console.WindowWidth; }
        catch (IOException) { /* consola redirigida o sin TTY: arranque en modo estrecho, el layout se corrige con el primer frame real. */ }
        _sidebarOpen = _sidebarSettings.Preferences.Visible ?? initialWidth >= _sidebarSettings.Preferences.TabbedMinWidth;
        _window.FrameChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
        RenderState();
        // El composer recibe el foco inicial: sin esto, el primer texto tecleado se pierde hasta que el usuario pulsa Tab.
        _composer.SetFocus();
        return _window;
    }

    private void ApplyResponsiveLayout()
    {
        if (_window is null || _sidebar is null) return;
        var layout = TuiLayoutModel.ForWidth(_window.Frame.Width, _sidebarOpen, _sidebarSettings.Preferences);
        _sidebar.Visible = layout.SidebarVisible;
        _sidebar.Width = layout.SidebarWidth;
        _sidebar.X = layout.Mode == TuiLayoutMode.Overlay && layout.SidebarVisible
            ? Pos.Center() : layout.SidebarVisible ? Pos.AnchorEnd(layout.SidebarWidth) : Pos.AnchorEnd(0);
        _mainColumnInset = layout.SidebarVisible && layout.Mode != TuiLayoutMode.Overlay ? layout.SidebarWidth : 0;
        if (_conversationFrame is not null) _conversationFrame.Width = Dim.Fill(_mainColumnInset);
        if (_composerFrame is not null) _composerFrame.Width = Dim.Fill(_mainColumnInset + 1);
        UpdateComposerHeight();
        if (_header is not null) _header.Width = Dim.Fill(_mainColumnInset + 2);
        if (_completion is not null) _completion.Width = Dim.Fill(_mainColumnInset + 2);
        if (_commandHelper is not null)
        {
            var height = Math.Min(Math.Min(10, _commandSuggestions.Length + 2), Math.Max(3, _window.Frame.Height - 9));
            _commandHelper.Width = Dim.Fill(_mainColumnInset + 1);
            _commandHelper.Height = height;
            _commandHelper.Y = Pos.AnchorEnd(5 + _composerRows + height);
        }
        if (_help is not null)
        {
            _help.Width = Dim.Fill(_mainColumnInset + 2);
            _help.Text = HelpText();
        }
        if (_status is not null) _status.Width = Dim.Fill(_mainColumnInset);
        RenderState();
    }

    private void ToggleSidebar()
    {
        _sidebarOpen = !_sidebarOpen;
        SaveSidebarSetting("Workspace", "sidebar.visible", _sidebarOpen ? "true" : "false", false);
        ApplyResponsiveLayout();
    }

    private void UpdateComposerHeight()
    {
        if (_composer is null || _composerFrame is null || _window is null) return;
        var width = Math.Max(1, _window.Frame.Width - _mainColumnInset - 8);
        var wanted = (_composer.Text ?? "").Replace("\r\n", "\n").Split('\n')
            .Sum(line => Math.Max(1, (Terminal.Gui.Text.StringExtensions.GetColumns(line, false) + width - 1) / width));
        _composerRows = Math.Clamp(wanted, 1, Math.Max(1, Math.Min(8, _window.Frame.Height / 3)));
        _composer.Height = _composerRows;
        _composerFrame.Height = _composerRows + 2;
        _composerFrame.Y = Pos.AnchorEnd(_composerRows + 5);
        if (_conversationFrame is not null) _conversationFrame.Height = Dim.Fill(_composerRows + 6);
        if (_commandHelper is not null) _commandHelper.Y = Pos.AnchorEnd(_composerRows + 5 + _commandHelper.Frame.Height);
    }

    private void UpdateAutocomplete()
    {
        if (_composer is null || _completion is null) return;
        var draft = _composer.Text?.ToString() ?? "";
        if (draft.Length == 0 || draft[0] is not ('/' or '@')) { _completion.Text = ""; HideCommandHelper(); return; }
        var names = ReadStringArray(_client.Query("commands", CancellationToken.None)?.Json, "commands")
            .Concat(new[] { "act", "context", "tools", "plan", "mode", "runmode", "ultracode",
                "cancel", "interrupt", "preferences", "models", "login", "sidebar", "fuente", "agents", "delegate", "agent", "join" });
        var suggestions = ComposerAutocomplete.Complete(draft,
            names, ReadStringArray(_client.Query("complete:" + draft[1..], CancellationToken.None)?.Json, "paths"));
        // Keep common entry points visible as the catalog grows. Typed prefixes retain
        // alphabetical completion, and /act remains the default first action.
        if (draft == "/") suggestions = suggestions.DistinctBy(suggestion => suggestion.Value)
            .OrderBy(suggestion => suggestion.Value switch { "/act" => 0, "/models" => 1, _ => 2 }).ToArray();
        _completion.Text = string.Join("   ", suggestions.Take(5).Select(suggestion => suggestion.Value));
        HideCommandHelper();
        if (draft[0] != '/' || draft.Any(char.IsWhiteSpace) || _overlay is not null || _window is null) return;
        _commandSuggestions = suggestions.Select(suggestion => suggestion.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (_commandSuggestions.Length == 0) return;
        var height = Math.Min(10, _commandSuggestions.Length + 2);
        height = Math.Min(height, Math.Max(3, _window.Frame.Height - 9));
        _commandHelper = new FrameView { Id = "omni-command-helper", X = 1, Y = Pos.AnchorEnd(5 + _composerRows + height),
            Width = Dim.Fill(_mainColumnInset + 1), Height = height, BorderStyle = LineStyle.None, CanFocus = false };
        _commandList = new ListView { Id = "omni-model-list", X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(1), ShowMarks = false, CanFocus = false };
        var nameWidth = Math.Min(22, _commandSuggestions.Max(value => value.Length) + 2);
        _commandList.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(
            _commandSuggestions.Select(value => value.PadRight(nameWidth) + CommandDescription(value))));
        _commandList.SelectedItem = 0;
        var commandList = _commandList;
        commandList.RowRender += (_, row) =>
        {
            if (row.Row == commandList.SelectedItem) row.RowAttribute = commandList.GetScheme().Focus;
        };
        _commandList.Accepted += (_, args) => { args.Handled = true; CompleteCommand(); };
        _commandHelper.Add(_commandList, new Label { Id = "omni-help", X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(2),
            Text = Ui("↑↓ elegir · Tab completar · Enter aceptar · Esc cerrar", "↑↓ select · Tab complete · Enter accept · Esc close") });
        ApplyTheme(_commandHelper, "#142636");
        _window.Add(_commandHelper);
        _completion.Visible = false;
    }

    private string CommandDescription(string command) => command switch
    {
        "/agents" => Ui("Inspeccionar agentes y lanes del Run", "Inspect Run agents and lanes"),
        "/delegate" => Ui("Encolar trabajo de sólo lectura con límites", "Queue bounded read-only work"),
        "/fanout" => Ui("Agrupar delegaciones en espera con fan-in Direct o Aggregate", "Group queued delegations with Direct or Aggregate fan-in"),
        "/agent" => Ui("Ejecutar, cancelar o evaluar un delegado", "Run, cancel or evaluate a delegate"),
        "/join" => Ui("Esperar resultados aceptados · All/Any/Quorum/Explicit", "Wait for accepted results · All/Any/Quorum/Explicit"),
        "/act" => Ui("Ejecutar cambios explícitos en archivos", "Execute explicit file changes"),
        "/mode" => Ui("Fijar el modo predeterminado del próximo Run", "Set the next Run's default mode"),
        "/runmode" => Ui("Cambiar el modo del Run activo", "Change the active Run's mode"),
        "/ultracode" => Ui("Autoridad adaptativa explícita con límites finitos; --plan=current cubre sólo el plan actual", "Explicit adaptive authority with finite limits; --plan=current covers only the current plan"),
        "/reasoning" => Ui("Preferencia persistente, selección del Run o impulso de un turno", "Persistent preference, Run selection, or one-turn boost"),
        "/models" => Ui("Seleccionar modelo", "Select model"),
        "/preferences" => Ui("Abrir configuración", "Open settings"),
        "/login" => Ui("Cuenta y conexión ChatGPT", "ChatGPT account and connection"),
        "/sidebar" => Ui("Mostrar u ocultar panel lateral", "Toggle workspace panel"),
        "/fuente" => Ui("Mostrar u ocultar ejemplos Markdown originales", "Show or hide original Markdown examples"),
        "/context" => Ui("Inspeccionar contexto del turno", "Inspect turn context"),
        "/tools" => Ui("Consultar herramientas disponibles", "Show available tools"),
        "/plan" => Ui("Consultar el plan actual", "Show current plan"),
        "/cancel" => Ui("Cancelar el turno activo", "Cancel active turn"),
        "/interrupt" => Ui("Interrumpir el turno activo", "Interrupt active turn"),
        _ => Ui("Comando del workspace", "Workspace command")
    };

    private void HideCommandHelper()
    {
        if (_commandHelper is not null && _window is not null)
        {
            _window.Remove(_commandHelper); _retiredOverlays.Add(_commandHelper);
        }
        _commandHelper = null; _commandList = null; _commandSuggestions = Array.Empty<string>();
        if (_completion is not null) _completion.Visible = true;
    }

    private void CompleteCommand()
    {
        if (_composer is null || _commandList?.SelectedItem is not int index || index >= _commandSuggestions.Length) return;
        var command = _commandSuggestions[index];
        _composer.Text = command + " "; _composer.MoveEnd();
        HideCommandHelper(); _composer.SetFocus();
    }

    private void SubmitComposer()
    {
        if (_composer is null) return;
        var input = _composer.Text?.ToString()?.Trim() ?? "";
        if (input == "/fuente")
        {
            _showMarkdownExamples = !_showMarkdownExamples;
            _renderedConversation = null;
            _composer.Text = "";
            RenderState();
            return;
        }
        if (input.Length == 0) return;
        if (_commandList?.SelectedItem is int index && index < _commandSuggestions.Length && input != _commandSuggestions[index])
        { CompleteCommand(); return; }
        HideCommandHelper();
        if (input.StartsWith("/act ", StringComparison.Ordinal))
        { if (StartModelTurn(input[5..], act: true)) { _composer.Text = ""; PollEvents(); } return; }
        if (input == "/mode")
        {
            var preference = _client.Query("modePreference", CancellationToken.None)?.Json ?? "{}";
            var authority = _client.Query("modeAuthority", CancellationToken.None)?.Json ?? "null";
            ShowMessage(Ui("Predeterminado del próximo Run: ", "Next Run default: ") + preference
                + Environment.NewLine + Ui("Autoridad del Run activo: ", "Active Run authority: ") + authority);
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/mode ", StringComparison.Ordinal))
        {
            var requested = input[6..].Trim().ToLowerInvariant();
            if (requested is not ("plan" or "act" or "orq"))
            { ShowMessage(Ui("Uso: /mode plan|act|orq", "Usage: /mode plan|act|orq")); _composer.Text = ""; return; }
            var preference = _client.Query("modePreference", CancellationToken.None)?.Json ?? "{}";
            if (!TryReadLong(preference, "revision", out var revision))
            { ShowMessage(Ui("No se pudo leer la revisión del modo", "Could not read the mode revision")); _composer.Text = ""; return; }
            SendTrustedModeCommand("mode.default.set", JsonObj.Field("mode", requested)
                + ",\"expectedRevision\":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/runmode ", StringComparison.Ordinal))
        {
            var requested = input[9..].Trim().ToLowerInvariant();
            if (requested is not ("plan" or "act" or "orq"))
            { ShowMessage(Ui("Uso: /runmode plan|act|orq", "Usage: /runmode plan|act|orq")); _composer.Text = ""; return; }
            SendTrustedModeCommand("run.mode.select", JsonObj.Field("mode", requested)
                + ",\"effort\":\"standard\"");
            _composer.Text = ""; PollEvents(); return;
        }
        if (input == "/reasoning")
        {
            var user = _client.Query("reasoningPreference", CancellationToken.None)?.Json ?? "null";
            var run = _client.Query("runReasoningPreference", CancellationToken.None)?.Json ?? "null";
            ShowMessage(Ui("Preferencia de usuario: ", "User preference: ") + user
                + Environment.NewLine + Ui("Selección del Run: ", "Run selection: ") + run);
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/reasoning ", StringComparison.Ordinal))
        {
            HandleReasoningCommand(input[11..].Trim());
            _composer.Text = ""; return;
        }
        if (input == "/ultracode off")
        {
            SendTrustedModeCommand("run.mode.revoke", "");
            _composer.Text = ""; PollEvents(); return;
        }
        if (input.StartsWith("/ultracode on ", StringComparison.Ordinal))
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var part = 2; part < parts.Length; part++)
            {
                var item = parts[part];
                var equals = item.IndexOf('=');
                if (!item.StartsWith("--", StringComparison.Ordinal) || equals <= 2
                    || !values.TryAdd(item[2..equals], item[(equals + 1)..]))
                { ShowMessage(Ui("Usa opciones explícitas --clave=valor", "Use explicit --key=value options")); _composer.Text = ""; return; }
            }
            var required = new[] { "mode", "modes", "agents", "depth", "turns", "tools", "seconds", "spend-usd" };
            if (values.TryGetValue("plan", out var planCoverage) && planCoverage != "current")
            { ShowMessage(Ui("Usa --plan=current para cubrir sólo la revisión actual", "Use --plan=current to cover only the current revision")); _composer.Text = ""; return; }
            if (required.Any(key => !values.ContainsKey(key))
                || values["mode"] is not ("plan" or "act" or "orq"))
            { ShowMessage(Ui("UltraCode requiere modo y todos los límites finitos", "UltraCode requires a mode and every finite limit")); _composer.Text = ""; return; }
            if (!int.TryParse(values["agents"], out var agents) || !int.TryParse(values["depth"], out var depth)
                || !int.TryParse(values["turns"], out var turns) || !int.TryParse(values["tools"], out var tools)
                || !long.TryParse(values["seconds"], out var seconds)
                || !decimal.TryParse(values["spend-usd"], System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var spend)
                || values["modes"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(mode => mode is not ("plan" or "act" or "orq")))
            { ShowMessage(Ui("Límites UltraCode inválidos", "Invalid UltraCode limits")); _composer.Text = ""; return; }
            var payload = JsonObj.Field("mode", values["mode"])
                + ",\"effort\":\"ultracode\",\"adaptive\":true," + JsonObj.Field("allowedModes", values["modes"])
                + ",\"maxAgents\":" + agents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxDepth\":" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxTurns\":" + turns.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxToolCalls\":" + tools.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxElapsedSeconds\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxSpendUsd\":" + spend.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (values.ContainsKey("plan")) payload += ",\"coverCurrentPlan\":true";
            SendTrustedModeCommand("run.mode.select", payload);
            _composer.Text = ""; PollEvents(); return;
        }
        if (input == "/preferences") { ShowPreferences(); _composer.Text = ""; return; }
        if (input == "/login") { ShowAccount(); _composer.Text = ""; return; }
        if (input == "/models") { ShowModelPicker(); _composer.Text = ""; return; }
        if (input == "/sidebar") { ToggleSidebar(); _composer.Text = ""; return; }
        if (input.StartsWith("/sidebar ", StringComparison.Ordinal))
        {
            var args = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 2 && args[1] == "settings") ShowSidebarSettings();
            else if (args.Length == 3 && args[1] == "focus")
            { _sidebarSelection = args[2]; _sidebarOpen = true; ApplyResponsiveLayout(); }
            else if (args.Length == 4 && args[1] is "user" or "workspace")
                SaveSidebarSetting(args[1] == "user" ? "User" : "Workspace", args[2], args[3], true);
            else ShowMessage("/sidebar settings · /sidebar focus <widget> · /sidebar user|workspace <key> <value|reset>");
            _composer.Text = ""; return;
        }
        if (input == "/agents")
        {
            _agents.Poll(_client);
            ShowMessage(AgentPresentation.Describe(_agents.Snapshot, _locale));
            _composer.Text = ""; return;
        }
        if (input == "/fanout" || input.StartsWith("/fanout ", StringComparison.Ordinal))
        {
            try
            {
                if (_client is not ITrustedUserActionClient trusted)
                    throw new InvalidOperationException("Trusted user client required");
                if (input.Length <= 8) throw new ArgumentException("/fanout {\"ownerExecutionId\":\"UUID\",\"delegationIds\":[\"UUID\",\"UUID\"],\"policy\":\"Direct|Aggregate\"} · /fanout {\"action\":\"replace\",\"groupId\":\"UUID\",\"previousDelegationId\":\"UUID\",\"replacementDelegationId\":\"UUID\"}");
                using var document = System.Text.Json.JsonDocument.Parse(input[8..]);
                var root = document.RootElement;
                var properties = root.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? root.EnumerateObject().ToArray() : Array.Empty<System.Text.Json.JsonProperty>();
                var replacing = root.ValueKind == System.Text.Json.JsonValueKind.Object
                    && root.TryGetProperty("action", out var action) && action.GetString() == "replace";
                var allowed = replacing
                    ? new HashSet<string>(["action", "groupId", "previousDelegationId", "replacementDelegationId"], StringComparer.Ordinal)
                    : new HashSet<string>(["ownerExecutionId", "delegationIds", "policy"], StringComparer.Ordinal);
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                    || properties.Any(property => !allowed.Contains(property.Name)))
                    throw new ArgumentException("Propiedades de Fan-out inválidas.");
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
                    || !allowed.SetEquals(properties.Select(property => property.Name)))
                    throw new ArgumentException("Se requieren los tres campos, cada uno una vez.");
                var payload = replacing
                    ? "{\"cmd\":\"fanout.replace_member\",\"groupId\":" + root.GetProperty("groupId").GetRawText()
                        + ",\"previousDelegationId\":" + root.GetProperty("previousDelegationId").GetRawText()
                        + ",\"replacementDelegationId\":" + root.GetProperty("replacementDelegationId").GetRawText() + "}"
                    : "{\"cmd\":\"fanout.create\"," + root.GetRawText()[1..^1] + "}";
                var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
                ShowMessage(ack.Error ?? ack.Outcome?.Reason ?? Ui("Fan-out registrado", "Fan-out recorded"));
                PollEvents();
            }
            catch (Exception failure) when (failure is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            { ShowMessage(failure.Message); }
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/agent inspect ", StringComparison.Ordinal))
        {
            var laneId = input[15..].Trim();
            var selected = Guid.TryParse(laneId, out _)
                ? _agents.Snapshot?.Lanes.SingleOrDefault(lane => lane.LaneId.Equals(laneId, StringComparison.OrdinalIgnoreCase))
                : null;
            if (selected is null)
                ShowMessage(Ui("Uso: /agent inspect <LaneId> · Lane desconocida", "Usage: /agent inspect <LaneId> · unknown lane"));
            else
                ShowMessage(AgentPresentation.Describe(_agents.Snapshot! with { Lanes = [selected] }, _locale));
            _composer.Text = "";
            return;
        }
        if (input == "/agent" || input.StartsWith("/agent ", StringComparison.Ordinal))
        {
            try
            {
                var parts = input.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !Guid.TryParse(parts[2], out _))
                    throw new ArgumentException("/agent run|cancel <DelegationId> · /agent accept|reject|rework <DelegationId> <ResultId> <razón>");
                if (parts[1] == "run")
                { if (StartModelTurn("", delegationId: parts[2])) _composer.Text = ""; return; }
                if (_client is not ITrustedUserActionClient trusted) throw new InvalidOperationException("Trusted user client required");
                var fields = JsonObj.Field("delegationId", parts[2]);
                var name = "delegation.cancel";
                (CancellationTokenSource Cancellation, Task Task)? background = null;
                if (parts[1] == "cancel" && Guid.TryParse(parts[2], out var cancellingId)
                    && _delegationTasks.TryGetValue(cancellingId.ToString("D"), out var activeBackground))
                    background = activeBackground;
                if (parts[1] != "cancel")
                {
                    if (parts.Length != 5 || !Guid.TryParse(parts[3], out _)) throw new ArgumentException("ResultId y razón requeridos");
                    var outcome = parts[1] switch { "accept" => "Accepted", "reject" => "Rejected", "rework" => "ReworkRequested", _ => throw new ArgumentException("Acción desconocida") };
                    fields += "," + JsonObj.Field("resultId", parts[3]) + "," + JsonObj.Field("outcome", outcome) + "," + JsonObj.Field("reason", parts[4]);
                    name = "delegation.disposition";
                }
                var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", name) + "," + fields + "}"), CancellationToken.None);
                if (parts[1] == "cancel" && ack.Status == "ok"
                    && ack.Outcome?.Kind is RuntimeCommandOutcomeKind.Accepted or RuntimeCommandOutcomeKind.NoOp)
                    background?.Cancellation.Cancel();
                ShowMessage(ack.Error ?? ack.Outcome?.Reason ?? Ui("Acción registrada", "Action recorded")); PollEvents();
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException) { ShowMessage(failure.Message); }
            _composer.Text = ""; return;
        }
        if (input == "/join" || input.StartsWith("/join ", StringComparison.Ordinal))
        {
            try
            {
                if (_client is not ITrustedUserActionClient trusted) throw new InvalidOperationException("Trusted user client required");
                string payload;
                if (input.StartsWith("/join cancel ", StringComparison.Ordinal))
                    payload = "{" + JsonObj.Field("cmd", "execution.join.cancel") + "," + JsonObj.Field("joinId", input[13..].Trim()) + "}";
                else
                {
                    if (input.Length <= 6) throw new ArgumentException("/join {\"ownerExecutionId\":\"UUID\",\"kind\":\"All\",\"members\":[\"UUID\"]} · /join cancel <JoinId>");
                    using var document = System.Text.Json.JsonDocument.Parse(input[6..]);
                    var fields = document.RootElement.EnumerateObject().ToArray();
                    if (fields.Any(f => f.Name == "cmd")) throw new ArgumentException("cmd no permitido");
                    payload = "{\"cmd\":\"execution.join\"," + string.Join(",", fields.Select(f => f.ToString())) + "}";
                }
                var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
                ShowMessage(ack.Error ?? ack.Outcome?.Reason ?? Ui("Join registrado", "Join recorded")); PollEvents();
            }
            catch (Exception failure) when (failure is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { ShowMessage(failure.Message); }
            _composer.Text = ""; return;
        }
        if (input == "/delegate" || input.StartsWith("/delegate ", StringComparison.Ordinal))
        {
            try
            {
                if (_client is not ITrustedUserActionClient trusted)
                    throw new InvalidOperationException(Ui("El cliente no permite delegación local", "Client cannot admit local delegation"));
                var request = input.Length > 10 ? AgentsJson.DecodeRequest(input[10..]) : null;
                if (request is null) throw new ArgumentException(Ui("Uso: /delegate ", "Usage: /delegate ")
                    + "{\"profileId\":\"UUID\",\"sourceEventId\":\"UUID de /agents\",\"objective\":\"Revisar\",\"selectedItemIds\":[],"
                    + "\"maximumPacketBytes\":8192,\"maxTurns\":2,\"maxToolCalls\":4,\"maxTokens\":4096,\"maxCostUsd\":0}");
                var ack = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()), CancellationToken.None);
                ShowMessage(ack.Status != "ok" ? ack.Error ?? Ui("Delegación rechazada", "Delegation rejected")
                    : ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Deferred ? "Deferred · " + ack.Outcome.Reason
                    : Ui("Delegación en cola. Usa /agents y /agent run <DelegationId> para ejecutarla respetando FIFO.",
                        "Delegation queued. Use /agents and /agent run <DelegationId> to dispatch in FIFO order."));
                PollEvents();
            }
            catch (Exception failure) when (failure is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            { ShowMessage(failure.Message); }
            _composer.Text = ""; return;
        }
        if (input is "/context" or "/tools" or "/plan")
        {
            var query = input switch { "/plan" => "workingState", _ => input.Substring(1) };
            var result = _client.Query(query, CancellationToken.None)?.Json ?? (_locale == "en" ? "No data available." : "Sin datos disponibles.");
            ShowMessage(result.Length <= 1800 ? result : result.Substring(0, 1800) + "…");
            _composer.Text = "";
            return;
        }
        if (input is "/cancel" or "/interrupt")
        {
            if (_turnBusy) _turnCancellation?.Cancel();
            else SendCommand(input == "/cancel" ? "run.cancel" : "run.interrupt", "{}");
            _composer.Text = ""; PollEvents(); return;
        }
        if (input[0] == '/')
        {
            if (!CommandLineParser.TryParse(input, out var invocation) || invocation is null) return;
            var ack = _client.Send(WireEnvelope.Command(Ids.NewV7(), CommandInvocationJson.Encode(invocation)), CancellationToken.None);
            if (ack.Status == "ok")
            {
                var outcome = _client.Query("commandOutcome", CancellationToken.None)?.Json ?? "{}";
                var text = JsonObj.Parse(outcome);
                if (text.TryGetValue("outcome", out var _)
                    && TryReadWorkflowOutcome(outcome, out var workflow, out var workflowCommandId))
                {
                    if (_turnHost is not null)
                    {
                        if (!StartModelTurn("", workflow: workflow, workflowCommandId: workflowCommandId)) return;
                    }
                    else ShowMessage(Ui("Este comando de workflow requiere el Host local.", "This workflow command requires the local Host."));
                }
                else if (text.TryGetValue("outcome", out _)
                    && TryReadOutcome(outcome, out var expanded))
                {
                    if (_turnHost is not null) { if (!StartModelTurn(expanded)) return; }
                    else SendCommand("session.input", "\"text\":" + ("\"" + JsonObj.Escape(expanded) + "\""));
                }
            }
            else ShowMessage("/" + invocation.Name + ": " + (ack.Error ?? "command failed"));
        }
        else if (input[0] == '@')
        {
            ShowMessage(_locale == "en" ? "Select a workspace suggestion; reference resolution is not available in this Host version." : "Selecciona una sugerencia del workspace; este Host aún no resuelve referencias @.");
        }
        else
        {
            if (_turnHost is not null) { if (!StartModelTurn(input)) return; }
            else SendCommand("session.input", "\"text\":" + ("\"" + JsonObj.Escape(input) + "\""));
        }
        _composer.Text = "";
        PollEvents();
    }

    private bool StartModelTurn(string input, bool act = false, string? resumeEscalation = null, string? resumeQuota = null,
        string? delegationId = null, WorkflowRequestDto? workflow = null, string? workflowCommandId = null)
    {
        if (_turnHost is null) return false;
        if (delegationId is not null) return StartDelegationTurn(delegationId);
        if (_turnBusy) { ShowMessage(Ui("Ya hay un turno procesando. Espera o usa /cancel.", "A turn is already processing. Wait or use /cancel.")); return false; }
        _turnCancellation?.Dispose();
        var cancellation = _turnCancellation = new CancellationTokenSource();
        _followConversationEnd = true;
        _turnBusy = true; _activityPhase = 0; _turnStatus = null; RenderState();
        _turnTask = Task.Run(async () =>
        {
            var messages = new List<string>();
            int code;
            try
            {
                Action<string> diagnostic = line =>
                {
                    if (messages.Count == 4) messages.RemoveAt(0);
                    messages.Add(OmniCliRuntime.RedactSensitive(line));
                };
                code = await (workflow is not null ? _turnHost.ExecuteWorkflowAsync(workflow,
                    workflowCommandId ?? throw new InvalidOperationException("Workflow command identity is missing."),
                    diagnostic, cancellation.Token)
                    : delegationId is not null ? _turnHost.ExecuteDelegationAsync(delegationId, diagnostic, cancellation.Token)
                    : resumeQuota is not null ? _turnHost.ResumeQuotaAsync(resumeQuota, diagnostic, cancellation.Token)
                    : resumeEscalation is not null ? _turnHost.ResumeEscalationAsync(resumeEscalation, diagnostic, cancellation.Token)
                    : act ? _turnHost.ExecuteActAsync(input, diagnostic, cancellation.Token)
                    : _turnHost.ExecuteAsync(input, diagnostic, cancellation.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { code = 2; }
            catch (Exception) { code = 1; messages.Add(Ui("El turno falló. Revisa omni doctor.", "Turn failed. Check omni doctor.")); }
            _uiActions.Enqueue(() =>
            {
                _turnBusy = false;
                _turnStatus = cancellation.IsCancellationRequested ? Ui("Cancelado", "Cancelled")
                    : code == 0 ? Ui("Listo", "Done") : code == 3 ? Ui("Requiere respuesta", "Input required") : Ui("Error", "Error");
                RefreshSelectedModel(); PollEvents();
                if (code != 0 && code != 3 && !cancellation.IsCancellationRequested)
                    ShowMessage(string.Join("\n", messages));
                if (_pendingEscalationResume is { } resume)
                {
                    _pendingEscalationResume = null;
                    if (!cancellation.IsCancellationRequested) StartModelTurn("", resumeEscalation: resume);
                }
                if (_pendingQuotaResume is { } quota)
                {
                    _pendingQuotaResume = null;
                    if (!cancellation.IsCancellationRequested) StartModelTurn("", resumeQuota: quota);
                }
            });
        });
        return true;
    }

    private bool StartDelegationTurn(string delegationId)
    {
        if (_turnHost is null || !Guid.TryParse(delegationId, out var parsed)) return false;
        var id = parsed.ToString("D");
        if (_delegationTasks.ContainsKey(id))
        {
            ShowMessage(Ui("Esa delegación ya está ejecutándose en segundo plano.",
                "That delegation is already running in the background."));
            return false;
        }
        var cancellation = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            var messages = new List<string>();
            var code = 1;
            try
            {
                code = await _turnHost.ExecuteDelegationAsync(id, line =>
                {
                    if (messages.Count == 4) messages.RemoveAt(0);
                    messages.Add(OmniCliRuntime.RedactSensitive(line));
                }, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { code = 2; }
            catch (Exception) { messages.Add(Ui("La delegación falló. Revisa /agents.", "Delegation failed. Check /agents.")); }
            _uiActions.Enqueue(() =>
            {
                if (_delegationTasks.TryGetValue(id, out var active)
                    && ReferenceEquals(active.Cancellation, cancellation))
                {
                    _delegationTasks.Remove(id);
                    cancellation.Dispose();
                }
                if (code != 0 && code != 2 && messages.Count > 0)
                    ShowMessage(string.Join("\n", messages));
                RenderState();
            });
        });
        _delegationTasks.Add(id, (cancellation, task));
        RenderState();
        return true;
    }

    private bool HasActiveModelWork => _turnBusy || _delegationTasks.Count > 0;

    private void PollEvents()
    {
        var identity = _client.Query("sessionIdentity", CancellationToken.None);
        if (identity is not null && JsonObj.Parse(identity.Json).TryGetValue("sessionId", out var session)
            && session != _cursorSession)
        {
            _cursorSession = session; _lastSequence = 0; _observedModel = null;
            var empty = ClientState.Empty();
            // Keep the visible transcript across fresh Runs; reset only session-local UI state.
            _state = new ClientState(_state.Header, _state.Conversation, empty.Sidebar,
                _state.Composer, empty.StatusLine, empty.Overlays, empty.Connection);
        }
        foreach (var envelope in _client.SubscribeSince(_lastSequence + 1))
        {
            ObserveModel(envelope);
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
        RefreshSidebarData();
        RenderState();
        ShowCurrentInteraction();
    }

    private ClientState RefreshState()
    {
        if (_client.Query("sessionIdentity", CancellationToken.None) is { } identity)
            JsonObj.Parse(identity.Json).TryGetValue("sessionId", out _cursorSession);
        var state = ClientState.Empty();
        foreach (var envelope in _client.SubscribeSince(0))
        {
            ObserveModel(envelope);
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
        RefreshSidebarData();
        return state;
    }

    private void RefreshSidebarData()
    {
        if (DateTimeOffset.UtcNow - _sidebarSettingsPolledAt > TimeSpan.FromSeconds(2))
        {
            var revision = _sidebarSettings.Revision; RefreshSidebarPreferences();
            if (_sidebarSettings.Revision != revision)
            {
                _sidebarOpen = _sidebarSettings.Preferences.Visible ?? _sidebarOpen;
                ApplyResponsiveLayout();
            }
        }
        _sidebarProjection.Activate(_cursorSession);
        _agents.Activate(_cursorSession);
        _files.Activate(_cursorSession);
        _observability.Activate(_cursorSession);
        if (_sidebarProjection.Snapshot is null || _sidebarProjection.Snapshot.BasedOnJournalSequence != _lastSequence)
            _sidebarProjection.Poll(_client);
        var agentsPollDue = DateTimeOffset.UtcNow - _agentsPolledAt >= TimeSpan.FromMilliseconds(500);
        if (_agents.Snapshot is null || _agents.Snapshot.BasedOnJournalSequence != _lastSequence
            || HasActiveModelWork && agentsPollDue)
        {
            _agents.Poll(_client);
            _agentsPolledAt = DateTimeOffset.UtcNow;
        }
        if (_files.Snapshot is null || _files.Snapshot.BasedOnJournalSequence != _lastSequence) _files.Poll(_client);
        // Only reads existing observations. No authentication, quota refresh or generation here.
        if (_sidebarOpen || _observability.Snapshot is null) _observability.Poll(_client);
    }

    private void ObserveModel(WireEnvelope envelope)
    {
        if (envelope.MessageType != MessageTypes.Event) return;
        var fields = JsonObj.Parse(envelope.PayloadJson);
        if (fields.GetValueOrDefault("conversationScope") is not ("child" or "ambiguous")
            && fields.GetValueOrDefault("type") == "model_step.started"
            && fields.GetValueOrDefault("modelId") is { Length: > 0 } model)
            _observedModel = model;
    }

    private void RenderState()
    {
        RenderActivity();
        RenderConversation();
        if (_header is not null) _header.Text = HeaderText();
        if (_help is not null) _help.Text = HelpText();
        if (_status is not null) _status.Text = StatusText();
        RenderSidebar();
    }

    private void RenderConversation()
    {
        if (_conversation is null || _renderingConversation) return;
        _renderingConversation = true;
        try
        {
        // Polling must not reload identical text: doing so resets selection and history scroll.
        var codeWidth = Math.Max(1, _conversation.Viewport.Width - 1);
        var signature = codeWidth + "\u001d" + string.Join("\u001f", _state.Conversation.Blocks.Select(b => b.Role + "\u001e" + b.ToolName + "\u001e" + b.Text));
        if (_renderedConversation == signature) return;
        _renderedConversation = signature;
        if (_state.Conversation.Blocks.Count == 0) { _conversation.Text = ConversationText(); return; }
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var scroll = _conversation.VerticalScrollBar;
        var follow = _followConversationEnd;
        var oldRow = _conversation.CurrentRow;
        var oldColumn = _conversation.CurrentColumn;
        var oldViewport = _conversation.Viewport;
        var oldScroll = scroll.Value;
        // TextView.Load consults the old insertion point while replacing its cell model.
        // Reflow may shorten a row; reset before replacement, then restore the reader below.
        _conversation.InsertionPoint = System.Drawing.Point.Empty;
        _conversation.LoadStyled(ConversationPresentation.RenderCards(_state.Conversation.Blocks, _locale, codeWidth,
            compactExamples: !_showMarkdownExamples), codeWidth, noColor);
        if (follow) _conversation.MoveEnd();
        else
        {
            _conversation.InsertionPoint = new System.Drawing.Point(oldColumn, Math.Min(oldRow, _conversation.GetAllLines().Count - 1));
            _conversation.Viewport = new System.Drawing.Rectangle(oldViewport.Location, _conversation.Viewport.Size);
            scroll.Value = oldScroll;
        }
        }
        finally { _renderingConversation = false; }
    }

    private string ConversationText() => _state.Conversation.Blocks.Count == 0
        ? (_locale == "en" ? "OmniCore\n\nReady when you are.\nWrite a message or use / to explore commands." : "OmniCore\n\nListo para trabajar.\nEscribe un mensaje o usa / para explorar comandos.")
        : string.Join("\n\n", _state.Conversation.Blocks.Select(block => block.Role switch
    {
        ConversationRole.User => "◉ " + block.Text,
        ConversationRole.Assistant => "◆ " + block.Text,
        ConversationRole.Tool => "● " + (block.ToolName ?? "tool") + " · " + block.Text,
        ConversationRole.Interaction => "! " + block.Text,
        _ => "○ " + block.Text,
    }));

    private string HeaderText()
    {
        var path = _state.Header.WorkingDirectory.Length == 0 ? Environment.CurrentDirectory : _state.Header.WorkingDirectory;
        var width = Math.Max(12, (_window?.Frame.Width ?? 80) - 4 - _mainColumnInset);
        var workspace = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrWhiteSpace(workspace)) workspace = path;
        var title = _sidebarProjection.Snapshot?.Title is { Length: > 0 } sessionTitle ? sessionTitle
            : _state.Conversation.Blocks.FirstOrDefault(block => block.Role == ConversationRole.User)?.Text.Split('\n')[0]
            ?? Ui("Nueva sesión", "New session");
        return SessionHeading(workspace, title, _observedModel ?? _selectedModel ?? Ui("Automático · sin turno", "Automatic · no turn"), width);
    }

    internal static string SessionHeading(string workspace, string title, string model, int width)
    {
        width = Math.Max(1, width);
        var project = FitText(workspace, Math.Max(1, Math.Min(20, width / 5)));
        var projectWidth = Terminal.Gui.Text.StringExtensions.GetColumns(project, false);
        var modelText = FitText("Modelo: " + model, Math.Max(1, width - projectWidth - 6));
        var modelWidth = Terminal.Gui.Text.StringExtensions.GetColumns(modelText, false);
        var titleWidth = Math.Clamp(width - projectWidth - modelWidth - 6, 0, 32);
        return FitText(project + " │ " + (titleWidth > 0 ? FitText(title, titleWidth) + " · " : "") + modelText, width);
    }

    private static string FitText(string text, int width)
    {
        if (width <= 0) return "";
        if (Terminal.Gui.Text.StringExtensions.GetColumns(text, false) <= width) return text;
        var result = new System.Text.StringBuilder();
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var used = 0;
        while (elements.MoveNext())
        {
            var value = elements.GetTextElement();
            var size = Terminal.Gui.Text.StringExtensions.GetColumns(value, false);
            if (used + size >= width) break;
            result.Append(value); used += size;
        }
        return result + "…";
    }

    private string HelpText() => CompactHelp(_state.StatusLine.Mode, _locale,
        Math.Max(1, (_window?.Frame.Width ?? 80) - _mainColumnInset - 4));

    internal static string CompactHelp(string mode, string locale, int width)
    {
        var lead = mode + (locale == "en" ? " · Enter send" : " · Enter enviar");
        var variants = locale == "en"
            ? new[] { " · ⇧Enter newline · / help · F2 panel · F3 model · F4 settings", " · ⇧↵ · / help · F2 panel · F3 model · F4", " · / help · F3", " · /" }
            : new[] { " · ⇧Enter salto · / ayuda · F2 panel · F3 modelo · F4 ajustes", " · ⇧↵ · / ayuda · F2 panel · F3 modelo · F4", " · / ayuda · F3", " · /" };
        return variants.Select(tail => lead + tail).FirstOrDefault(value => Terminal.Gui.Text.StringExtensions.GetColumns(value, false) <= width)
            ?? FitText(lead, width);
    }

    internal static string AbbreviatePath(string path, int width)
    {
        width = Math.Max(1, width);
        if (path.Length <= width) return path;
        if (width < 5) return "…" + path[^Math.Max(0, width - 1)..];
        var prefix = Math.Min(3, width / 3);
        return path[..prefix] + "…" + path[^(width - prefix - 1)..];
    }

    private string StatusText()
    {
        var presentation = StatusLinePresentation.From(_state.StatusLine);
        var width = Math.Max(1, (_window?.Frame.Width ?? 80) - 4 - _mainColumnInset);
        var right = _turnBusy ? "" : _delegationTasks.Count > 0
            ? Ui($"{_delegationTasks.Count} delegación(es) en segundo plano", $"{_delegationTasks.Count} background delegation(s)")
            : _turnStatus ?? presentation.Right;
        return "  " + FitText(right, width);
    }

    private void RenderSidebar()
    {
        if (_sidebarContent is null) return;
        var widgets = SessionSidebarPresentation.Build(_sidebarProjection.Snapshot, _observability.Snapshot,
            _observedModel ?? _selectedModel ?? Ui("Automático · sin turno", "Automatic · no turn"),
            _state.StatusLine.Mode, _locale, _sidebarExpanded).ToList();
        if (!_sidebarExpanded && _sidebarSettings.Preferences.Widgets?.Any(p => p.Value.Expanded) == true)
        {
            var expanded = SessionSidebarPresentation.Build(_sidebarProjection.Snapshot, _observability.Snapshot,
                _observedModel ?? _selectedModel ?? Ui("Automático · sin turno", "Automatic · no turn"), _state.StatusLine.Mode, _locale, true);
            for (var index = 0; index < widgets.Count; index++)
                if (_sidebarSettings.Preferences.Widgets.GetValueOrDefault(widgets[index].Id)?.Expanded == true)
                    widgets[index] = expanded.First(w => w.Id == widgets[index].Id);
        }
        var agentWidget = AgentPresentation.Build(_agents.Snapshot, _locale, _sidebarExpanded
            || _sidebarSettings.Preferences.Widgets?.GetValueOrDefault("core.agents")?.Expanded == true);
        _sidebarWidgets = widgets.Concat(new[] { agentWidget, _files.Widget(_locale) }).ToArray();
        var layout = TuiLayoutModel.ForWidth(_window?.Frame.Width ?? 80, _sidebarOpen, _sidebarSettings.Preferences);
        _sidebarComposition = OmniCore.Client.SidebarComposition.Build(_sidebarWidgets, _state, _sidebarSettings.Preferences,
            layout.Mode, Math.Max(1, (_window?.Frame.Height ?? 25) - 12), _sidebarSelection, _sidebarExpanded);
        _sidebarSelection = _sidebarComposition.SelectedId;
        var pinned = _sidebarComposition.Pinned.Select(item => (item.Widget, item.Model is ListWidgetModel list
            ? (WidgetModel)(list with { Rows = list.Rows.Select(row => row with
                { Text = FitText(row.Text, Math.Max(1, layout.SidebarWidth - 6)) }).ToArray() }) : item.Model)).ToArray();
        _sidebarSession?.Render(pinned);
        _sidebarContent.Render(_sidebarComposition.Body);
        if (_sidebarNavigation is not null) _sidebarNavigation.Text = (_sidebarComposition.Navigation.FirstOrDefault(w => w.Id == _sidebarSelection)?.Title ?? "")
            + (layout.Mode == TuiLayoutMode.Tabbed ? " · tab" : "");
        if (_sidebarAttention is not null) _sidebarAttention.Text = _sidebarSettings.Diagnostics.Count > 0 ? Ui("! Configuración inválida", "! Invalid settings")
            : _sidebarComposition.AttentionIds.Count > 0 ? "! " + string.Join(" · ", _sidebarComposition.AttentionIds.Select(id => id.Replace("core.", "")))
            : _sidebarComposition.Collapsed > 0 ? Ui("Detalles colapsados · ‹ › inspeccionar", "Details collapsed · ‹ › inspect") : "";
    }

    private void NavigateSidebar(int delta)
    {
        var navigation = _sidebarComposition?.Navigation;
        if (navigation is null || navigation.Count == 0) return;
        var index = navigation.ToList().FindIndex(w => w.Id == _sidebarSelection);
        _sidebarSelection = navigation[(index + delta + navigation.Count) % navigation.Count].Id;
        // Navigation is an explicit inspection request, including widgets hidden by configuration.
        RenderSidebar(); _sidebarContent?.SelectWidget(_sidebarSelection);
    }

    private void RefreshSidebarPreferences()
    {
        _sidebarSettingsPolledAt = DateTimeOffset.UtcNow;
        var query = _client.Query("sidebarSettings", CancellationToken.None);
        if (query is not null && SidebarPreferencesJson.Decode(query.Json) is { } preferences) _sidebarSettings = preferences;
    }

    private bool SaveSidebarSetting(string scope, string key, string value, bool notice)
    {
        if (_client is not ITrustedUserActionClient trusted) { if (notice) ShowMessage("Trusted user client required"); return false; }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sidebar.configure\","
            + JsonObj.Field("scope", scope) + "," + JsonObj.Field("key", key) + "," + JsonObj.Field("value", value)
            + "," + JsonObj.Field("revision", _sidebarSettings.Revision) + "}"), CancellationToken.None);
        RefreshSidebarPreferences();
        if (ack.Status != "ok") { if (notice) ShowMessage(ack.Error ?? "Settings rejected"); return false; }
        if (key == "sidebar.visible") _sidebarOpen = _sidebarSettings.Preferences.Visible ?? _sidebarOpen;
        ApplyResponsiveLayout(); return true;
    }

    private void ShowSidebarSettings()
    {
        if (_window is null) return;
        CloseOverlay(); RefreshSidebarPreferences();
        var frame = OverlayFrame(Ui("Panel · ajustes", "Sidebar · settings"), 19);
        var scope = "Workspace";
        var scopeButton = new Button { X = 2, Y = 2, Text = "Scope: Workspace" };
        var mode = new Button { X = Pos.Right(scopeButton) + 1, Y = 2, Text = "Mode: " + _sidebarSettings.Preferences.Mode };
        var list = new ListView { Id = "omni-model-list", X = 2, Y = 4, Width = Dim.Fill(2), Height = Dim.Fill(6) };
        var notice = new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(2), Width = Dim.Fill(2), Height = 1 };
        var ids = _sidebarWidgets.Select(w => w.Id).ToArray();
        void Update()
        {
            var selected = list.SelectedItem;
            list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(ids.Select(id =>
            {
                var setting = _sidebarSettings.Preferences.Widgets?.GetValueOrDefault(id) ?? new();
                return id + " · " + setting.Visible + " · " + (setting.Expanded ? "expanded" : "compact") + " · "
                    + (setting.Priority?.ToString() ?? "default");
            })));
            if (ids.Length > 0) list.SelectedItem = Math.Clamp(selected ?? 0, 0, ids.Length - 1);
            notice.Text = _sidebarSettings.Diagnostics.Count > 0 ? string.Join(" · ", _sidebarSettings.Diagnostics)
                : Ui("User < Project confiable < Workspace · sesión fija arriba", "User < trusted Project < Workspace · session pinned");
            mode.Text = "Mode: " + _sidebarSettings.Preferences.Mode;
        }
        string? Selected() => list.SelectedItem is { } index && index >= 0 && index < ids.Length ? ids[index] : null;
        void Save(string key, string value)
        {
            if (!SaveSidebarSetting(scope, key, value, false)) notice.Text = Ui("No se guardó · revisa YAML o recarga", "Not saved · check YAML or reload");
            else Update();
        }
        scopeButton.Accepted += (_, _) => { scope = scope == "Workspace" ? "User" : "Workspace"; scopeButton.Text = "Scope: " + scope; };
        mode.Accepted += (_, _) =>
        {
            var values = new[] { "auto", "stacked", "tabbed", "overlay" };
            Save("sidebar.mode", values[(Array.IndexOf(values, _sidebarSettings.Preferences.Mode) + 1) % values.Length]);
        };
        var visible = new Button { X = 2, Y = Pos.AnchorEnd(5), Text = Ui("Visibilidad", "Visibility") };
        var expand = new Button { X = Pos.Right(visible) + 1, Y = Pos.AnchorEnd(5), Text = Ui("Expandir", "Expand") };
        var reset = new Button { X = Pos.Right(expand) + 1, Y = Pos.AnchorEnd(5), Text = "Reset" };
        visible.Accepted += (_, _) =>
        {
            if (Selected() is not { } id || id == "core.session") return;
            var value = _sidebarSettings.Preferences.Widgets?.GetValueOrDefault(id)?.Visible ?? "auto";
            Save("widgets." + id + ".visible", value == "auto" ? "true" : value == "true" ? "false" : "auto");
        };
        expand.Accepted += (_, _) => { if (Selected() is { } id) Save("widgets." + id + ".expanded", _sidebarSettings.Preferences.Widgets?.GetValueOrDefault(id)?.Expanded == true ? "false" : "true"); };
        reset.Accepted += (_, _) => { if (Selected() is { } id) foreach (var key in new[] { "visible", "expanded", "priority" }) Save("widgets." + id + "." + key, "reset"); };
        var up = new Button { X = 2, Y = Pos.AnchorEnd(4), Text = Ui("Prioridad +", "Priority +") };
        var down = new Button { X = Pos.Right(up) + 1, Y = Pos.AnchorEnd(4), Text = Ui("Prioridad −", "Priority −") };
        void Priority(int delta) { if (Selected() is { } id) Save("widgets." + id + ".priority", Math.Clamp((_sidebarSettings.Preferences.Widgets?.GetValueOrDefault(id)?.Priority ?? _sidebarWidgets.First(w => w.Id == id).DefaultPriority) + delta, -1000, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        up.Accepted += (_, _) => Priority(10); down.Accepted += (_, _) => Priority(-10);
        frame.Add(scopeButton, mode, list, visible, expand, reset, up, down, notice);
        Update(); ApplyTheme(frame); _overlay = frame; _window.Add(frame); list.SetFocus();
    }

    private void ShowFileDiff(string id)
    {
        if (_window is null) return;
        var query = _client.Query("diff.open:" + id, CancellationToken.None);
        var diff = query is null ? null : FilesJson.DecodeDiff(query.Json);
        if (diff is null || diff.SessionId != _cursorSession || diff.EffectId != id) { ShowMessage(Ui("Diff no disponible", "Diff unavailable")); return; }
        CloseOverlay();
        var frame = OverlayFrame("Diff · " + diff.Path, _window.Frame.Height - 5);
        var content = new SidebarView { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(2), WordWrap = false, ShowRoleGlyphs = false };
        var model = FileDiffPresentation.Build(diff);
        content.Render([(new ChangedFilesSidebarWidget(new([])), model)]);
        frame.Add(content, new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(1), Text = Ui("Efecto registrado · no incluye ediciones posteriores · Esc cerrar", "Recorded effect · excludes later edits · Esc close") });
        _overlay = frame; _window.Add(frame); ApplyTheme(frame); content.SetFocus();
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
        overlay.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Height = 2,
            Text = interaction.Subject.Length == 0 ? interaction.Kind : interaction.Subject });
        Button? defaultButton = null;
        for (var index = 0; index < interaction.Options.Count; index++)
        {
            var optionId = interaction.OptionIds.ElementAtOrDefault(index) ?? "";
            // Sin IsDefault: en Terminal.Gui 2.6 un botón predeterminado suprime el Accept de
            // cualquier otro botón del contenedor (Enter sobre «Permitir una vez» dispararía
            // «Denegar»), lo que traiciona la intención visible del foco. La opción segura llega
            // por el foco inicial y por Esc (OnApplicationKeyDown → RespondDefault), no por flag.
            var button = new Button { X = 2, Y = 4 + index, Text = interaction.Options[index] };
            button.Accepted += (_, _) => RespondChoice(interaction, optionId);
            overlay.Add(button);
            if (optionId == interaction.DefaultOptionId) defaultButton = button;
        }
        overlay.Add(new Label { Id = "omni-help", X = 2, Y = 5 + interaction.Options.Count, Text = _locale == "en" ? "Esc: default / deny" : "Esc: opción predeterminada / denegar" });
        _overlay = overlay;
        ApplyTheme(overlay);
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
        var y = 2;
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
                        // La exclusividad de la selección única se cablea a ValueChanged: Accepted no
                        // se dispara al marcar con espacio (Checkbox de Terminal.Gui 2.6).
                        checkbox.ValueChanged += (_, args) =>
                        {
                            if (args.NewValue != CheckState.Checked) return;
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
        // Sin IsDefault: Enter activa el botón enfocado (predecible). Enviar/Cancelar responden al
        // Enter del usuario solo cuando el usuario los enfoca deliberadamente; Esc cancela.
        var submit = new Button { X = Pos.AnchorEnd(22), Y = Math.Min(y + 1, 20), Text = model.SubmitLabel };
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
        ApplyTheme(overlay);
        _window!.Add(overlay);
        overlay.SetFocus();
    }

    private FrameView OverlayFrame(string title, int height)
    {
        HideCommandHelper();
        var frame = new FrameView { Id = "omni-panel", X = Pos.Center(), Y = Pos.Center(), Width = Dim.Percent(80),
            Height = Math.Min(height + 2, Math.Max(6, (_window?.Frame.Height ?? 25) - 2)),
            Title = " " + title + " ", BorderStyle = LineStyle.None, CanFocus = true };
        frame.Add(new Label { Id = "omni-menu-title", X = 2, Y = 0, Width = Dim.Fill(2), Height = 1, Text = title });
        ApplyTheme(frame);
        return frame;
    }

    private static void ApplyTheme(View view, string surface = "#061822", bool dimmed = false)
    {
        // Framework defaults invert whole titles and edit fields. Keep a quiet surface;
        // focus remains visible through the cursor and cyan hotkeys, never color alone.
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        if (view.Id == "omni-composer") surface = "#293648";
        else if (view.Id == "omni-composer-accent") surface = "#B47CE7";
        else if (view.Id == "omni-panel") surface = "#202C3B";
        else if (view.Id == "omni-menu-title") surface = "#344559";
        else if (view.Id == "omni-model-list") surface = "#142636";
        else if (view.Id == "omni-menu-action") surface = "#293E51";
        else if (view.Id == "omni-model-palette" || view.Id == "omni-palette-search" || view.Id == "omni-palette-action") surface = "#061822";
        var background = noColor ? Color.None : new Color(surface);
        var foreground = view.Id == "omni-help" ? "#B9CBDF"
            : view.Id == "omni-heading" ? "#85E6DF" : "#F4F7FB";
        if (dimmed) foreground = "#405666";
        var primary = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(foreground), background);
        var accent = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(dimmed ? "#405666" : "#67D4D0"), background);
        var focus = view is Button or ListView && !noColor
            ? new Terminal.Gui.Drawing.Attribute(new Color("#FFFFFF"), new Color("#3D5068")) : primary;
        if (view.Id == "omni-model-list" && !noColor)
            focus = new Terminal.Gui.Drawing.Attribute(new Color("#061822"), new Color("#67D4D0"));
        if (view is Button button)
        {
            button.NoDecorations = true;
            button.ShadowStyle = ShadowStyles.None;
        }
        view.SetScheme(new Scheme { Normal = primary, Focus = focus, Editable = primary,
            HotNormal = accent, HotFocus = view is Button or ListView ? focus : accent });
        foreach (var child in view.SubViews) ApplyTheme(child, surface, dimmed);
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
        var ack = _client.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        if (ack.Status != "ok") { ShowMessage(ack.Error ?? Ui("Respuesta rechazada", "Response rejected")); return; }
        CloseOverlay(); PollEvents();
        if (interaction.Kind == "BudgetExceeded" && optionId == "allow_quota")
        {
            if (_turnBusy) _pendingQuotaResume = interaction.Id;
            else StartModelTurn("", resumeQuota: interaction.Id);
        }
        if (interaction.Kind == "ModelRouteConsent" && optionId == "allow_route"
            && _turnHost?.HasEscalationForInteraction(interaction.Id) == true)
        {
            // The overlay can be drawn by polling just before the originating task returns 3.
            // Defer the callback until that owned invocation reaches its checkpoint.
            if (_turnBusy) _pendingEscalationResume = interaction.Id;
            else StartModelTurn("", resumeEscalation: interaction.Id);
        }
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

    private string Ui(string spanish, string english) => _locale == "en" ? english : spanish;

    private void ShowPreferences()
    {
        if (_window is null || _overlay is not null) return;
        var frame = OverlayFrame(Ui("Configuración", "Settings"), 12);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = Ui("Modelos, políticas y cuenta", "Models, policies and account") });
        var models = new Button { X = 2, Y = 4, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = Ui("Modelos y permisos", "Models and permissions") };
        models.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(); };
        var account = new Button { X = 2, Y = 6, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = Ui("Cuenta ChatGPT · login", "ChatGPT account · login") };
        account.Accepted += (_, _) => { CloseOverlay(); ShowAccount(); };
        var close = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = Ui("Cerrar", "Close") };
        close.Accepted += (_, _) => CloseOverlay();
        frame.Add(models, account, close); ApplyTheme(frame);
        _overlay = frame; _window.Add(frame); models.SetFocus();
    }

    private void ShowAccount()
    {
        if (_window is null || _overlay is not null) return;
        try
        {
            _account ??= _accountOverride ?? new TuiAccountHost();
            var status = _account.Status();
            var frame = OverlayFrame(Ui("Cuenta ChatGPT", "ChatGPT account"), 14);
            frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = status.LoggedIn
                ? Ui("Cuenta: ", "Account: ") + status.AccountIdMasked + (status.Expired ? Ui(" · sesión expirada", " · session expired") : Ui(" · conectada", " · connected"))
                : Ui("Sin sesión iniciada", "Not signed in") });
            frame.Add(new Label { X = 2, Y = 3, Width = Dim.Fill(2), Text = Ui("No necesitas introducir una API key aquí.", "No API key is required here.") });
            var browser = new Button { X = 2, Y = 5, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = Ui("Iniciar sesión · enlace de navegador", "Sign in · browser link") };
            browser.Accepted += (_, _) => StartLogin(false);
            var device = new Button { X = 2, Y = 7, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = Ui("Iniciar sesión · código de dispositivo", "Sign in · device code") };
            device.Accepted += (_, _) => StartLogin(true);
            frame.Add(browser, device);
            if (status.LoggedIn)
            {
                var logout = new Button { X = 2, Y = 9, Text = Ui("Cerrar sesión…", "Sign out…") };
                logout.Accepted += (_, _) => ConfirmLogout(); frame.Add(logout);
            }
            var back = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = Ui("Volver", "Back") };
            back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences(); };
            frame.Add(back); ApplyTheme(frame); _overlay = frame; _window.Add(frame); browser.SetFocus();
        }
        catch (Exception) { ShowMessage(Ui("No se pudo leer la sesión. Revisa omni doctor.", "Could not read the session. Check omni doctor.")); }
    }

    private void ConfirmLogout()
    {
        CloseOverlay();
        var frame = OverlayFrame(Ui("¿Cerrar sesión ChatGPT?", "Sign out of ChatGPT?"), 8);
        var confirm = new Button { X = 2, Y = 3, Text = Ui("Sí, cerrar sesión", "Yes, sign out") };
        confirm.Accepted += (_, _) =>
        {
            try { _account!.Logout(); CloseOverlay(); ShowAccount(); }
            catch (Exception) { ShowMessage(Ui("No se pudo cerrar la sesión.", "Could not sign out.")); }
        };
        var cancel = new Button { X = 2, Y = 5, Text = Ui("Cancelar", "Cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowAccount(); };
        frame.Add(confirm, cancel); ApplyTheme(frame); _overlay = frame; _window!.Add(frame); cancel.SetFocus();
    }

    private void StartLogin(bool deviceCode)
    {
        CloseOverlay();
        var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        var frame = OverlayFrame(Ui("Login ChatGPT", "ChatGPT login"), 16);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = Ui("Abre el enlace y completa la autorización. Esc cancela.", "Open the link and authorize. Esc cancels.") });
        // Read-only, selectable OAuth instructions; no editing or document model is needed.
#pragma warning disable CS0618
        var progress = new TextView { X = 2, Y = 4, Width = Dim.Fill(2), Height = Dim.Fill(4), ReadOnly = true, WordWrap = true,
            Text = Ui("Preparando autorización…", "Preparing authorization…") };
#pragma warning restore CS0618
        var cancel = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = Ui("Cancelar", "Cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowAccount(); };
        frame.Add(progress, cancel); ApplyTheme(frame); _overlay = frame; _window!.Add(frame); cancel.SetFocus();
        void Update(Action action) => _uiActions.Enqueue(() => { if (ReferenceEquals(_overlay, frame) && !cancellation.IsCancellationRequested) action(); });
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _account!.LoginAsync(deviceCode, text => Update(() => progress.Text = text), cancellation.Token);
                Update(() => { CloseOverlay(); ShowMessage(result.LoggedIn ? Ui("Sesión iniciada correctamente.", "Successfully signed in.") : Ui("No se inició la sesión.", "Sign-in did not complete.")); });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception) { Update(() => { CloseOverlay(); ShowMessage(Ui("No se pudo iniciar sesión. Reintenta o utiliza código de dispositivo; revisa omni doctor.", "Sign-in failed. Retry or use device code; check omni doctor.")); }); }
            finally { cancellation.Dispose(); }
        });
    }

    private void ShowModelPicker(int page = 0, bool refreshCatalog = true, string query = "")
    {
        if (_window is null || _overlay is not null) return;
        _policies.ReloadModels();
        if (refreshCatalog) _catalogFresh = false;
        var available = _policies.Models.Where(m => _policies.IsVisibleInPicker(m.ProviderId, m.Id)).ToArray();
        var models = available;
        var frame = OverlayFrame(Ui("Seleccionar modelo", "Select model"), Math.Clamp(available.Length + 9, 12, 20));
        frame.Id = "omni-model-palette";
        frame.Width = Dim.Percent(66);
        frame.ViewportSettings |= ViewportSettingsFlags.Transparent;
        frame.SubViews.Single(view => view.Id == "omni-menu-title").Width = Dim.Fill(10);
        var feedback = new Label { X = 2, Y = 1, Width = Dim.Fill(4),
            Text = models.Length == 0 ? Ui("Sin modelos visibles. Revisa F4 > Modelos.", "No visible models. Check F4 > Models.")
                : "" };
        frame.Add(feedback);
        var searchLabel = new Label { Id = "omni-heading", X = 2, Y = 2, Width = 8, Text = Ui("Buscar", "Search") };
        var search = new TextField { Id = "omni-palette-search", X = 10, Y = 2, Width = Dim.Fill(15), Text = query };
        var searchHint = new Label { Id = "omni-help", X = Pos.AnchorEnd(12), Y = 2, Width = 10, Text = "Ctrl+F" };
        var list = new ListView { Id = "omni-model-list", X = 2, Y = 4, Width = Dim.Fill(4), Height = Dim.Fill(4),
            CanFocus = models.Length > 0, ShowMarks = false };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(models.Select(model =>
            (_selectedModel == model.Id ? " ✓  " : "    ") + (_policies.DisplayName(model.Id) ?? model.Id) + "   ·   " + model.ProviderId)));
        var position = new Label { Id = "omni-heading", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(4) };
        void UpdatePosition() => position.Text = models.Length == 0 ? Ui("Sin coincidencias · revisa búsqueda o F4", "No matches · check search or F4")
            : $"{(list.SelectedItem ?? 0) + 1} / {models.Length}  ·  " + Ui("↑↓ navegar · Enter elegir", "↑↓ navigate · Enter select");
        list.ValueChanged += (_, _) => UpdatePosition();
        void FilterModels()
        {
            var text = search.Text.ToString().Trim();
            models = available.Where(model => (model.Id + " " + (_policies.DisplayName(model.Id) ?? "") + " " + model.ProviderId)
                .Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(models.Select(model =>
                (_selectedModel == model.Id ? " ✓  " : "    ") + (_policies.DisplayName(model.Id) ?? model.Id) + "   ·   " + model.ProviderId)));
            list.CanFocus = models.Length > 0;
            if (models.Length > 0) list.SelectedItem = 0;
            list.EnsureSelectedItemVisible();
            UpdatePosition();
            if (models.Length == 0) position.Text = available.Length == 0
                ? Ui("Sin modelos visibles · F4 para configurarlos", "No visible models · F4 to configure")
                : Ui("Sin coincidencias · cambia la búsqueda", "No matches · change the search");
        }
        search.TextChanged += (_, _) => FilterModels();
        void HandlePaletteShortcut(Key key)
        {
            if (key.KeyCode == (KeyCode.F | KeyCode.CtrlMask)) { search.SetFocus(); key.Handled = true; }
            else if (key.KeyCode == (KeyCode.M | KeyCode.CtrlMask)) { CloseOverlay(); ShowModelPolicies(); key.Handled = true; }
        }
        void MoveSelection(int delta)
        {
            if (models.Length == 0) return;
            list.SelectedItem = ((list.SelectedItem ?? 0) + delta + models.Length) % models.Length;
            list.EnsureSelectedItemVisible();
        }
        list.KeyDown += (_, key) =>
        {
            HandlePaletteShortcut(key);
            if (key.Handled) return;
            if (models.Length == 0) return;
            var delta = key.KeyCode == KeyCode.CursorDown ? 1 : key.KeyCode == KeyCode.CursorUp ? -1 : 0;
            if (delta == 0) return;
            MoveSelection(delta);
            key.Handled = true;
        };
        search.KeyDown += (_, key) =>
        {
            HandlePaletteShortcut(key);
            if (key.Handled) return;
            if (key.KeyCode is KeyCode.CursorDown or KeyCode.CursorUp && models.Length > 0)
            {
                list.SetFocus(); key.Handled = true;
            }
            else if (key.KeyCode == KeyCode.Enter) { list.InvokeCommand(Command.Accept); key.Handled = true; }
        };
        list.MouseEvent += (_, mouse) =>
        {
            var delta = mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? 1 : mouse.Flags.HasFlag(MouseFlags.WheeledUp) ? -1 : 0;
            if (delta == 0 || models.Length == 0) return;
            MoveSelection(delta);
            mouse.Handled = true;
        };
        list.Accepted += (_, args) =>
        {
            args.Handled = true;
            if (list.SelectedItem is not int index || index < 0 || index >= models.Length) return;
            var model = models[index];
            if (!_catalogFresh && _policies.IsSubscriptionModel(model.Id)) return;
            var key = new ModelPolicyKeyDto(model.ProviderId, model.Id);
            var safe = _policies.Get(key, CancellationToken.None) is null;
            _policies.Select(WorkspaceSelectionId, key, model.Id, safe, CancellationToken.None);
            _turnStatus = safe ? Ui("Modo seguro · ObserveOnly", "Safe mode · ObserveOnly") : null;
            RefreshSelectedModel(); CloseOverlay();
        };
        FilterModels();
        var initial = Array.FindIndex(models, model => model.Id == _selectedModel);
        if (models.Length > 0) list.SelectedItem = Math.Max(0, initial);
        UpdatePosition();
        var close = new Button { Id = "omni-palette-action", X = Pos.AnchorEnd(8), Y = 0, Width = 6, Text = "Esc" };
        close.Accepted += (_, _) => CloseOverlay();
        var manage = new Button { Id = "omni-palette-action", X = 2, Y = Pos.AnchorEnd(1), Text = Ui("Gestionar modelos  Ctrl+M", "Manage models  Ctrl+M") };
        manage.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(); };
        ApplyTheme(_window, dimmed: true);
        if (_conversation is not null) _conversation.Dimmed = true;
        frame.Add(searchLabel, search, searchHint, list, position, close, manage); ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        if (models.Length > 0) list.SetFocus(); else search.SetFocus();
        if (refreshCatalog && _policies.SupportsDiscovery) RefreshAccountModels(frame, feedback, page, picker: true);
    }

    private void ShowModelPolicies(int page = 0, bool refreshCatalog = true, int focusIndex = -1, int focusColumn = 0)
    {
        if (_window is null || _overlay is not null) return;
        _policies.ReloadModels();
        if (refreshCatalog) _catalogFresh = false;
        var frame = OverlayFrame(_locale == "en" ? "Preferences > Models" : "Preferencias > Modelos", 18);
        var pageSize = Math.Max(1, Math.Min(18, _window.Frame.Height - 2) - 7);
        var models = _policies.Models.ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, models.Length - pageSize));
        var feedback = new Label { X = 2, Y = 1, Width = Dim.Fill(2), Text = (_catalogNotice ?? $"{models.Length} " + Ui("modelos", "models"))
            + Ui(" · ↑↓ filas · ←→ acciones · ☑ en F3", " · ↑↓ rows · ←→ actions · ☑ in F3") };
        frame.Add(feedback);
        var cells = new Dictionary<(int Index, int Column), Button>();
        void FocusModel(int index, int column)
        {
            if (models.Length == 0) return;
            index = (index + models.Length) % models.Length;
            if (index < page || index >= page + pageSize)
            {
                var offset = index < page ? index : index - pageSize + 1;
                CloseOverlay(); ShowModelPolicies(offset, false, index, column);
                return;
            }
            if (!cells.TryGetValue((index, column), out var target) || !target.Enabled)
                target = cells[(index, 1)];
            target.SetFocus();
        }
        void BindCell(Button button, int index, int column)
        {
            cells[(index, column)] = button;
            button.KeyDown += (_, key) =>
            {
                if (key.KeyCode is KeyCode.CursorDown or KeyCode.CursorUp)
                {
                    FocusModel(index + (key.KeyCode == KeyCode.CursorDown ? 1 : -1), column);
                    key.Handled = true;
                }
                else if (key.KeyCode is KeyCode.CursorLeft or KeyCode.CursorRight)
                {
                    var nextColumn = column + (key.KeyCode == KeyCode.CursorRight ? 1 : -1);
                    if (cells.TryGetValue((index, nextColumn), out var next) && next.Enabled) next.SetFocus();
                    key.Handled = true;
                }
            };
            button.MouseEvent += (_, mouse) =>
            {
                var delta = mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? 1 : mouse.Flags.HasFlag(MouseFlags.WheeledUp) ? -1 : 0;
                if (delta == 0) return;
                FocusModel(index + delta, column); mouse.Handled = true;
            };
        }
        var y = 2;
        foreach (var descriptor in models.Skip(page).Take(pageSize))
        {
            var key = new ModelPolicyKeyDto(descriptor.ProviderId, descriptor.Id);
            var record = _policies.Get(key, CancellationToken.None);
            var displayName = _policies.DisplayName(descriptor.Id);
            var label = (_selectedModel == descriptor.Id ? "✓ " : "") + descriptor.ProviderId + "/" + descriptor.Id
                + (displayName is not null && displayName != descriptor.Id ? " · " + displayName : "")
                + " · " + (record?.Category ?? (_locale == "en" ? "setup required" : "requiere configuración"));
            var row = y++;
            var index = page + row - 2;
            var select = new Button { Id = "model-policy-" + descriptor.Id, X = 1, Y = row, Width = Dim.Fill(13), TextAlignment = Alignment.Start, Text = label };
            BindCell(select, index, 0);
            if (!_catalogFresh && _policies.IsSubscriptionModel(descriptor.Id)) select.Enabled = false;
            select.Accepted += (_, _) =>
            {
                CloseOverlay(); ShowModelPolicySetup(_policies.Draft(key, CancellationToken.None));
            };
            frame.Add(select);
            var visible = _policies.IsVisibleInPicker(descriptor.ProviderId, descriptor.Id);
            var visibility = new Button { Id = "model-visibility-" + descriptor.Id,
                X = Pos.AnchorEnd(11), Y = row, Width = 4, Text = visible ? "☑" : "☐" };
            BindCell(visibility, index, 1);
            visibility.Accepted += (_, _) =>
            {
                _policies.SetVisibleInPicker(descriptor.ProviderId, descriptor.Id, !visible, CancellationToken.None);
                CloseOverlay(); ShowModelPolicies(page, false, index, 1);
            };
            frame.Add(visibility);
            if (record is not null)
            {
                var delete = new Button { Id = "delete-policy-" + descriptor.Id,
                    X = Pos.AnchorEnd(6), Y = row, Width = 4, Text = "🗑" };
                BindCell(delete, index, 2);
                delete.Accepted += (_, _) => ConfirmDeletePolicy(key, record.Revision, page);
                frame.Add(delete);
            }
        }
        var register = new Button { X = 2, Y = Pos.AnchorEnd(4), Text = Ui("Actualizar modelos", "Refresh models") };
        register.Accepted += (_, _) => { CloseOverlay(); _catalogNotice = null; ShowModelPolicies(); };
        frame.Add(register);
        var settings = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = Ui("Configuración", "Settings") };
        settings.Accepted += (_, _) => { CloseOverlay(); ShowPreferences(); };
        frame.Add(settings);
        var close = new Button { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = _locale == "en" ? "Close" : "Cerrar" };
        close.Accepted += (_, _) => CloseOverlay();
        frame.Add(close);
        void BindFooter(Button button, Action up, Action down)
        {
            button.KeyDown += (_, key) =>
            {
                if (key.KeyCode == KeyCode.CursorUp) { up(); key.Handled = true; }
                else if (key.KeyCode == KeyCode.CursorDown) { down(); key.Handled = true; }
            };
        }
        BindFooter(register, () => FocusModel(models.Length - 1, 0), () => settings.SetFocus());
        BindFooter(settings, () => register.SetFocus(), () => FocusModel(0, 0));
        BindFooter(close, () => register.SetFocus(), () => FocusModel(0, 0));
        ApplyTheme(frame);
        _overlay = frame; _window.Add(frame);
        if (models.Length > 0) FocusModel(focusIndex < 0 ? page : focusIndex, focusColumn); else register.SetFocus();
        if (refreshCatalog && _policies.SupportsDiscovery) RefreshAccountModels(frame, feedback, page);
    }

    private void ConfirmDeletePolicy(ModelPolicyKeyDto key, long revision, int page)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(Ui("Eliminar política", "Delete policy"), 10);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Text = key.ToString() },
            new Label { X = 2, Y = 4, Width = Dim.Fill(4),
                Text = Ui("Se borrará la política, no el modelo ni la cuenta.", "Deletes the policy, not the model or account.") });
        var cancel = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = Ui("Cancelar", "Cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(page, false); };
        var confirm = new Button { X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = Ui("Eliminar", "Delete") };
        confirm.Accepted += (_, _) =>
        {
            try { _policies.Delete(key, revision, CancellationToken.None); }
            catch (Exception) { CloseOverlay(); ShowMessage(Ui("No se pudo eliminar la política. Actualiza el listado.", "Could not delete the policy. Refresh the list.")); return; }
            CloseOverlay(); ShowModelPolicies(page, false);
        };
        frame.Add(cancel, confirm); ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        cancel.SetFocus();
    }

    private void RefreshAccountModels(FrameView frame, Label feedback, int page, bool picker = false)
    {
        try
        {
            _account ??= _accountOverride ?? new TuiAccountHost();
            if (!_account.Status().LoggedIn) { feedback.Text = Ui("Inicia sesión en F4 para consultar ChatGPT.", "Sign in with F4 to discover ChatGPT models."); return; }
        }
        catch (Exception) { feedback.Text = Ui("No se pudo leer la sesión ChatGPT.", "Could not read the ChatGPT session."); return; }
        feedback.Text = Ui("Consultando modelos disponibles…", "Loading available models…");
        var cancellation = _catalogCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = await _account.ListModelsAsync(cancellation.Token).ConfigureAwait(false);
                _uiActions.Enqueue(() =>
                {
                    if (cancellation.IsCancellationRequested || !ReferenceEquals(_overlay, frame)) return;
                    try
                    {
                        _catalogNotice = _policies.ApplyChatGptCatalog(catalog, WorkspaceSelectionId, cancellation.Token)
                            ?? Ui("Catálogo actualizado", "Catalog refreshed");
                        _catalogFresh = true;
                        var query = frame.SubViews.OfType<TextField>().FirstOrDefault(v => v.Id == "omni-palette-search")?.Text.ToString() ?? "";
                        RefreshSelectedModel(); CloseOverlay();
                        if (picker) ShowModelPicker(page, false, query); else ShowModelPolicies(page, false);
                    }
                    catch (Exception) { feedback.Text = Ui("No se pudo guardar el catálogo; selección conservada.", "Could not save catalog; selection preserved."); }
                });
            }
            catch (Exception)
            {
                _uiActions.Enqueue(() =>
                {
                    if (!ReferenceEquals(_overlay, frame)) return;
                    feedback.Text = picker
                        ? Ui("Consulta fallida; selección conservada. Reabre el selector.", "Discovery failed; selection preserved. Reopen the picker.")
                        : Ui("Consulta fallida; selección conservada. Reintenta Actualizar.", "Discovery failed; selection preserved. Retry Refresh.");
                });
            }
            // The UI owns this CTS until it closes/replaces the panel.
        });
    }

    private void ShowModelPolicySetup(ModelPolicyDraftDto draft)
    {
        if (_window is null) return;
        var model = ModelPolicySetupModel.From(draft.Key.ToString(), draft.RecommendedCategory, draft.Warnings);
        var expectedRevision = _policies.Get(draft.Key, CancellationToken.None)?.Revision ?? 0;
        var frame = OverlayFrame(_locale == "en" ? "Model setup" : "Configurar modelo", 14);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = model.ModelIdentity });
        var y = 3;
        foreach (var warning in model.Warnings) frame.Add(new Label { X = 1, Y = y++, Width = Dim.Fill(2), Text = "! " + warning });
        foreach (var choice in model.Choices)
        {
            var button = new Button { X = 1, Y = y++, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = (choice.Recommended ? "[recommended] " : "") + choice.Category + " — " + choice.Description };
            button.Accepted += (_, _) =>
            {
                try { _policies.Set(draft.Key, expectedRevision, choice.Category, null, CancellationToken.None); }
                catch (Exception) { CloseOverlay(); ShowMessage(Ui("No se pudo guardar la política. Abre nuevamente el modelo.", "Could not save the policy. Reopen the model.")); return; }
                RefreshSelectedModel();
                CloseOverlay(); ShowModelPolicies();
            };
            frame.Add(button);
        }
        var cancel = new Button { X = 1, Y = y, Text = _locale == "en" ? "Cancel" : "Cancelar" };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(); };
        frame.Add(cancel); ApplyTheme(frame); _overlay = frame; _window!.Add(frame); frame.SetFocus();
    }

    private void RefreshSelectedModel()
    {
        var selection = _policies.CurrentSelection(WorkspaceSelectionId, CancellationToken.None)?.ModelId;
        if (selection != _selectedModel) _observedModel = null;
        _selectedModel = selection;
        if (_header is not null) _header.Text = HeaderText();
        if (_status is not null) _status.Text = StatusText();
    }

    private void ShowMessage(string message)
    {
        if (_window is null) return;
        CloseOverlay();
        // Diagnostic JSON is inspected as structured text, not a clipped single-line blob.
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(message);
            using var buffer = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer,
                new System.Text.Json.JsonWriterOptions { Indented = true })) document.RootElement.WriteTo(writer);
            message = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (System.Text.Json.JsonException) { }
        var lines = message.Split('\n');
        var available = Math.Max(1, (_window.Frame.Height > 0 ? _window.Frame.Height : 25) - 9);
        var visible = lines.Take(available).ToArray();
        if (lines.Length > available) visible[^1] = "…";
        var frame = OverlayFrame(_locale == "en" ? "Notice" : "Aviso", Math.Max(6, visible.Length + 5));
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Height = visible.Length, Text = string.Join("\n", visible) });
        var ok = new Button { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = "OK" };
        ok.Accepted += (_, _) => CloseOverlay(); frame.Add(ok); ApplyTheme(frame); _overlay = frame; _window.Add(frame); frame.SetFocus();
    }

    private void CloseOverlay()
    {
        if (_overlay is null || _window is null) return;
        var catalog = _catalogCancellation; _catalogCancellation = null;
        if (catalog is not null) { catalog.Cancel(); catalog.Dispose(); }
        var login = _loginCancellation;
        _loginCancellation = null;
        if (login is not null) { try { login.Cancel(); } catch (ObjectDisposedException) { } }
        // Draw contexts can still reference the outgoing panel's adornments. Retire it
        // immediately, but dispose after a subsequent completed frame, never inside Accepted.
        _window.Remove(_overlay); _retiredOverlays.Add(_overlay); _overlay = null; _activeInteraction = null;
        ApplyTheme(_window);
        if (_conversation is not null) { _conversation.Dimmed = false; _conversation.SetNeedsDraw(); }
        _composer?.SetFocus();
    }

    private void DisposeRetiredOverlays()
    {
        var retired = _retiredOverlays.ToArray(); _retiredOverlays.Clear();
        foreach (var view in retired) view.Dispose();
        foreach (var view in _overlaysReadyForDisposal) view.Dispose();
        _overlaysReadyForDisposal.Clear();
    }

    private void SendTrustedModeCommand(string command, string fields)
    {
        if (_client is not OmniCore.Protocol.ITrustedUserActionClient trusted)
        { ShowMessage(Ui("Este cliente no permite cambiar autoridad local", "This client cannot change local authority")); return; }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", command)
            + (fields.Length == 0 ? "" : "," + fields) + "}"), CancellationToken.None);
        if (ack.Status != "ok") ShowMessage(ack.Error ?? Ui("Cambio de modo rechazado", "Mode change rejected"));
        else ShowMessage(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Deferred
            ? Ui("El turno aún tiene un paso activo; vuelve a intentarlo cuando termine",
                "The active turn still has a model step; retry after it completes")
            : Ui("Modo actualizado", "Mode updated"));
    }

    private void HandleReasoningCommand(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            ShowMessage(Ui("Uso: /reasoning [default|run] <kind|off|reset> [budgetTokens] o /reasoning boost <kind|budget> [budgetTokens]",
                "Usage: /reasoning [default|run] <kind|off|reset> [budgetTokens] or /reasoning boost <kind|budget> [budgetTokens]"));
            return;
        }

        if (parts[0] == "boost")
        {
            if (_turnHost is null || parts.Length is < 2 or > 3
                || !TryBuildReasoningRequest(parts[1], parts.Length == 3 ? parts[2] : null,
                    out var boostKind, out var boostBudgetTokens)
                || boostKind is null)
            {
                ShowMessage(Ui("Impulso de un turno inválido o no disponible", "One-turn boost is invalid or unavailable"));
                return;
            }
            if (!_turnHost.SetNextTurnReasoningBoost(boostKind, boostBudgetTokens))
            {
                ShowMessage(Ui("Ya hay un impulso pendiente", "A reasoning boost is already pending"));
                return;
            }
            ShowMessage(Ui("Impulso aplicado al próximo turno nuevo: ", "Boost queued for the next fresh turn: ")
                + boostKind + (boostBudgetTokens is { } budget ? ":" + budget : ""));
            return;
        }

        if (parts.Length is < 2 or > 3 || parts[0] is not ("default" or "run"))
        {
            ShowMessage(Ui("Uso: /reasoning [default|run] <kind|off|reset> [budgetTokens]",
                "Usage: /reasoning [default|run] <kind|off|reset> [budgetTokens]"));
            return;
        }
        var isRun = parts[0] == "run";
        var json = _client.Query(isRun ? "runReasoningPreference" : "reasoningPreference",
            CancellationToken.None)?.Json ?? "null";
        long revision = 0;
        if (json != "null" && !TryReadLong(json, "revision", out revision))
        {
            ShowMessage(Ui("No se pudo leer la revisión de razonamiento", "Could not read the reasoning revision"));
            return;
        }
        if (parts[1] == "reset")
        {
            if (parts.Length != 2)
            {
                ShowMessage(Ui("reset no acepta un presupuesto", "reset does not accept a budget"));
                return;
            }
            SendTrustedReasoningCommand(isRun ? "run.reasoning.revoke" : "reasoning.default.revoke",
                "\"expectedRevision\":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            PollEvents();
            return;
        }
        if (!TryBuildReasoningRequest(parts[1], parts.Length == 3 ? parts[2] : null,
                out var requestKind, out var requestBudgetTokens))
        {
            ShowMessage(Ui("Tipo o presupuesto de razonamiento inválido", "Invalid reasoning kind or budget"));
            return;
        }
        var fields = JsonObj.Field("kind", requestKind ?? "off")
            + (requestBudgetTokens is { } tokenBudget
                ? ",\"budgetTokens\":" + tokenBudget.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "")
            + ",\"expectedRevision\":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SendTrustedReasoningCommand(isRun ? "run.reasoning.select" : "reasoning.default.set", fields);
        PollEvents();
    }

    private static bool TryBuildReasoningRequest(string kind, string? budgetText,
        out string? requestKind, out int? budgetTokens)
    {
        requestKind = null;
        budgetTokens = null;
        if (kind == "off") return budgetText is null;
        if (string.IsNullOrWhiteSpace(kind)) return false;
        if (kind == "budget")
        {
            if (!int.TryParse(budgetText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var budget) || budget < 1024)
                return false;
            requestKind = kind;
            budgetTokens = budget;
            return true;
        }
        if (budgetText is not null) return false;
        requestKind = kind;
        return true;
    }

    private void SendTrustedReasoningCommand(string command, string fields)
    {
        if (_client is not OmniCore.Protocol.ITrustedUserActionClient trusted)
        {
            ShowMessage(Ui("Este cliente no permite cambiar razonamiento", "This client cannot change reasoning"));
            return;
        }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", command)
            + (fields.Length == 0 ? "" : "," + fields) + "}"), CancellationToken.None);
        if (ack.Status != "ok")
            ShowMessage(ack.Error ?? Ui("Cambio de razonamiento rechazado", "Reasoning change rejected"));
        else if (ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Deferred)
            ShowMessage(Ui("El turno aún tiene un paso activo; vuelve a intentarlo cuando termine",
                "The active turn still has a model step; retry after it completes"));
        else
            ShowMessage(Ui("Razonamiento actualizado", "Reasoning updated"));
    }

    private static bool TryReadLong(string json, string name, out long value)
    {
        value = 0;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var property)
                && property.TryGetInt64(out value);
        }
        catch (System.Text.Json.JsonException) { return false; }
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

    private static bool TryReadWorkflowOutcome(string json, out WorkflowRequestDto? request, out string commandId)
    {
        request = null;
        commandId = "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("outcome", out var outcome)
                || outcome.ValueKind != System.Text.Json.JsonValueKind.Object
                || !outcome.TryGetProperty("kind", out var kind)
                || kind.GetString() != "workflowRequested") return false;
            var workflowId = outcome.GetProperty("workflowId").GetString() ?? "";
            var version = outcome.GetProperty("version").GetString() ?? "";
            var arguments = outcome.GetProperty("arguments").EnumerateArray()
                .Select(value => value.GetString() ?? "").ToArray();
            request = new WorkflowRequestDto(workflowId, version, arguments);
            commandId = outcome.GetProperty("commandId").GetString() ?? "";
            return commandId.Length > 0;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException
            or KeyNotFoundException)
        { return false; }
    }
}
