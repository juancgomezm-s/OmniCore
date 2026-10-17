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
public sealed partial class TuiApp
{
    private readonly IOmniClient _client;
    private IApplication? _application;
    private string _locale;
    private Localization _localization;
    private ClientProjection _projection;
    private LocaleSelection? _localeSelection;
    private readonly Action<string>? _localeChanged;
    private readonly SessionSidebarProjection _sidebarProjection = new();
    private readonly AgentsProjection _agents = new();
    private DateTimeOffset _agentsPolledAt;
    private readonly ChangedFilesProjection _files = new();
    private SidebarPreferencesSnapshot _sidebarSettings = new("", new(), []);
    private DateTimeOffset _sidebarSettingsPolledAt;
    private string? _sidebarSelection;
    private SidebarView? _sidebarSession;
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
    private Label? _attachmentChips;
    private readonly List<TuiAttachmentReference> _pendingAttachments = [];
    private FrameView? _conversationFrame;
    private Label? _help;
    private int _mainColumnInset;
    private Label? _header;
    private Label? _status;
    private FrameView? _sidebar;
    private SidebarView? _sidebarContent;
    private Label? _completion;
    private FrameView? _commandHelper;
    private ListView? _commandList;
    private string[] _commandSuggestions = Array.Empty<string>();
    private IReadOnlyDictionary<string, string> _disabledCommandReasons =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private View? _overlay;
    private InteractionOverlayModel? _activeInteraction;
    private bool _sidebarOpen;
    private readonly ITuiAccountHost? _accountOverride;
    private ITuiAccountHost? _account;
    private CancellationTokenSource? _loginCancellation;
    private CancellationTokenSource? _catalogCancellation;
    private string? _catalogNotice;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _uiActions = new();
    private readonly ITuiLocalEngineHost _localEngines;
    private readonly ITuiLocalEngineSupervisor? _localEngineSupervisor;
    private readonly IPolicyPreferencesFacade? _policyPreferencesOverride;
    private readonly ISecurityPreferencesFacade? _securityPreferencesOverride;
    private readonly IDiagnosticsPreferencesFacade? _diagnosticsPreferencesOverride;
    private readonly ITuiClaudeAccountHost _claudeAccounts;
    private string WorkspaceSelectionId => ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
    private string? _selectedModel;
    private string? _observedModel;
    private string? _selectionObservationSession;
    private long _selectionObservationAfterSequence;
    private bool _showMarkdownExamples;
    private readonly List<View> _retiredOverlays = new();
    private readonly List<View> _overlaysReadyForDisposal = new();
    private readonly bool _ownsPolicies;
    private readonly ITuiTurnHost? _turnHost;
    private CancellationTokenSource? _turnCancellation;
    private Task? _turnTask;
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, Task Task, string? SessionId, string? RunId)> _delegationTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _turnBusy;
    private string? _localActivitySession;
    private string? _pendingEscalationResume;
    private string? _pendingQuotaResume;
    private ActivityView? _activity;
    private ActivityView? _activityLabel;
    private long _activityPhase;
    private int _activityExtraRows;
    private int _activityTicks;
    private readonly AnimationSettingsStore _animationStore;
    private AnimationPreferences _animationPreferences;
    private ActivityView? _animationPreview;
    private string _previewEffect = "omega-pulse";
    internal View? Activity => _activity;
    private string? _turnStatus;
    private UsageSnapshot? _turnUsage;
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

    public TuiApp(IOmniClient client, string locale = "es", ModelPolicyHost? policies = null, ITuiAccountHost? account = null, ITuiTurnHost? turnHost = null, ITuiProviderConnectionHost? providerConnections = null,
        string? animationSettingsPath = null, LocaleSelection? localeSelection = null,
        ITuiLocalEngineHost? localEngines = null, Action<string>? localeChanged = null,
        ITuiClaudeAccountHost? claudeAccounts = null, ITuiLocalEngineSupervisor? localEngineSupervisor = null,
        IPolicyPreferencesFacade? policyPreferences = null, ISecurityPreferencesFacade? securityPreferences = null,
        IDiagnosticsPreferencesFacade? diagnosticsPreferences = null)
    {
        _client = client;
        _localeSelection = localeSelection;
        _localeChanged = localeChanged;
        _locale = UiLocaleCodes.NormalizeOrDefault(localeSelection?.Locale ?? locale);
        _localization = new Localization(_locale);
        _projection = new ClientProjection(_localization);
        _policies = policies ?? ModelPolicyHost.Create();
        _ownsPolicies = policies is null;
        _accountOverride = account;
        _turnHost = turnHost;
        _providerConnectionHost = providerConnections ?? TuiProviderConnectionHost.Create();
        _localEngineSupervisor = localEngineSupervisor;
        _policyPreferencesOverride = policyPreferences;
        _securityPreferencesOverride = securityPreferences;
        _diagnosticsPreferencesOverride = diagnosticsPreferences;
        _localEngines = localEngines ?? new TuiLocalEngineHost(supervisor: localEngineSupervisor);
        _claudeAccounts = claudeAccounts ?? TuiClaudeAccountHost.Create();
        _animationStore = new(animationSettingsPath ?? TuiPresentationPaths.AnimationSettingsPath());
        _themeStore = new(Path.ChangeExtension(animationSettingsPath ?? TuiPresentationPaths.AnimationSettingsPath(), ".theme"));
        _theme = _themeStore.Read();
        _animationPreferences = _animationStore.Load();
        RefreshSidebarPreferences();
        _selectedModel = _policies.CurrentSelection(WorkspaceSelectionId, CancellationToken.None)?.ModelId;
        _state = RefreshState();
        if (_selectedModel is not null) MarkExplicitModelSelection();
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
        var status = StatusLinePresentation.From(state.StatusLine, app._locale);
        return "OmniCore · TUI (sim)" + Environment.NewLine + header + Environment.NewLine
            + (conversation.Length == 0 ? app._localization.Resolve("shell.empty")
                : conversation) + Environment.NewLine + status.Left + " · " + status.Right;
    }

    public static int Run(IOmniClient client, string locale = "es", ITuiTurnHost? turnHost = null,
        LocaleSelection? localeSelection = null, Action<string>? localeChanged = null,
        ITuiClaudeAccountHost? claudeAccounts = null, ITuiLocalEngineSupervisor? localEngineSupervisor = null)
    {
        var app = new TuiApp(client, locale, turnHost: turnHost, localeSelection: localeSelection,
            localeChanged: localeChanged, claudeAccounts: claudeAccounts, localEngineSupervisor: localEngineSupervisor);
        using var application = Application.Create();
        // Native Windows input avoids VT escape-sequence mouse/keyboard ambiguity.
        // Preserve explicit driver configuration and the platform default on Unix.
        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(application.ForceDriver))
            application.ForceDriver = DriverRegistry.Names.WINDOWS;
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
        _application = application;
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
            ClearProviderApiKey();
            CancelProviderConnectionOperation();
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
            _suspendedSecurityForm?.Dispose(); _suspendedSecurityForm = null; _cancelSecurityConfirmation = null;
            DisposeRetiredOverlays();
            if (_ownsPolicies) _policies.Dispose();
        }
        return 0;
    }

    /// <summary>Poll de eventos del cliente; devuelve true para repetir como AddTimeout.</summary>
    internal bool PollOnce()
    {
        while (_uiActions.TryDequeue(out var action)) action();
        ReflowPreferencesIfNeeded();
        PollEvents();
        return true;
    }

    internal bool AnimateActivity()
    {
        if (!HasActiveModelWork && _activity?.Visible != true && _animationPreview is null) return true;
        if (_animationPreferences.Enabled && !_animationPreferences.ReducedMotion
            && ++_activityTicks >= _animationPreferences.IntervalMilliseconds / 100)
        {
            _activityTicks = 0;
            _activityPhase = _activityPhase == long.MaxValue ? 0 : _activityPhase + 1;
        }
        RenderActivity();
        RenderAnimationPreview();
        return true;
    }

    private void RenderActivity()
    {
        if (_activity is null) return;
        var roots = _agents.Snapshot?.Lanes.Where(lane => lane.ParentTaskId is null).ToArray() ?? [];
        var runId = _sidebarProjection.Snapshot?.RunId;
        var pendingChildren = _delegationTasks.Values.Count(task => task.SessionId == _cursorSession && task.RunId == runId);
        var presentation = ActivityPresentation.From(_cursorSession, _sidebarProjection.Snapshot?.RunId,
            roots.Length == 1 ? roots[0].LaneId : null, _observability.Activity,
            _turnBusy && (_localActivitySession is null || _localActivitySession == _cursorSession), pendingChildren);
        _activity.Visible = presentation.Visible && (_window?.Frame.Width ?? 80) - _mainColumnInset >= 30;
        var effect = _animationPreferences.Effect(presentation.Phase);
        var rows = _activity.Visible && _animationPreferences.Enabled && !_animationPreferences.ReducedMotion
            && presentation.Moving && (_window?.Frame.Height ?? 25) >= 22 ? AnimationCatalog.Rows(effect) : 1;
        if (_activityExtraRows != rows - 1)
        {
            _activityExtraRows = rows - 1; _activity.Height = rows; _activity.Y = Pos.AnchorEnd(rows + 2);
            if (_activityLabel is not null) _activityLabel.Y = Pos.AnchorEnd(rows + 2);
            UpdateComposerHeight();
        }
        if (_activityLabel is not null) _activityLabel.Visible = _activity.Visible;
        if (_completion is not null) _completion.Visible = !_activity.Visible;
        if (!_activity.Visible) return;
        if (_activityLabel is not null)
        {
            _activityLabel.Width = Dim.Fill(_mainColumnInset + 2);
            _activityLabel.Render(effect, _activityPhase,
                presentation.Moving && _animationPreferences.Enabled && !_animationPreferences.ReducedMotion,
                Environment.GetEnvironmentVariable("NO_COLOR") is not null,
                presentation.Phase == ChatActivityPhase.WaitingForApproval, _animationPreferences.Ascii,
                presentation.Label(_locale));
        }
        _activity.Render(effect, _activityPhase,
            presentation.Moving && _animationPreferences.Enabled && !_animationPreferences.ReducedMotion,
            Environment.GetEnvironmentVariable("NO_COLOR") is not null,
            presentation.Phase == ChatActivityPhase.WaitingForApproval, _animationPreferences.Ascii);
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
        else if (code == "F4")
        {
            if (_overlay?.Id == "theme-preferences") ReturnFromPreferencesSubpage();
            else ShowPreferences();
            key.Handled = true;
        }
        else if (code == "F6") { ShowConversations(); key.Handled = true; }
        else if (key.KeyCode == (KeyCode.Tab | KeyCode.ShiftMask) && _overlay is null && _activeInteraction is null)
        { CycleMode(); key.Handled = true; }
        else if (code == "Esc" && _activeInteraction is { } interaction) { RespondDefault(interaction); key.Handled = true; }
        else if (code == "Esc" && HandleSessionPreferencesEscape()) { key.Handled = true; }
        else if (code == "Esc" && HandleModelCatalogPreferencesEscape()) { key.Handled = true; }
        else if (code == "Esc" && HandleOperationalPreferencesEscape()) { key.Handled = true; }
        else if (code == "Esc" && _preferencesReturnToCategory) { ReturnFromPreferencesSubpage(); key.Handled = true; }
        else if (code == "Esc" && _preferencesCenterActive) { HandlePreferencesEscape(); key.Handled = true; }
        else if (code == "Esc" && _overlay is not null) { CloseOverlay(); key.Handled = true; }
        else if (code == "Esc" && _commandHelper is not null) { HideCommandHelper(); key.Handled = true; }
    }

    private bool HandleOperationalPreferencesEscape()
    {
        switch (_overlay?.Id)
        {
            case "animation-picker":
                ReturnFromAnimationPicker(); return true;
            case "about-screen":
                ShowHelp(); return true;
            case "policy-preferences":
                CloseOverlay(); ShowPreferences("models_execution", categoryPage: true); return true;
            case "security-preferences":
                CloseOverlay(); ShowPreferences("tools_security", categoryPage: true); return true;
            case "security-preferences-confirm":
                CancelSecurityConfirmation(); return true;
            case "diagnostics-preferences":
                CloseOverlay(); ShowPreferences("diagnostics_data", categoryPage: true); return true;
            case "diagnostics-confirm":
                CloseOverlay(); ShowDiagnosticsPreferences("diagnostics_data"); return true;
            default: return false;
        }
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
        ConfigureConversationLinks(_conversation);
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
        _sidebar = new FrameView { Id = "omni-panel", X = Pos.AnchorEnd(31), Y = 0, Width = 31, Height = Dim.Fill(), Title = _localization.Resolve("shell.workspace_heading"), BorderStyle = LineStyle.None };
        _sidebarContent = new SidebarView { X = 2, Y = 10, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        _sidebarContent.RowActivated += row =>
        {
            if (row.Action == "diff.open" && row.Target is { } file) ShowFileDiff(file);
            else if (row.Action == "sidebar.expand" && row.Target is { } widget)
            {
                var expanded = _sidebarSettings.Preferences.Widgets?.GetValueOrDefault(widget)?.Expanded == true;
                if (SaveSidebarSetting("Workspace", "widgets." + widget + ".expanded", expanded ? "false" : "true", true))
                    _sidebarContent.SelectWidget(widget);
            }
        };
        _sidebarSession = new SidebarView { X = 2, Y = 3, Width = Dim.Fill(1), Height = 5, CanFocus = false, ScrollBars = false };
        var settings = new Button { Id = "sidebar-settings", X = Pos.AnchorEnd(9), Y = 1, Text = _localization.Resolve("shell.settings") };
        settings.Accepted += (_, _) => ShowSidebarSettings();
        _sidebarAttention = new Label { Id = "omni-help", X = 2, Y = 8, Width = Dim.Fill(1), Height = 1 };
        _sidebar.Add(new Label { Id = "omni-heading", X = 2, Y = 0, Text = _localization.Resolve("shell.workspace_heading") }, settings,
            _sidebarSession, _sidebarAttention, _sidebarContent);
        var composerFrame = _composerFrame = new FrameView { Id = "omni-composer", X = 1, Y = Pos.AnchorEnd(6), Width = Dim.Fill(1), Height = 3, BorderStyle = LineStyle.None };
        composerFrame.Add(new View { Id = "omni-composer-accent", X = 0, Y = 0, Width = 1, Height = Dim.Fill() });
        _attachmentChips = new Label { Id = "omni-attachments", X = 4, Y = 0, Width = Dim.Fill(2), Height = 1, Text = "", Visible = false };
        composerFrame.Add(_attachmentChips);
        _composer = new ComposerView { X = 4, Y = 1, Width = Dim.Fill(2), Height = 1, Text = "", Multiline = true,
            WordWrap = true, EnterKeyAddsLine = true };
        composerFrame.Add(new Label { Id = "omni-heading", X = 2, Y = 1, Text = "›" });
        _completion = new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Height = 1, Text = "" };
        _composer.TextChanged += (_, _) => { UpdateComposerHeight(); UpdateAutocomplete(); };
        _composer.ContentsChanged += (_, _) => { UpdateComposerHeight(); UpdateAutocomplete(); };
        _composer.KeyDown += (_, key) =>
        {
            if (key.KeyCode == (KeyCode.V | KeyCode.CtrlMask)) { PasteClipboard(); key.Handled = true; return; }
            if (key.KeyCode == (KeyCode.O | KeyCode.CtrlMask)) { PickAttachments(); key.Handled = true; return; }
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
            Text = HelpText() };
        _window.Add(_help, _sidebar);
        _activity = new ActivityView { Id = "omni-activity", X = 2, Y = Pos.AnchorEnd(3), Width = 12, Height = 1,
            CanFocus = false, Visible = false };
        _activityLabel = new ActivityView { Id = "omni-activity-label", X = 16, Y = Pos.AnchorEnd(3), Width = 14, Height = 1,
            Text = _localization.Resolve("shell.processing"), CanFocus = false, Visible = false };
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
        var attachmentRows = _pendingAttachments.Count == 0 ? 0 : 1;
        _composer.Height = _composerRows;
        _composerFrame.Height = _composerRows + 2 + attachmentRows;
        _composerFrame.Y = Pos.AnchorEnd(_composerRows + 5 + attachmentRows + _activityExtraRows);
        if (_conversationFrame is not null) _conversationFrame.Height = Dim.Fill(_composerRows + 6 + attachmentRows + _activityExtraRows);
        if (_commandHelper is not null) _commandHelper.Y = Pos.Top(_composerFrame) - _commandHelper.Frame.Height;
    }

    private void UpdateAutocomplete()
    {
        if (_composer is null || _completion is null) return;
        var draft = _composer.Text?.ToString() ?? "";
        if (draft.Length == 0 || draft[0] is not ('/' or '@')) { _completion.Text = ""; HideCommandHelper(); return; }
        var commandCatalogJson = _client.Query("commands", CancellationToken.None)?.Json;
        _disabledCommandReasons = ReadDisabledCommandReasons(commandCatalogJson);
        var aliases = new UserCommandAliases((_policyPreferencesOverride ?? PolicyPreferencesHost.Create(Environment.CurrentDirectory))
            .Read().CommandAliases);
        var names = ReadStringArray(commandCatalogJson, "commands")
            .Concat(new[] { "act", "context", "tools", "plan", "mode", "runmode", "ultracode", "reasoning",
                "cancel", "interrupt", "preferences", "models", "sidebar", "agents", "delegate", "agent", "join", "fanout", "animation", "images",
                "new", "sessions", "resume-session", "clear", "compact" })
            .Concat(ClientCommandRegistry.Default.Commands.Select(command => command.Name))
            .Where(name => !TuiMenuCommands.IsMenuOwned(name, aliases));
        var suggestions = ComposerAutocomplete.Complete(draft,
        aliases.CompletionNames(names).Where(name => !TuiMenuCommands.IsMenuOwned(name, aliases)),
            ReadStringArray(_client.Query("complete:" + draft[1..], CancellationToken.None)?.Json, "paths"),
            name => HumanCommandAliases.DisplayName(name, _locale));
        // Show one spelling in the effective UI language. Canonical commands remain accepted
        // when typed explicitly, without duplicating the localized browsing surface.
        if (draft == "/") suggestions = suggestions.DistinctBy(suggestion => suggestion.Value)
            .OrderBy(suggestion => HumanCommandAliases.NormalizeSlashHead(suggestion.Value) switch { "/help" => 0, "/act" => 1, "/mode" => 2, "/models" => 3, "/agents" => 4, _ => 5 }).ToArray();
        else if (draft[0] == '/') suggestions = suggestions
            .OrderBy(suggestion => suggestion.Value == draft ? 0
                : HumanCommandAliases.NormalizeSlashHead(suggestion.Value) == suggestion.Value ? 1 : 2).ToArray();
        _completion.Text = string.Join("   ", suggestions.Take(5).Select(suggestion => suggestion.Value));
        HideCommandHelper();
        if (draft[0] != '/' || draft.Any(char.IsWhiteSpace) || _overlay is not null || _window is null) return;
        _commandSuggestions = suggestions.Select(suggestion => suggestion.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (_commandSuggestions.Length == 0) return;
        var height = Math.Min(10, _commandSuggestions.Length + 2);
        height = Math.Min(height, Math.Max(3, _window.Frame.Height - 9));
        _commandHelper = new FrameView { Id = "omni-command-helper", X = 1, Y = Pos.Top(_composerFrame!) - height,
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
            Text = _localization.Resolve("shell.select_tab_complete_enter_accept_esc_close") });
        ApplyTheme(_commandHelper, "#142636");
        _window.Add(_commandHelper);
        _completion.Visible = false;
    }

    private string CommandDescription(string command) => HumanCommandAliases.NormalizeSlashHead(command) switch
    {
        "/new" => _localization.Resolve("shell.conversation.new"),
        "/sessions" => _localization.Resolve("shell.conversation.title"),
        "/resume-session" => _localization.Resolve("shell.conversation.resume"),
        "/clear" => _localization.Resolve("shell.conversation.clear"),
        "/compact" => _localization.Resolve("shell.conversation.compact"),
        "/agents" => _localization.Resolve("shell.inspect_run_agents_and_lanes"),
        "/delegate" => _localization.Resolve("shell.queue_bounded_read_only_work"),
        "/fanout" => _localization.Resolve("shell.group_queued_delegations_with_direct_or_aggregate_fan_in"),
        "/agent" => _localization.Resolve("shell.run_cancel_or_evaluate_a_delegate"),
        "/join" => _localization.Resolve("shell.wait_for_accepted_results_all_any_quorum_explicit"),
        "/act" => _localization.Resolve("shell.execute_explicit_file_changes"),
        "/mode" => _localization.Resolve("shell.set_the_next_run_s_default_mode"),
        "/runmode" => _localization.Resolve("shell.change_the_active_run_s_mode"),
        "/ultracode" => _localization.Resolve("shell.explicit_adaptive_authority_with_finite_limits_plan_current_covers_onl"),
        "/reasoning" => _localization.Resolve("shell.persistent_preference_run_selection_or_one_turn_boost"),
        "/models" => _localization.Resolve("shell.select_model"),
        "/preferences" => _localization.Resolve("shell.open_settings"),
        "/animation" => _localization.Resolve("shell.activity_and_preview"),
        "/login" => _localization.Resolve("shell.chatgpt_account_and_connection"),
        "/sidebar" => _localization.Resolve("shell.toggle_workspace_panel"),
        "/fuente" => _localization.Resolve("shell.show_or_hide_original_markdown_examples"),
        "/context" => _localization.Resolve("shell.inspect_turn_context"),
        "/tools" => _localization.Resolve("shell.show_available_tools"),
        "/plan" => _localization.Resolve("shell.show_current_plan"),
        "/cancel" => _localization.Resolve("shell.cancel_active_turn"),
        "/interrupt" => _localization.Resolve("shell.interrupt_active_turn"),
        var head when ClientCommandRegistry.Default.Commands.FirstOrDefault(c => "/" + c.Name == head) is { } client =>
            _localization.Resolve(client.DescriptionKey),
        var head when head.StartsWith('/') && _disabledCommandReasons.TryGetValue(head[1..], out var reason) =>
            _localization.Resolve("shell.disabled") + DescribeDisabledCommand(reason),
        _ => _localization.Resolve("shell.workspace_command")
    };

    private string DescribeDisabledCommand(string reason) => reason.Contains("Orchestrate", StringComparison.OrdinalIgnoreCase)
        && reason.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            ? _localization.Resolve("shell.requires_an_active_orchestrate_run_and_current_authorization")
            : CommandCatalogPresentation.DescribeReason(reason, _localization);

    private static IReadOnlyDictionary<string, string> ReadDisabledCommandReasons(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("catalog", out var catalog)
                || !catalog.TryGetProperty("commands", out var commands)
                || commands.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var command in commands.EnumerateArray())
            {
                if (!command.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != System.Text.Json.JsonValueKind.False
                    || !command.TryGetProperty("disabledReason", out var disabledReason)
                    || disabledReason.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(disabledReason.GetString())
                    || !command.TryGetProperty("name", out var name)
                    || name.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var reason = disabledReason.GetString()!;
                result[name.GetString()!] = reason;
                if (command.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var alias in aliases.EnumerateArray())
                        if (alias.ValueKind == System.Text.Json.JsonValueKind.String)
                            result[alias.GetString()!] = reason;
            }
        }
        catch (System.Text.Json.JsonException) { }
        return result;
    }

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
        var rawInput = _composer.Text?.ToString() ?? "";
        var aliases = new UserCommandAliases((_policyPreferencesOverride ?? PolicyPreferencesHost.Create(Environment.CurrentDirectory))
            .Read().CommandAliases);
        // Whitespace is grammar for commands, but remains literal in a conversation prompt.
        var input = HumanCommandAliases.NormalizeSlashSyntax(aliases.NormalizeSlashHead(rawInput.Trim()));
        if (HandleConversationSlash(input)) return;
        if (input == "/attach" || input.StartsWith("/attach ", StringComparison.Ordinal))
        {
            if (input.Length == 7) PickAttachments();
            else AddAttachmentFromPath(input[8..].Trim().Trim('"'));
            return;
        }
        if (input == "/paste")
        {
            _composer!.Text = "";
            PasteClipboard();
            return;
        }
        if (input == "/attachments")
        {
            ShowMessage(_pendingAttachments.Count == 0 ? _localization.Resolve("shell.attachment.none")
                : string.Join(Environment.NewLine, _pendingAttachments.Select((item, index) =>
                    $"{index + 1}. {item.FileName} ({item.Size} bytes)"))
                    + Environment.NewLine + _localization.Resolve("shell.attachment.remove_help"));
            return;
        }
        if (input.StartsWith("/remove-attachment ", StringComparison.Ordinal))
        {
            if (int.TryParse(input[19..], out var number) && number >= 1 && number <= _pendingAttachments.Count)
            { _pendingAttachments.RemoveAt(number - 1); RenderAttachmentChips(); }
            else ShowMessage(_localization.Resolve("shell.attachment.invalid_index"));
            return;
        }
        if (input == "/send")
        {
            if (_pendingAttachments.Count == 0) { ShowMessage(_localization.Resolve("shell.attachment.none_to_send")); return; }
            if (StartModelTurn("", attachments: _pendingAttachments.ToArray())) _composer.Text = "";
            return;
        }
        if (input == "/animation" || input.StartsWith("/animation ", StringComparison.Ordinal))
        {
            HandleAnimationCommand(input.Length > 10 ? input[11..].Trim() : "");
            _composer.Text = "";
            return;
        }
        if (input == "/fuente")
        {
            _showMarkdownExamples = !_showMarkdownExamples;
            _renderedConversation = null;
            _composer.Text = "";
            RenderState();
            return;
        }
        if (input.Length == 0) return;
        if (input == "/images")
        {
            if (_turnHost is null) ShowMessage(_localization.Resolve("shell.images.unavailable"));
            else
            {
                var images = _turnHost.ListGeneratedImages();
                ShowMessage(images.Count == 0 ? _localization.Resolve("shell.images.empty")
                    : string.Join(Environment.NewLine, images.Select((image, imageIndex) => _localization.Resolve("shell.images.item",
                        new Dictionary<string, string> { ["index"] = (imageIndex + 1).ToString(), ["type"] = image.MediaType,
                            ["size"] = image.Size.ToString(), ["dimensions"] = $"{image.Width}×{image.Height}", ["hash"] = image.Hash[..12] }))));
            }
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/images save ", StringComparison.Ordinal))
        {
            if (_turnHost is null) ShowMessage(_localization.Resolve("shell.images.unavailable"));
            else
            {
                var arguments = input[13..].Trim();
                var separator = arguments.IndexOf(' ');
                if (separator <= 0 || !int.TryParse(arguments[..separator], out var imageIndex) || imageIndex < 1)
                    ShowMessage(_localization.Resolve("shell.images.save_usage"));
                else
                {
                    var destination = arguments[(separator + 1)..].Trim().Trim('"');
                    try
                    {
                        var saved = _turnHost.SaveGeneratedImage(imageIndex, destination);
                        ShowMessage(_localization.Resolve("shell.images.saved", new Dictionary<string, string> { ["path"] = saved }));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                    { ShowMessage(_localization.Resolve("shell.images.save_failed", new Dictionary<string, string> { ["reason"] = error.Message })); }
                }
            }
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/images open ", StringComparison.Ordinal))
        {
            if (_turnHost is null) ShowMessage(_localization.Resolve("shell.images.unavailable"));
            else if (!int.TryParse(input[13..].Trim(), out var imageIndex) || imageIndex < 1)
                ShowMessage(_localization.Resolve("shell.images.open_usage"));
            else
            {
                try
                {
                    var opened = _turnHost.OpenGeneratedImage(imageIndex);
                    ShowMessage(_localization.Resolve("shell.images.opened", new Dictionary<string, string> { ["path"] = opened }));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
                { ShowMessage(_localization.Resolve("shell.images.open_failed", new Dictionary<string, string> { ["reason"] = error.Message })); }
            }
            _composer.Text = ""; return;
        }
        if (_commandList?.SelectedItem is int index && index < _commandSuggestions.Length && input != HumanCommandAliases.NormalizeSlashHead(_commandSuggestions[index]))
        { CompleteCommand(); return; }
        HideCommandHelper();
        if (input.StartsWith("/act ", StringComparison.Ordinal))
        {
            if (_pendingAttachments.Count > 0) { ShowMessage(_localization.Resolve("shell.attachment.act_unsupported")); return; }
            if (StartModelTurn(input[5..], act: true)) { _composer.Text = ""; PollEvents(); } return;
        }
        // Bare forms never reach the Host as unknown commands: they explain how to use them.
        if (input == "/act") { ShowMessage(_localization.Resolve("shell.act_usage")); _composer.Text = ""; return; }
        if (input == "/runmode") { ShowMessage(_localization.Resolve("shell.usage_runmode_plan_act_orq")); _composer.Text = ""; return; }
        if (input is "/ultracode" or "/ultracode on") { ShowMessage(_localization.Resolve("shell.usage_ultracode")); _composer.Text = ""; return; }
        if (input == "/mode")
        {
            var preference = _client.Query("modePreference", CancellationToken.None)?.Json ?? "{}";
            var authority = _client.Query("modeAuthority", CancellationToken.None)?.Json ?? "null";
            ShowMessage(_localization.Resolve("shell.mode_overview", new Dictionary<string, string>
            {
                ["current"] = StatusLinePresentation.ModeLabel(CurrentModeCode(), _locale),
                ["next"] = ReadableModeField(preference, "mode"),
                ["authority"] = authority is "null" or "{}" ? "—" : ReadableJsonText(authority),
            }));
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/mode ", StringComparison.Ordinal))
        {
            var requested = input[6..].Trim().ToLowerInvariant();
            if (requested is not ("plan" or "act" or "orq"))
            { ShowMessage(_localization.Resolve("shell.usage_mode_plan_act_orq")); _composer.Text = ""; return; }
            var preference = _client.Query("modePreference", CancellationToken.None)?.Json ?? "{}";
            if (!TryReadLong(preference, "revision", out var revision))
            { ShowMessage(_localization.Resolve("shell.could_not_read_the_mode_revision")); _composer.Text = ""; return; }
            SendTrustedModeCommand("mode.default.set", JsonObj.Field("mode", requested)
                + ",\"expectedRevision\":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _composer.Text = ""; return;
        }
        if (input.StartsWith("/runmode ", StringComparison.Ordinal))
        {
            var requested = input[9..].Trim().ToLowerInvariant();
            if (requested is not ("plan" or "act" or "orq"))
            { ShowMessage(_localization.Resolve("shell.usage_runmode_plan_act_orq")); _composer.Text = ""; return; }
            SendTrustedModeCommand("run.mode.select", JsonObj.Field("mode", requested)
                + ",\"effort\":\"standard\"");
            _composer.Text = ""; PollEvents(); return;
        }
        if (input == "/reasoning")
        {
            var user = _client.Query("reasoningPreference", CancellationToken.None)?.Json ?? "null";
            var run = _client.Query("runReasoningPreference", CancellationToken.None)?.Json ?? "null";
            ShowMessage(_localization.Resolve("shell.user_preference") + user
                + Environment.NewLine + _localization.Resolve("shell.run_selection") + run);
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
                { ShowMessage(_localization.Resolve("shell.use_explicit_key_value_options")); _composer.Text = ""; return; }
            }
            var required = new[] { "mode", "modes", "agents", "depth", "turns", "tools", "seconds" };
            if (values.TryGetValue("plan", out var planCoverage) && planCoverage != "current")
            { ShowMessage(_localization.Resolve("shell.use_plan_current_to_cover_only_the_current_revision")); _composer.Text = ""; return; }
            if (required.Any(key => !values.ContainsKey(key))
                || values["mode"] is not ("plan" or "act" or "orq"))
            { ShowMessage(_localization.Resolve("shell.ultracode_requires_a_mode_and_every_finite_limit")); _composer.Text = ""; return; }
            if (!int.TryParse(values["agents"], out var agents) || !int.TryParse(values["depth"], out var depth)
                || !int.TryParse(values["turns"], out var turns) || !int.TryParse(values["tools"], out var tools)
                || !long.TryParse(values["seconds"], out var seconds)
                || values["modes"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(mode => mode is not ("plan" or "act" or "orq")))
            { ShowMessage(_localization.Resolve("shell.invalid_ultracode_limits")); _composer.Text = ""; return; }
            decimal? spend = null;
            if (values.TryGetValue("spend-usd", out var spendText))
            {
                if (!decimal.TryParse(spendText, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSpend))
                { ShowMessage(_localization.Resolve("shell.invalid_ultracode_limits")); _composer.Text = ""; return; }
                spend = parsedSpend;
            }
            var payload = JsonObj.Field("mode", values["mode"])
                + ",\"effort\":\"ultracode\",\"adaptive\":true," + JsonObj.Field("allowedModes", values["modes"])
                + ",\"maxAgents\":" + agents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxDepth\":" + depth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxTurns\":" + turns.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxToolCalls\":" + tools.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"maxElapsedSeconds\":" + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (spend is { } spendLimit)
                payload += ",\"maxSpendUsd\":" + spendLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (values.ContainsKey("plan")) payload += ",\"coverCurrentPlan\":true";
            SendTrustedModeCommand("run.mode.select", payload);
            _composer.Text = ""; PollEvents(); return;
        }
        if (input == "/preferences") { ShowPreferences(); _composer.Text = ""; return; }
        if (input == "/openrouter") { ShowPreferences("accounts_connections", categoryPage: true); _composer.Text = ""; return; }
        if (input == "/login") { ShowAccount(fromPreferences: false); _composer.Text = ""; return; }
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
            else ShowMessage(_localization.Resolve("shell.sidebar_usage"));
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
                    throw new InvalidOperationException(_localization.Resolve("shell.trusted_client"));
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
                    throw new ArgumentException(_localization.Resolve("shell.fanout_invalid"));
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
                    || !allowed.SetEquals(properties.Select(property => property.Name)))
                    throw new ArgumentException(_localization.Resolve("shell.fanout_required"));
                var payload = replacing
                    ? "{\"cmd\":\"fanout.replace_member\",\"groupId\":" + root.GetProperty("groupId").GetRawText()
                        + ",\"previousDelegationId\":" + root.GetProperty("previousDelegationId").GetRawText()
                        + ",\"replacementDelegationId\":" + root.GetProperty("replacementDelegationId").GetRawText() + "}"
                    : "{\"cmd\":\"fanout.create\"," + root.GetRawText()[1..^1] + "}";
                var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
                ShowMessage(ack.Error ?? ack.Outcome?.Reason ?? _localization.Resolve("shell.fan_out_recorded"));
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
                ShowMessage(_localization.Resolve("shell.usage_agent_inspect_laneid_unknown_lane"));
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
                    throw new ArgumentException("/agent run|cancel <DelegationId> · /agent send <DelegationId> <mensaje> · /agent accept|reject|rework <DelegationId> <ResultId> <razón>");
                if (parts[1] == "run")
                { if (StartModelTurn("", delegationId: parts[2])) _composer.Text = ""; return; }
                if (_client is not ITrustedUserActionClient trusted) throw new InvalidOperationException(_localization.Resolve("shell.trusted_client"));
                var fields = JsonObj.Field("delegationId", parts[2]);
                var name = "delegation.cancel";
                (CancellationTokenSource Cancellation, Task Task)? background = null;
                if (parts[1] == "cancel" && Guid.TryParse(parts[2], out var cancellingId)
                    && _delegationTasks.TryGetValue(cancellingId.ToString("D"), out var activeBackground))
                    background = (activeBackground.Cancellation, activeBackground.Task);
                if (parts[1] == "send")
                {
                    var message = input[(input.IndexOf(parts[2], StringComparison.Ordinal) + parts[2].Length)..].Trim();
                    if (message.Length == 0) throw new ArgumentException("/agent send <DelegationId> <mensaje>");
                    fields += "," + JsonObj.Field("content", message);
                    name = "delegation.mailbox.send";
                }
                else if (parts[1] != "cancel")
                {
                    if (parts.Length != 5 || !Guid.TryParse(parts[3], out _)) throw new ArgumentException("ResultId y razón requeridos");
                    var outcome = parts[1] switch { "accept" => "Accepted", "reject" => "Rejected", "rework" => "ReworkRequested", _ => throw new ArgumentException(_localization.Resolve("shell.unknown_action")) };
                    fields += "," + JsonObj.Field("resultId", parts[3]) + "," + JsonObj.Field("outcome", outcome) + "," + JsonObj.Field("reason", parts[4]);
                    name = "delegation.disposition";
                }
                var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", name) + "," + fields + "}"), CancellationToken.None);
                if (parts[1] == "cancel" && ack.Status == "ok"
                    && ack.Outcome?.Kind is RuntimeCommandOutcomeKind.Accepted or RuntimeCommandOutcomeKind.NoOp)
                    background?.Cancellation.Cancel();
                ShowMessage(ack.Error ?? ack.Outcome?.Reason ?? _localization.Resolve("shell.action_recorded")); PollEvents();
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException) { ShowMessage(failure.Message); }
            _composer.Text = ""; return;
        }
        if (input == "/join" || input.StartsWith("/join ", StringComparison.Ordinal))
        {
            try
            {
                if (_client is not ITrustedUserActionClient trusted) throw new InvalidOperationException(_localization.Resolve("shell.trusted_client"));
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
                ShowMessage(ack.Error ?? ack.Outcome?.Reason ?? _localization.Resolve("shell.join_recorded")); PollEvents();
            }
            catch (Exception failure) when (failure is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { ShowMessage(failure.Message); }
            _composer.Text = ""; return;
        }
        if (input == "/delegate" || input.StartsWith("/delegate ", StringComparison.Ordinal))
        {
            try
            {
                if (_client is not ITrustedUserActionClient trusted)
                    throw new InvalidOperationException(_localization.Resolve("shell.client_cannot_admit_local_delegation"));
                var request = input.Length > 10 ? AgentsJson.DecodeRequest(input[10..]) : null;
                if (request is null) throw new ArgumentException(_localization.Resolve("shell.usage_delegate")
                    + "{\"profileId\":\"UUID\",\"sourceEventId\":\"UUID de /agents\",\"objective\":\"Revisar\",\"selectedItemIds\":[],"
                    + "\"maximumPacketBytes\":8192,\"maxTurns\":2,\"maxToolCalls\":4}");
                var ack = trusted.SendUserAction(DelegationCommands.Create(request, Ids.NewV7()), CancellationToken.None);
                ShowMessage(ack.Status != "ok" ? ack.Error ?? _localization.Resolve("shell.delegation_rejected")
                    : ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Deferred ? _localization.Resolve("shell.deferred") + ack.Outcome.Reason
                    : _localization.Resolve("shell.delegation_queued_use_agents_and_agent_run_delegationid_to_dispatch_in"));
                PollEvents();
            }
            catch (Exception failure) when (failure is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            { ShowMessage(failure.Message); }
            _composer.Text = ""; return;
        }
        // ADR-0024/0025: los ClientCommands (/help, /exit, /plan, /tasks, /events, /context, /tools, /permissions,
        // /cancel, /interrupt) resuelven a una ClientAction; la TUI solo aporta lo que es suyo (cerrar, el Turn en curso).
        if (input[0] == '/' && CommandLineParser.TryParse(input, out var typed) && typed is not null
            && ClientCommandRegistry.Default.TryResolve(typed, out var clientAction) && clientAction is not null)
        {
            RunClientAction(clientAction);
            return;
        }
        if (input[0] == '/')
        {
            if (!CommandLineParser.TryParse(input, out var invocation) || invocation is null) return;
            var ack = _client.Send(WireEnvelope.Command(Ids.NewV7(), CommandInvocationJson.Encode(invocation)), CancellationToken.None);
            if (ack.Status == "ok" && ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Rejected)
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
                    else
                    {
                        ShowMessage(_localization.Resolve("shell.this_workflow_command_requires_the_local_host"));
                        return;
                    }
                }
                else if (text.TryGetValue("outcome", out _)
                    && TryReadOutcome(outcome, out var expanded))
                {
                    string? forcedToolName = null;
                    using (var outcomeDocument = System.Text.Json.JsonDocument.Parse(outcome))
                        if (outcomeDocument.RootElement.GetProperty("outcome").TryGetProperty("toolName", out var toolName)
                            && toolName.GetString() is { Length: > 0 } selectedTool) forcedToolName = selectedTool;
                    if (_turnHost is not null) { if (!StartModelTurn(expanded, extensionToolName: forcedToolName)) return; }
                    else if (!SubmitSessionInput(expanded)) return;
                }
            }
            else
            {
                ShowMessage("/" + invocation.Name + ": " + CommandError(ack.Error));
                return;
            }
        }
        else if (input[0] == '@')
        {
            ShowMessage(_localization.Resolve("shell.references.unavailable"));
            return;
        }
        else
        {
            if (_turnHost is not null) { if (!StartModelTurn(rawInput, attachments: _pendingAttachments.ToArray())) return; }
            else if (!SubmitSessionInput(rawInput)) return;
        }
        if (_pendingAttachments.Count == 0) _composer.Text = "";
        PollEvents();
    }

    private void PasteClipboard()
    {
        if (_application?.Clipboard is null) return;
        if (!_application.Clipboard.TryGetClipboardData(out var value) || string.IsNullOrEmpty(value))
        {
            if (_pendingAttachments.Count >= AttachmentReferences.MaximumCount)
            { ShowMessage(_localization.Resolve("shell.attachment.limit")); return; }
            try
            {
                var png = WindowsClipboardImage.TryReadPng();
                if (png is null) { ShowMessage(_localization.Resolve("shell.attachment.clipboard_empty")); return; }
                var reference = _turnHost?.ImportAttachment(png, "clipboard.png") ?? throw new NotSupportedException();
                _pendingAttachments.Add(reference);
                RenderAttachmentChips();
                ShowMessage(_localization.Resolve("shell.attachment.image_pasted"));
            }
            catch (Exception)
            { ShowMessage(_localization.Resolve("shell.attachment.paste_rejected")); }
            return;
        }
        if (value.Length > 200_000)
        { ShowMessage(_localization.Resolve("shell.attachment.clipboard_too_large")); return; }
        if (value.Length <= 32_000)
        {
            _composer?.InsertText(value);
            return;
        }
        if (_pendingAttachments.Count >= AttachmentReferences.MaximumCount)
        { ShowMessage(_localization.Resolve("shell.attachment.limit")); return; }
        try
        {
            var reference = _turnHost?.ImportAttachment(System.Text.Encoding.UTF8.GetBytes(value), "clipboard.txt")
                ?? throw new NotSupportedException();
            _pendingAttachments.Add(reference);
            RenderAttachmentChips();
            ShowMessage(_localization.Resolve("shell.attachment.long_paste_added"));
        }
        catch (Exception)
        { ShowMessage(_localization.Resolve("shell.attachment.paste_rejected")); }
    }

    private void PickAttachments()
    {
        if (_application is null || _turnHost is null) return;
        try
        {
            var dialog = new OpenDialog
            {
                Title = _localization.Resolve("shell.attachment.pick_title"),
                OpenMode = OpenMode.File,
                AllowsMultipleSelection = true,
            };
            _application.Run(dialog);
            foreach (var path in dialog.FilePaths)
            {
                if (_pendingAttachments.Count >= AttachmentReferences.MaximumCount)
                { ShowMessage(_localization.Resolve("shell.attachment.limit")); break; }
                AddAttachmentFromPath(path);
            }
        }
        catch (Exception)
        { ShowMessage(_localization.Resolve("shell.attachment.pick_failed")); }
    }

    private void AddAttachmentFromPath(string path)
    {
        if (_turnHost is null) { ShowMessage(_localization.Resolve("shell.attachment.unavailable")); return; }
        if (_pendingAttachments.Count >= AttachmentReferences.MaximumCount)
        { ShowMessage(_localization.Resolve("shell.attachment.limit")); return; }
        try
        {
            var reference = _turnHost.ImportAttachmentFromPath(path);
            _pendingAttachments.Add(reference);
            RenderAttachmentChips();
            ShowMessage(_localization.Resolve("shell.attachment.added", "name", reference.FileName ?? "attachment"));
        }
        catch (Exception)
        { ShowMessage(_localization.Resolve("shell.attachment.rejected")); }
    }

    private void RenderAttachmentChips()
    {
        if (_attachmentChips is null) return;
        _attachmentChips.Visible = _pendingAttachments.Count > 0;
        _attachmentChips.Text = _pendingAttachments.Count == 0 ? ""
            : "[" + string.Join("] [", _pendingAttachments.Select((item, index) =>
                $"{index + 1}: {item.FileName ?? "attachment"} ×")) + "]";
        UpdateComposerHeight();
    }

    private void RunClientAction(ClientActionInvocation action)
    {
        _composer!.Text = "";
        if (OpenClientView(action)) return;
        // La TUI lleva su propio Turn: cancelar o interrumpir detiene primero ese Turn (misma ruta que antes).
        if (action.ActionId is ClientActionIds.RunCancel or ClientActionIds.RunInterrupt && _turnBusy)
        {
            _turnCancellation?.Cancel();
            PollEvents();
            return;
        }
        var context = new ClientContext(_client, _localization, CliApp.PermissionsListing);
        var result = new ClientActionHandler().ExecuteAsync(action, context, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (result.Tools is { } tools) ShowTools(tools);
        else if (result.Text.Length > 0) ShowMessage(result.Text);
        if (result.ExitRequested) _application?.RequestStop();
        PollEvents();
    }

    private bool StartModelTurn(string input, bool act = false, string? resumeEscalation = null, string? resumeQuota = null,
        string? delegationId = null, WorkflowRequestDto? workflow = null, string? workflowCommandId = null,
        string? extensionToolName = null, IReadOnlyList<TuiAttachmentReference>? attachments = null)
    {
        if (_turnHost is null) return false;
        if (act && attachments is { Count: > 0 })
        { ShowMessage(_localization.Resolve("shell.attachment.act_unsupported")); return false; }
        if (delegationId is not null) return StartDelegationTurn(delegationId);
        if (_turnBusy) { ShowMessage(_localization.Resolve("shell.a_turn_is_already_processing_wait_or_use_cancel")); return false; }
        try
        {
            if (!_turnHost.TryAcquireSessionWriter())
            { ShowMessage(_localization.Resolve("session_writer_owned")); return false; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowMessage(_localization.Resolve("shell.turn_failed_check_omni_doctor") + "\n" + OmniCliRuntime.RedactSensitive(error.Message));
            return false;
        }
        _turnCancellation?.Dispose();
        var cancellation = _turnCancellation = new CancellationTokenSource();
        var submittedAttachments = attachments?.ToArray() ?? [];
        var submittedDraft = _composer?.Text?.ToString() ?? "";
        _followConversationEnd = true;
        _localActivitySession = _cursorSession;
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
                code = await (extensionToolName is not null ? _turnHost.ExecuteExtensionCommandAsync(input, extensionToolName,
                    diagnostic, cancellation.Token)
                    : workflow is not null ? _turnHost.ExecuteWorkflowAsync(workflow,
                    workflowCommandId ?? throw new InvalidOperationException("Workflow command identity is missing."),
                    diagnostic, cancellation.Token)
                    : delegationId is not null ? _turnHost.ExecuteDelegationAsync(delegationId, diagnostic, cancellation.Token)
                    : resumeQuota is not null ? _turnHost.ResumeQuotaAsync(resumeQuota, diagnostic, cancellation.Token)
                    : resumeEscalation is not null ? _turnHost.ResumeModelRouteAsync(resumeEscalation, diagnostic, cancellation.Token)
                    : act ? _turnHost.ExecuteActAsync(input, diagnostic, cancellation.Token)
                    : submittedAttachments.Length > 0
                        ? _turnHost.ExecuteAsync(input, submittedAttachments, diagnostic, cancellation.Token)
                        : _turnHost.ExecuteAsync(input, diagnostic, cancellation.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { code = 2; }
            catch (Exception) { code = 1; messages.Add(_localization.Resolve("shell.turn_failed_check_omni_doctor")); }
            _uiActions.Enqueue(() =>
            {
                _turnBusy = false;
                if (code == 0 && submittedAttachments.Length > 0)
                {
                    foreach (var submitted in submittedAttachments) _pendingAttachments.Remove(submitted);
                    RenderAttachmentChips();
                    if (_composer?.Text?.ToString() == submittedDraft) _composer.Text = "";
                }
                _turnStatus = cancellation.IsCancellationRequested ? _localization.Resolve("shell.cancelled")
                    : code == 0 ? _localization.Resolve("shell.done") : code == 3 ? _localization.Resolve("shell.input_required") : _localization.Resolve("shell.error");
                RefreshSelectedModel(); PollEvents();
                _turnUsage = _turnHost.CurrentUsage();
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
            ShowMessage(_localization.Resolve("shell.that_delegation_is_already_running_in_the_background"));
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
            catch (Exception) { messages.Add(_localization.Resolve("shell.delegation_failed_check_agents")); }
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
        _delegationTasks.Add(id, (cancellation, task, _cursorSession, _sidebarProjection.Snapshot?.RunId));
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
            _cursorSession = session; _lastSequence = 0; _observedModel = null; _turnUsage = null;
            var empty = ClientState.Empty();
            _state = new ClientState(_state.Header, empty.Conversation, empty.Sidebar,
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
        // Activity is used by the composer indicator too, even when the sidebar is hidden.
        _observability.Poll(_client);
    }

    private void ObserveModel(WireEnvelope envelope)
    {
        if (envelope.MessageType != MessageTypes.Event) return;
        var fields = JsonObj.Parse(envelope.PayloadJson);
        if (fields.GetValueOrDefault("conversationScope") is not ("child" or "ambiguous")
            && fields.GetValueOrDefault("type") == "model_step.started"
            && fields.GetValueOrDefault("modelId") is { Length: > 0 } model)
        {
            if (_selectionObservationSession == _cursorSession
                && long.TryParse(fields.GetValueOrDefault("seq"), out var sequence)
                && sequence <= _selectionObservationAfterSequence) return;
            _observedModel = model;
        }
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
        ? _localization.Resolve("shell.welcome")
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
            ?? _localization.Resolve("shell.new_session");
        return SessionHeading(workspace, title, _observedModel ?? _selectedModel ?? _localization.Resolve("shell.automatic_no_turn"), width, _locale);
    }

    internal static string SessionHeading(string workspace, string title, string model, int width, string locale = "es")
    {
        width = Math.Max(1, width);
        var project = FitText(workspace, Math.Max(1, Math.Min(20, width / 5)));
        var projectWidth = Terminal.Gui.Text.StringExtensions.GetColumns(project, false);
        var modelText = FitText(new Localization(locale).Resolve("shell.model_label") + model, Math.Max(1, width - projectWidth - 6));
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

    private string HelpText()
    {
        var width = Math.Max(1, (_window?.Frame.Width ?? 80) - _mainColumnInset - 4);
        var normal = _conversation?.HasFocus == true ? _localization.Resolve("links.help")
            : CompactHelp(CurrentModeCode(), _locale, width, PermissionLevelLabel());
        return FitText(normal + " · " + _localization.Resolve("shell.attachment.help"), width);
    }

    internal static string CompactHelp(string mode, string locale, int width, string? permissionLevel = null)
    {
        var localization = new Localization(locale);
        var lead = StatusLinePresentation.ModeLabel(mode, locale)
            + (permissionLevel is null ? "" : " · " + permissionLevel) + localization.Resolve("shell.help.lead");
        var variants = new[] { localization.Resolve("shell.help.full"), localization.Resolve("shell.help.medium"),
            localization.Resolve("shell.help.short"), " · /" };
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
        var presentation = StatusLinePresentation.From(_state.StatusLine, _locale);
        var width = Math.Max(1, (_window?.Frame.Width ?? 80) - 4 - _mainColumnInset);
        var right = _turnBusy ? "" : _delegationTasks.Count > 0
            ? _localization.Resolve(_delegationTasks.Count == 1 ? "shell.background.one" : "shell.background.many",
                "count", _delegationTasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : _turnStatus ?? presentation.Right;
        var account = UsagePresentation.Account(_turnUsage?.Account, _localization);
        if (account is not null && !_turnBusy) right += " · " + account;
        return "  " + FitText(right, width);
    }

    private void RenderSidebar()
    {
        if (_sidebarContent is null) return;
        if (_sidebar?.SubViews.FirstOrDefault(view => view.Id == "omni-heading") is Label heading)
            heading.Text = _sidebar.Title = _localization.Resolve("shell.workspace_heading");
        var widgets = SessionSidebarPresentation.Build(_sidebarProjection.Snapshot, _observability.Snapshot,
            _observedModel ?? _selectedModel ?? _localization.Resolve("shell.automatic_no_turn"),
            _state.StatusLine.Mode, _locale, true).ToList();
        var agentWidget = AgentPresentation.Build(_agents.Snapshot, _locale, true);
        _sidebarWidgets = widgets.Concat(new[] { agentWidget, _files.Widget(_locale) }).ToArray();
        var layout = TuiLayoutModel.ForWidth(_window?.Frame.Width ?? 80, _sidebarOpen, _sidebarSettings.Preferences);
        _sidebarComposition = OmniCore.Client.SidebarComposition.Build(_sidebarWidgets, _state, _sidebarSettings.Preferences,
            layout.Mode, Math.Max(1, (_window?.Frame.Height ?? 25) - 11), _sidebarSelection, summaryLinks: true, locale: _locale);
        _sidebarSelection = _sidebarComposition.SelectedId;
        var pinned = _sidebarComposition.Pinned.Select(item => (item.Widget, item.Model is ListWidgetModel list
            ? (WidgetModel)(list with { Rows = list.Rows.Select(row => row with
                { Text = FitText(row.Text, Math.Max(1, layout.SidebarWidth - 6)) }).ToArray() }) : item.Model)).ToArray();
        _sidebarSession?.Render(pinned);
        _sidebarContent.Render(_sidebarComposition.Body);
        if (_sidebarAttention is not null) _sidebarAttention.Text = _sidebarSettings.Diagnostics.Count > 0 ? _localization.Resolve("shell.invalid_settings")
            : _sidebarComposition.AttentionIds.Count > 0 ? "! " + string.Join(" · ", _sidebarComposition.AttentionIds.Select(id => id.Replace("core.", "")))
            : _sidebarComposition.Collapsed > 0 ? _localization.Resolve("shell.details_collapsed_inspect") : "";
    }

    private void RefreshSidebarPreferences()
    {
        _sidebarSettingsPolledAt = DateTimeOffset.UtcNow;
        var query = _client.Query("sidebarSettings", CancellationToken.None);
        if (query is not null && SidebarPreferencesJson.Decode(query.Json) is { } preferences) _sidebarSettings = preferences;
    }

    private bool SaveSidebarSetting(string scope, string key, string value, bool notice)
    {
        if (_client is not ITrustedUserActionClient trusted) { if (notice) ShowMessage(_localization.Resolve("shell.trusted_client")); return false; }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sidebar.configure\","
            + JsonObj.Field("scope", scope) + "," + JsonObj.Field("key", key) + "," + JsonObj.Field("value", value)
            + "," + JsonObj.Field("revision", _sidebarSettings.Revision) + "}"), CancellationToken.None);
        RefreshSidebarPreferences();
        if (ack.Status != "ok") { if (notice) ShowMessage(ack.Error ?? _localization.Resolve("shell.settings_rejected")); return false; }
        if (key == "sidebar.visible") _sidebarOpen = _sidebarSettings.Preferences.Visible ?? _sidebarOpen;
        ApplyResponsiveLayout(); return true;
    }

    private void ShowSidebarSettings()
    {
        if (_window is null) return;
        CloseOverlay(); RefreshSidebarPreferences();
        var frame = OverlayFrame(_localization.Resolve("shell.sidebar_settings"), 22);
        frame.Id = "sidebar-preferences";
        var scope = "Workspace";
        string ScopeLabel() => _localization.Resolve("shell.sidebar_scope") + UiCompletionResources.EnumLabel("scope", scope, _locale);
        string LayoutLabel() => _localization.Resolve("shell.sidebar_mode") + _localization.Resolve("shell." + _sidebarSettings.Preferences.Mode);
        var scopeButton = new Button { X = MenuIndent.Level0, Y = 2, Text = ScopeLabel() };
        var mode = new Button { X = Pos.Right(scopeButton) + 1, Y = 2, Text = LayoutLabel() };
        var heading = MenuIndent.Heading(_localization.Resolve("shell.widget_list_heading"), 4);
        var list = new ListView { Id = "omni-model-list", X = MenuIndent.Level1, Y = 5, Width = Dim.Fill(2), Height = Dim.Fill(10) };
        var description = new Label { Id = "omni-detail", X = MenuIndent.Level1, Y = Pos.AnchorEnd(9), Width = Dim.Fill(2), Height = 2 };
        var help = new Label { Id = "omni-help", X = MenuIndent.Level1, Y = Pos.AnchorEnd(5), Width = Dim.Fill(2), Height = 2 };
        var notice = new Label { Id = "omni-help", X = MenuIndent.Level0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(2), Height = 1 };
        var widgets = _sidebarWidgets.ToArray();
        var ids = widgets.Select(w => w.Id).ToArray();
        string DescriptionOf(string id) => _localization.Resolve(ShellUiResources.ContainsKey("shell.widget_desc." + id)
            ? "shell.widget_desc." + id : "shell.widget_desc.other");
        void Describe()
        {
            if (Selected() is { } id) description.Text = DescriptionOf(id);
        }
        void Update()
        {
            var selected = list.SelectedItem;
            var titleWidth = Math.Max(8, widgets.Length == 0 ? 8 : widgets.Max(w => w.Title.Length));
            list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(widgets.Select(widget =>
            {
                var setting = _sidebarSettings.Preferences.Widgets?.GetValueOrDefault(widget.Id) ?? new();
                return widget.Title.PadRight(titleWidth) + "  "
                    + _localization.Resolve("shell.widget_label_visibility") + _localization.Resolve("shell." + setting.Visible) + " · "
                    + _localization.Resolve("shell.widget_label_size") + _localization.Resolve(setting.Expanded ? "shell.expanded" : "shell.compact") + " · "
                    + _localization.Resolve("shell.widget_label_priority")
                    + (setting.Priority?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? _localization.Resolve("shell.default"));
            })));
            if (ids.Length > 0) list.SelectedItem = Math.Clamp(selected ?? 0, 0, ids.Length - 1);
            notice.Text = _sidebarSettings.Diagnostics.Count > 0 ? string.Join(" · ", _sidebarSettings.Diagnostics)
                : _localization.Resolve("shell.sidebar_precedence");
            mode.Text = LayoutLabel();
            Describe();
        }
        string? Selected() => list.SelectedItem is { } index && index >= 0 && index < ids.Length ? ids[index] : null;
        list.ValueChanged += (_, _) => Describe();
        help.Text = _localization.Resolve("shell.sidebar_settings_help");
        void Save(string key, string value)
        {
            if (!SaveSidebarSetting(scope, key, value, false)) notice.Text = _localization.Resolve("shell.not_saved_check_yaml_or_reload");
            else Update();
        }
        scopeButton.Accepted += (_, _) => { scope = scope == "Workspace" ? "User" : "Workspace"; scopeButton.Text = ScopeLabel(); };
        mode.Accepted += (_, _) =>
        {
            var values = new[] { "auto", "stacked", "tabbed", "overlay" };
            Save("sidebar.mode", values[(Array.IndexOf(values, _sidebarSettings.Preferences.Mode) + 1) % values.Length]);
        };
        var visible = new Button { X = MenuIndent.Level1, Y = Pos.AnchorEnd(7), Text = _localization.Resolve("shell.visibility") };
        var expand = new Button { X = Pos.Right(visible) + 1, Y = Pos.AnchorEnd(7), Text = _localization.Resolve("shell.expand") };
        var reset = new Button { X = Pos.Right(expand) + 1, Y = Pos.AnchorEnd(7), Text = _localization.Resolve("shell.reset") };
        visible.Accepted += (_, _) =>
        {
            if (Selected() is not { } id || id == "core.session") return;
            var value = _sidebarSettings.Preferences.Widgets?.GetValueOrDefault(id)?.Visible ?? "auto";
            Save("widgets." + id + ".visible", value == "auto" ? "true" : value == "true" ? "false" : "auto");
        };
        expand.Accepted += (_, _) => { if (Selected() is { } id) Save("widgets." + id + ".expanded", _sidebarSettings.Preferences.Widgets?.GetValueOrDefault(id)?.Expanded == true ? "false" : "true"); };
        reset.Accepted += (_, _) => { if (Selected() is { } id) foreach (var key in new[] { "visible", "expanded", "priority" }) Save("widgets." + id + "." + key, "reset"); };
        var up = new Button { X = MenuIndent.Level1, Y = Pos.AnchorEnd(6), Text = _localization.Resolve("shell.priority") };
        var down = new Button { X = Pos.Right(up) + 1, Y = Pos.AnchorEnd(6), Text = _localization.Resolve("shell.priority_67") };
        void Priority(int delta) { if (Selected() is { } id) Save("widgets." + id + ".priority", Math.Clamp((_sidebarSettings.Preferences.Widgets?.GetValueOrDefault(id)?.Priority ?? _sidebarWidgets.First(w => w.Id == id).DefaultPriority) + delta, -1000, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        up.Accepted += (_, _) => Priority(10); down.Accepted += (_, _) => Priority(-10);
        var back = new Button { Id = "sidebar-settings-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = Pref("back") };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("general", categoryPage: true); };
        frame.Add(scopeButton, mode, heading, list, description, visible, expand, reset, up, down, help, notice, back);
        Update(); ApplyTheme(frame); _overlay = frame; _window.Add(frame); list.SetFocus();
    }

    private void ShowFileDiff(string id)
    {
        if (_window is null) return;
        var query = _client.Query("diff.open:" + id, CancellationToken.None);
        var diff = query is null ? null : FilesJson.DecodeDiff(query.Json);
        if (diff is null || diff.SessionId != _cursorSession || diff.EffectId != id) { ShowMessage(_localization.Resolve("shell.diff_unavailable")); return; }
        CloseOverlay();
        var frame = OverlayFrame("Diff · " + diff.Path, _window.Frame.Height - 5);
        var content = new SidebarView { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(2), WordWrap = false, ShowRoleGlyphs = false };
        var model = FileDiffPresentation.Build(diff, locale: _locale);
        content.Render([(new ChangedFilesSidebarWidget(new([])), model)]);
        frame.Add(content, new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(1), Text = _localization.Resolve("shell.recorded_effect_excludes_later_edits_esc_close") });
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
        var bodyHeight = interaction.Kind == "ModelRouteConsent" ? 4 : 2;
        var overlay = OverlayFrame(interaction.Title, 5 + bodyHeight + interaction.Options.Count);
        _activeInteraction = interaction;
        overlay.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Height = bodyHeight,
            Text = interaction.Subject.Length == 0 ? interaction.Kind : interaction.Subject });
        Button? defaultButton = null;
        for (var index = 0; index < interaction.Options.Count; index++)
        {
            var optionId = interaction.OptionIds.ElementAtOrDefault(index) ?? "";
            // Sin IsDefault: en Terminal.Gui 2.6 un botón predeterminado suprime el Accept de
            // cualquier otro botón del contenedor (Enter sobre «Permitir una vez» dispararía
            // «Denegar»), lo que traiciona la intención visible del foco. La opción segura llega
            // por el foco inicial y por Esc (OnApplicationKeyDown → RespondDefault), no por flag.
            var button = new Button { X = 2, Y = 2 + bodyHeight + index, Text = interaction.Options[index] };
            button.Accepted += (_, args) => { args.Handled = true; RespondChoice(interaction, optionId); };
            overlay.Add(button);
            if (optionId == interaction.DefaultOptionId) defaultButton = button;
        }
        overlay.Add(new Label { Id = "omni-help", X = 2, Y = 3 + bodyHeight + interaction.Options.Count, Text = _localization.Resolve("shell.escape_deny") });
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

    // Blue surfaces remain distinct from the conversation (#061822) and sidebar (#202C3B).
    private const string MenuSurface = "#182A38";
    private const string PreferencesSurface = "#13222E";

    private void ApplyTheme(View view, string surface = "#061822", bool dimmed = false, bool preferences = false)
    {
        preferences |= view is FrameView && view.Id is { } preferenceId
            && (preferenceId.Contains("preferences", StringComparison.Ordinal) || preferenceId.StartsWith("model-catalog-", StringComparison.Ordinal)
                || preferenceId.StartsWith("claude-account", StringComparison.Ordinal));
        var information = preferences && view is Label && (view is MenuInformationLabel || view.Id is "omni-detail"
            || view.Id is { } labelId && new[] { "-help", "-hint", "-notice", "-status", "-summary", "-intro", "-details", "-metadata", "-note", "-description", "-modes" }
                .Any(suffix => labelId.EndsWith(suffix, StringComparison.Ordinal)));
        if (information) { view.X = MenuIndent.Information; view.CanFocus = false; }
        if (view is FrameView && preferences)
            PreferenceNavigation.Attach(view.SubViews.FirstOrDefault(child => child.Id == "preferences-panel") ?? view);
        // Framework defaults invert whole titles and edit fields. Keep a quiet surface;
        // focus remains visible through the cursor and cyan hotkeys, never color alone.
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        if (view.Id == "omni-composer") surface = "#293648";
        else if (view.Id == "omni-composer-accent") surface = "#B47CE7";
        else if (preferences && view is FrameView) surface = PreferencesSurface;
        else if (view.Id == "preferences-center" || view is FrameView
            && (view.Id?.StartsWith("session-preferences", StringComparison.Ordinal) == true
                || view.Id?.StartsWith("model-catalog-", StringComparison.Ordinal) == true
                || view.Id is "policy-preferences" or "security-preferences" or "security-preferences-confirm"
                    or "diagnostics-preferences" or "diagnostics-confirm" or "command-preferences" or "mapping-preferences")) surface = PreferencesSurface;
        else if (view.Id is "omni-panel" or "provider-connections" or "openrouter-credits") surface = "#202C3B";
        else if (view.Id == "omni-menu-title") surface = MenuSurface;
        else if (view.Id is "omni-model-list" or "tools-list" or "session-preferences-list" or "model-catalog-list"
            or "commands-list" or "security-rules" or "security-grants" or "diagnostics-configuration") surface = "#142636";
        else if (view.Id == "session-preferences-details") surface = "#1B2836";
        else if (view.Id == "preferences-panel") surface = PreferencesSurface;
        else if (view.Id == "preferences-categories") surface = MenuSurface;
        else if (view.Id is "preferences-search" or "commands-filter") surface = "#102331";
        else if (view is TextField) surface = "#293E51";
        else if (view.Id == "preferences-accent") surface = noColor ? "#061822" : "#67D4D0";
        else if (view.Id == "omni-menu-action") surface = "#293E51";
        else if (view.Id == "omni-model-palette" || view.Id == "omni-palette-search" || view.Id == "omni-palette-action") surface = "#061822";
        var background = noColor ? Color.None : new Color(surface);
        var foreground = information || view is MenuInformationLabel || view.Id == "omni-help" ? "#B9CBDF"
            : view.Id is "omni-heading" or "omni-activity-label" or "omni-section" or "preferences-account-heading" or "preferences-locale-label" ? "#85E6DF"
            : view.Id == "omni-detail" ? "#D6E2EE" : "#F4F7FB";
        if (dimmed) foreground = "#405666";
        var primary = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(foreground), background);
        var accent = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(dimmed ? "#405666" : "#67D4D0"), background);
#pragma warning disable CS0618 // The pinned Terminal.Gui multiline editor still uses TextView.
        var focus = view is Button or ListView or TextField || view is TextView and not ComposerView and not ConversationView
            ? noColor ? new Terminal.Gui.Drawing.Attribute(Color.None, Color.None, TextStyle.Reverse)
                : new Terminal.Gui.Drawing.Attribute(new Color("#FFFFFF"), new Color("#3D5068")) : primary;
#pragma warning restore CS0618
        if (view.Id is "omni-model-list" or "tools-list" or "preferences-categories" && !noColor)
            focus = new Terminal.Gui.Drawing.Attribute(new Color("#061822"), new Color("#67D4D0"));
        if (view is Button button)
        {
            button.NoDecorations = true;
            button.ShadowStyle = ShadowStyles.None;
        }
        var mappedFocus = _theme.Map(focus);
        if (!noColor && view is Button or ListView or TextField)
        {
            var fill = view.Id is "omni-model-list" or "tools-list" or "preferences-categories" ? _theme.Accent : _theme.Selection;
            mappedFocus = new Terminal.Gui.Drawing.Attribute(TuiTheme.On(fill), new Color(fill), focus.Style);
        }
        view.SetScheme(new Scheme { Normal = _theme.Map(primary), Focus = mappedFocus, Editable = _theme.Map(primary),
            HotNormal = _theme.Map(accent), HotFocus = view is Button or ListView ? mappedFocus : _theme.Map(accent) });
        if (view is StyledTextPresenter styled) styled.Theme = _theme;
        if (view is SidebarView sidebar) sidebar.Theme = _theme;
        if (view is ActivityView activity) activity.Theme = _theme;
        foreach (var child in view.SubViews) ApplyTheme(child, surface, dimmed, preferences);
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
        if (ack.Status != "ok") { ShowMessage(ack.Error ?? _localization.Resolve("shell.response_rejected")); return; }
        CloseOverlay(); PollEvents();
        if (interaction.Kind == "BudgetExceeded" && optionId == "allow_quota")
        {
            if (_turnBusy) _pendingQuotaResume = interaction.Id;
            else StartModelTurn("", resumeQuota: interaction.Id);
        }
        if (interaction.Kind == "ModelRouteConsent" && optionId == "allow_route")
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


    private bool _accountFromPreferences;

    private void ShowAccount(bool? fromPreferences = null)
    {
        if (_window is null || _overlay is not null) return;
        if (fromPreferences is { } origin) _accountFromPreferences = origin;
        try
        {
            _account ??= _accountOverride ?? new TuiAccountHost();
            var status = _account.Status();
            var frame = OverlayFrame(AccountUi("title.chatgpt"), 14);
            frame.Id = "account-preferences";
            frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = status.LoggedIn
                ? status.AccountIdMasked + (status.Expired ? " · " + AccountUi("status.expired") : " · " + AccountUi("status.connected"))
                : AccountUi("status.disconnected") });
            frame.Add(new Label { X = 2, Y = 3, Width = Dim.Fill(2), Text = AccountUi("account.noApiKey") });
            var browser = new Button { Id = "account-browser-login", X = 2, Y = 5, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = AccountUi("action.browserLogin") };
            browser.Accepted += (_, _) => StartLogin(false);
            var device = new Button { X = 2, Y = 7, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = AccountUi("action.deviceLogin") };
            device.Accepted += (_, _) => StartLogin(true);
            frame.Add(browser, device);
            if (status.LoggedIn)
            {
                var logout = new Button { X = 2, Y = 9, Text = AccountUi("action.logout") };
                logout.Accepted += (_, _) => ConfirmLogout(); frame.Add(logout);
            }
            var back = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = AccountUi("action.back") };
            back.Accepted += (_, _) => { CloseOverlay(); if (_accountFromPreferences) ShowPreferences("accounts_connections", categoryPage: true); };
            frame.Add(back); ApplyTheme(frame); _overlay = frame; _window.Add(frame);
            _preferencesReturnToCategory = _accountFromPreferences;
            _returnToChatGptAccount = false;
            (status.LoggedIn ? (status.Expired ? browser : back) : browser).SetFocus();
        }
        catch (Exception)
        {
            if (_accountFromPreferences) ShowPreferenceText(AccountUi("title.chatgpt"), AccountUi("error.readAccount"));
            else ShowMessage(AccountUi("error.readAccount"));
        }
    }

    private void ConfirmLogout()
    {
        CloseOverlay();
        var frame = OverlayFrame(AccountUi("account.logoutQuestion"), 8);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = AccountUi("account.logoutQuestion") });
        var confirm = new Button { X = 2, Y = 3, Text = AccountUi("action.confirmLogout") };
        confirm.Accepted += (_, _) =>
        {
            try { _account!.Logout(); CloseOverlay(); ShowAccount(); }
            catch (Exception) { ShowMessage(AccountUi("error.logout")); }
        };
        var cancel = new Button { X = 2, Y = 5, Text = AccountUi("action.cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowAccount(); };
        frame.Add(confirm, cancel); ApplyTheme(frame); _overlay = frame; _window!.Add(frame);
        _preferencesReturnToCategory = _accountFromPreferences; _returnToChatGptAccount = _accountFromPreferences; cancel.SetFocus();
    }

    private void StartLogin(bool deviceCode)
    {
        CloseOverlay();
        var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        var frame = OverlayFrame(AccountUi("title.chatgpt"), 16);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = AccountUi("help.login") });
        // Read-only, selectable OAuth instructions; no editing or document model is needed.
#pragma warning disable CS0618
        var progress = new TextView { X = 2, Y = 4, Width = Dim.Fill(2), Height = Dim.Fill(4), ReadOnly = true, WordWrap = true,
            Text = AccountUi("chatgpt.phase.preparing") };
#pragma warning restore CS0618
        var cancel = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = AccountUi("action.cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowAccount(); };
        frame.Add(progress, cancel); ApplyTheme(frame); _overlay = frame; _window!.Add(frame);
        _preferencesReturnToCategory = _accountFromPreferences; _returnToChatGptAccount = _accountFromPreferences; cancel.SetFocus();
        void Update(Action action) => _uiActions.Enqueue(() => { if (ReferenceEquals(_overlay, frame) && !cancellation.IsCancellationRequested) action(); });
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _account!.LoginAsync(deviceCode, text => Update(() => progress.Text = text), cancellation.Token);
                Update(() => { CloseOverlay(); ShowMessage(result.LoggedIn ? AccountUi("result.loginOk") : AccountUi("result.loginFailed")); });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception) { Update(() => { CloseOverlay(); ShowMessage(AccountUi("error.login")); }); }
            finally { cancellation.Dispose(); }
        });
    }

    private void ShowModelPicker(int page = 0, bool refreshCatalog = true, string query = "", ModelPolicyKeyDto? highlighted = null)
    {
        if (_window is null || _overlay is not null) return;
        _policies.ReloadModels();
        IReadOnlyList<OpenRouterPickerModel> routerModels = [];
        ProviderConnectionMenuRow[] routerProviders = [];
        string? routerError = null;
        if (_providerConnectionHost is ITuiOpenRouterCatalogHost routerCatalog)
        {
            try
            {
                routerProviders = _providerConnectionHost.List(CancellationToken.None)
                    .Where(row => row.Backend == "openrouter" && row.CanDiscoverModels).ToArray();
                routerModels = routerCatalog.CachedOpenRouterPickerModels(CancellationToken.None);
            }
            catch (Exception) { routerError = _localization.Resolve("openrouter.catalog.failed"); }
        }
        var configured = _policies.Models.ToArray();
        var available = configured.Concat(routerModels
                .Where(model => !configured.Any(existing => existing.Id == model.Id))
                .Select(model => new ModelRegistryModelDescriptor(model.Id, model.ProviderId, 0, 0, 0)))
            .Where(m => _policies.IsVisibleInPicker(m.ProviderId, m.Id)).ToArray();
        string ModelName(ModelRegistryModelDescriptor model) => CatalogDisplay(_policies.DisplayName(model.Id)
            ?? routerModels.FirstOrDefault(row => row.ProviderId == model.ProviderId && row.Id == model.Id)?.Name
            ?? model.Id, 160);
        string ProviderLabel(ModelRegistryModelDescriptor model) =>
            routerProviders.Any(row => row.ProviderId == model.ProviderId)
                ? model.ProviderId == "openrouter" ? "OpenRouter" : "OpenRouter · " + CatalogDisplay(model.ProviderId, 80)
                : CatalogDisplay(model.ProviderId, 80);
        string ModelRow(ModelRegistryModelDescriptor model) => (_selectedModel == model.Id ? " ✓  " : "    ")
            + CatalogDisplay(_policies.DisplayName(model.Id) ?? model.Id, 160) + "   ·   " + ProviderLabel(model);
        var models = available;
        var frame = OverlayFrame(_localization.Resolve("shell.select_model"), Math.Clamp(available.Length + 9, 12, 20));
        frame.Id = "omni-model-palette";
        frame.Width = Dim.Percent(66);
        frame.ViewportSettings |= ViewportSettingsFlags.Transparent;
        frame.SubViews.Single(view => view.Id == "omni-menu-title").Width = Dim.Fill(10);
        var feedback = new Label { Id = "omni-palette-feedback", X = 2, Y = 1, Width = Dim.Fill(4),
            Text = routerError ?? (routerProviders.Length > 0 && routerModels.Count == 0
                ? _localization.Resolve("openrouter.picker.notLoaded")
                : models.Length == 0 ? _localization.Resolve("shell.no_visible_models_check_f4_models") : "") };
        frame.Add(feedback);
        var searchLabel = new Label { Id = "omni-heading", X = 2, Y = 2, Width = 8, Text = _localization.Resolve("shell.search") };
        var search = new TextField { Id = "omni-palette-search", X = 10, Y = 2, Width = Dim.Fill(15), Text = query };
        var searchHint = new Label { Id = "omni-help", X = Pos.AnchorEnd(12), Y = 2, Width = 10, Text = "Ctrl+F" };
        var list = new ListView { Id = "omni-model-list", X = 2, Y = 4, Width = Dim.Fill(4), Height = Dim.Fill(4),
            CanFocus = models.Length > 0, ShowMarks = false };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(models.Select(ModelRow)));
        var position = new Label { Id = "omni-heading", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(4) };
        void UpdatePosition() => position.Text = models.Length == 0 ? _localization.Resolve("shell.no_matches_check_search_or_f4")
            : $"{(list.SelectedItem ?? 0) + 1} / {models.Length}  ·  " + _localization.Resolve("shell.navigate_enter_select");
        list.ValueChanged += (_, _) => UpdatePosition();
        void FilterModels()
        {
            var text = search.Text.ToString().Trim();
            models = available.Where(model => (model.Id + " " + ModelName(model) + " " + ProviderLabel(model))
                .Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(models.Select(ModelRow)));
            list.CanFocus = models.Length > 0;
            if (models.Length > 0) list.SelectedItem = 0;
            list.EnsureSelectedItemVisible();
            UpdatePosition();
            if (models.Length == 0) position.Text = available.Length == 0
                ? _localization.Resolve("shell.no_visible_models_f4_to_configure")
                : _localization.Resolve("shell.no_matches_change_the_search");
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
            var key = new ModelPolicyKeyDto(model.ProviderId, model.Id);
            try
            {
                if (!configured.Any(existing => existing.Id == model.Id && existing.ProviderId == model.ProviderId)
                    && _providerConnectionHost is ITuiOpenRouterCatalogHost selectedCatalog)
                {
                    selectedCatalog.RegisterOpenRouterModels(model.ProviderId, [model.Id], CancellationToken.None);
                    _policies.ReloadModels();
                }
                var safe = _policies.Get(key, CancellationToken.None) is null;
                _policies.Select(WorkspaceSelectionId, key, model.Id, safe, CancellationToken.None);
                _turnStatus = safe ? _localization.Resolve("shell.safe_mode_observeonly") : null;
                RefreshSelectedModel(); MarkExplicitModelSelection(); CloseOverlay(); RenderState();
            }
            catch (Exception) { feedback.Text = _localization.Resolve("shell.could_not_select_model"); }
        };
        ModelPolicyKeyDto? HighlightedModel() => list.SelectedItem is int at && at >= 0 && at < models.Length
            ? new(models[at].ProviderId, models[at].Id) : null;
        FilterModels();
        var initial = Array.FindIndex(models, model => highlighted is null ? model.Id == _selectedModel
            : model.Id == highlighted.ModelId && model.ProviderId == highlighted.ProviderId);
        if (initial < 0) initial = Array.FindIndex(models, model => model.Id == _selectedModel);
        if (models.Length > 0) list.SelectedItem = Math.Max(0, initial);
        UpdatePosition();
        var close = new Button { Id = "omni-palette-action", X = Pos.AnchorEnd(8), Y = 0, Width = 6, Text = "Esc" };
        close.Accepted += (_, _) => CloseOverlay();
        var manage = new Button { Id = "omni-palette-action", X = 2, Y = Pos.AnchorEnd(1), Text = _localization.Resolve("shell.manage_models_ctrl_m") };
        manage.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(); };
        var refresh = new Button { Id = "omni-palette-refresh", X = 2, Y = Pos.AnchorEnd(2),
            Text = _localization.Resolve(routerProviders.Length > 0 ? "openrouter.picker.refreshChatGpt" : "shell.refresh_models"),
            Enabled = _policies.SupportsDiscovery };
        refresh.Accepted += (_, _) => RefreshAccountModels(frame, feedback, page, picker: true, highlighted: HighlightedModel);
        if (routerProviders.Length > 0)
        {
            var routerRefresh = new Button { Id = "omni-palette-openrouter-refresh", X = Pos.Right(refresh) + 1,
                Y = Pos.AnchorEnd(2), Text = _localization.Resolve("openrouter.picker.refresh") };
            routerRefresh.Accepted += (_, args) =>
            {
                args.Handled = true;
                RefreshOpenRouterPicker(frame, feedback, routerProviders, search, HighlightedModel);
            };
            frame.Add(routerRefresh);
        }
        ApplyTheme(_window, dimmed: true);
        if (_conversation is not null) _conversation.Dimmed = true;
        frame.Add(searchLabel, search, searchHint, list, position, close, manage, refresh); ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        if (models.Length > 0) list.SetFocus(); else search.SetFocus();
        var refreshSubscription = refreshCatalog && _policies.SupportsDiscovery;
        if (refreshSubscription && routerProviders.Length > 0)
        {
            try { refreshSubscription = (_account ??= _accountOverride ?? new TuiAccountHost()).Status().LoggedIn; }
            catch (Exception) { refreshSubscription = false; }
        }
        if (refreshSubscription) RefreshAccountModels(frame, feedback, page, picker: true, highlighted: HighlightedModel);
    }

    private void ShowModelPolicies(int page = 0, bool refreshCatalog = true, int focusIndex = -1, int focusColumn = 0)
    {
        if (_window is null || _overlay is not null) return;
        _policies.ReloadModels();
        var frame = OverlayFrame(_localization.Resolve("shell.models_title"), 18);
        var pageSize = Math.Max(1, Math.Min(18, _window.Frame.Height - 2) - 7);
        var models = _policies.Models.ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, models.Length - pageSize));
        var feedback = new Label { X = 2, Y = 1, Width = Dim.Fill(2), Text = (_catalogNotice ?? $"{models.Length} " + _localization.Resolve("shell.models"))
            + _localization.Resolve("shell.rows_actions_in_f3") };
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
                + " · " + (record?.Category ?? (_localization.Resolve("shell.setup_required")));
            var row = y++;
            var index = page + row - 2;
            var select = new Button { Id = "model-policy-" + descriptor.Id, X = 1, Y = row, Width = Dim.Fill(13), TextAlignment = Alignment.Start, Text = label };
            BindCell(select, index, 0);
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
                var limits = new Button { Id = "model-policy-limits-" + descriptor.Id,
                    X = Pos.AnchorEnd(17), Y = row, Width = 5, Text = "⚙" };
                BindCell(limits, index, 2);
                limits.Accepted += (_, _) => ShowModelPolicyLimits(key, record);
                frame.Add(limits);
                var delete = new Button { Id = "delete-policy-" + descriptor.Id,
                    X = Pos.AnchorEnd(6), Y = row, Width = 4, Text = "🗑" };
                BindCell(delete, index, 3);
                delete.Accepted += (_, _) => ConfirmDeletePolicy(key, record.Revision, page);
                frame.Add(delete);
            }
        }
        var register = new Button { X = 2, Y = Pos.AnchorEnd(4), Text = _localization.Resolve("shell.refresh_models") };
        register.Accepted += (_, _) => { CloseOverlay(); _catalogNotice = null; ShowModelPolicies(); };
        frame.Add(register);
        var settings = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = _localization.Resolve("shell.settings") };
        settings.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("models_execution", categoryPage: true); };
        frame.Add(settings);
        var close = new Button { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = _localization.Resolve("shell.close") };
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

    private void ShowModelPolicyLimits(ModelPolicyKeyDto key, ModelPolicyRecordDto record)
    {
        if (_window is null) return;
        CloseOverlay();
        var labels = ModelPolicyProfileLabels.For(_locale);
        var frame = OverlayFrame(labels.Title, 48);
        frame.Id = "model-profile-limits";
        frame.Width = Dim.Percent(92);
        frame.Height = Dim.Fill(2);
        var panel = new View { Id = "model-profile-limits-panel", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = Dim.Fill(5),
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var y = 0;
        panel.Add(new Label { Id = "model-profile-key", X = 0, Y = y++, Width = Dim.Fill(2),
            Text = key.ProviderId + "/" + key.ModelId });

        TextField AddNumber(string id, string label, string value)
        {
            panel.Add(new Label { Id = id + "-label", X = 0, Y = y++, Width = Dim.Fill(2), Text = label });
            var field = new TextField { Id = id, X = 0, Y = y++, Width = 12, Text = value };
            panel.Add(field);
            return field;
        }

        var steps = AddNumber("profile-limits-steps", labels.Steps, record.MaxModelStepsPerTurn.ToString());
        var calls = AddNumber("profile-limits-tool-calls", labels.ToolCalls, record.MaxToolCallsPerTurn.ToString());
        var visible = AddNumber("profile-limits-visible-tools", labels.VisibleTools, record.MaxVisibleTools.ToString());
        var generationSeconds = AddNumber("profile-limits-generation-seconds", labels.GenerationDeadline,
            record.MaxGenerationSeconds.ToString());
        panel.Add(new Label { Id = "profile-limits-bounds", X = 0, Y = y, Width = Dim.Fill(2), Height = 2,
            Text = labels.Bounds });
        y += 2;
        var permittedCapabilities = record.CapabilityCeiling.ToHashSet(StringComparer.Ordinal);
        var capabilityChecks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        panel.Add(new Label { Id = "profile-limits-capabilities-heading", X = 0, Y = y++, Width = Dim.Fill(2),
            Text = labels.Capabilities });
        var basicCapabilities = new[] { "WorkspaceRead", "Search", "ReferenceResolve", "AgentMailboxWait" };
        var advancedCapabilities = new[]
        {
            "PlanProposal", "PatchExisting", "CreateFile", "ReplaceFile", "DeleteFile", "MoveOrRename",
            "ValidationProcess", "GeneralProcess", "Shell", "Network",
        };
        AddCapabilityGroup("basics", basicCapabilities);
        AddCapabilityGroup("advanced", advancedCapabilities);

        void AddCapabilityGroup(string groupId, IReadOnlyList<string> capabilities)
        {
            var allowed = capabilities.Where(permittedCapabilities.Contains).ToArray();
            if (allowed.Length == 0) return;
            panel.Add(new Label { Id = "profile-limits-" + groupId + "-heading", X = 0, Y = y++,
                Width = Dim.Fill(2), Text = ModelPolicyProfileText.Get(_locale, groupId) });
            foreach (var capability in allowed)
            {
                var checkbox = new CheckBox
                {
                    Id = "profile-limits-capability-" + capability,
                    X = 0, Y = y++, Width = Dim.Fill(2),
                    Text = ModelPolicyProfileText.Capability(_locale, capability),
                    Value = CheckState.Checked,
                };
                capabilityChecks.Add(capability, checkbox); panel.Add(checkbox);
            }
        }

        panel.Add(new Label { Id = "profile-limits-mutation-heading", X = 0, Y = y++, Width = Dim.Fill(2),
            Text = labels.Mutation });
        panel.Add(new Label { Id = "profile-limits-mutation-mode-label", X = 0, Y = y++, Width = Dim.Fill(2),
            Text = labels.MutationMode });
        var mode = AddRadioOptions("mode", new[]
        {
            ("None", "mode_none"), ("PatchExisting", "mode_patch"),
            ("PatchAndCreate", "mode_patch_create"), ("Full", "mode_full"),
        }, record.MutationMode);
        var files = AddNumber("profile-limits-files", labels.MaxFiles, record.MaxFilesPerTurn.ToString());
        var lines = AddNumber("profile-limits-changed-lines", labels.MaxLines, record.MaxChangedLinesPerTurn.ToString());
        var ratio = AddNumber("profile-limits-rewrite-ratio", labels.RewriteRatio,
            record.MaxRewriteRatio.ToString("R", PreferenceInput.Culture(_locale)));
        panel.Add(new Label { Id = "profile-limits-delete-policy-label", X = 0, Y = y++, Width = Dim.Fill(2),
            Text = labels.DeletePolicy });
        var deletePolicy = AddRadioOptions("delete", new[]
            { ("Deny", "deny"), ("Ask", "confirm"), ("Allow", "allow") }, record.DeletePolicy);
        panel.Add(new Label { Id = "profile-limits-move-policy-label", X = 0, Y = y++, Width = Dim.Fill(2),
            Text = labels.MovePolicy });
        var movePolicy = AddRadioOptions("move", new[]
            { ("Deny", "deny"), ("Ask", "confirm"), ("Allow", "allow") }, record.MoveOrRenamePolicy);
        var priorRead = AddFlag("prior-read", labels.PriorRead, record.RequirePriorRead);
        var versionToken = AddFlag("version-token", labels.VersionToken, record.RequireExpectedVersionToken);
        var validation = AddFlag("validation", labels.Validation, record.RequirePostEditValidation);
        var orchestration = AddFlag("orchestration", labels.AllowOrchestration, record.AllowOrchestration);
        orchestration.Enabled = record.AllowOrchestration;
        panel.Add(new Label { Id = "profile-limits-provider-reasoning", X = 0, Y = y++, Width = Dim.Fill(2), Height = 2,
            Text = labels.ProviderReasoning });
        ConfigurePreferenceScroll(panel, y + 1);
        var cancel = new Button { Id = "profile-limits-cancel", X = 2, Y = Pos.AnchorEnd(2),
            Text = _localization.Resolve("shell.cancel") };
        var status = new Label { Id = "profile-limits-status", X = 2, Y = Pos.AnchorEnd(4),
            Width = Dim.Fill(4), Height = 1 };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(refreshCatalog: false); };
        var save = new Button { Id = "profile-limits-save", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = labels.Save };
        save.Accepted += (_, _) =>
        {
            if (!int.TryParse(steps.Text.ToString(), out var stepCount)
                || !int.TryParse(calls.Text.ToString(), out var callCount)
                || !int.TryParse(visible.Text.ToString(), out var visibleCount)
                || !int.TryParse(generationSeconds.Text.ToString(), out var generationLimit)
                || !int.TryParse(files.Text.ToString(), out var fileCount)
                || !int.TryParse(lines.Text.ToString(), out var lineCount)
                || !double.TryParse(ratio.Text.ToString(), System.Globalization.NumberStyles.Float,
                    PreferenceInput.Culture(_locale), out var rewriteRatio)
                || stepCount is < 1 or > 8 || callCount is < 1 or > 64
                || generationLimit is < 1 or > 3600)
            { status.Text = labels.Invalid; return; }
            try
            {
                var capabilityNames = capabilityChecks.Where(pair => pair.Value.Value == CheckState.Checked)
                    .Select(pair => pair.Key).ToArray();
                _policies.SetProfileControls(key, record.Revision, stepCount, callCount, visibleCount,
                    capabilityNames, Selected(mode), fileCount, lineCount, rewriteRatio,
                    Selected(deletePolicy), Selected(movePolicy), priorRead.Value == CheckState.Checked,
                    versionToken.Value == CheckState.Checked, validation.Value == CheckState.Checked,
                    orchestration.Value == CheckState.Checked, generationLimit, CancellationToken.None);
            }
            catch (ModelPolicyRevisionConflict)
            { status.Text = _localization.Resolve("shell.could_not_save_the_policy_reopen_the_model"); return; }
            catch (Exception) { status.Text = labels.Failed; return; }
            CloseOverlay(); ShowModelPolicies(refreshCatalog: false);
        };
        frame.Add(panel, status, cancel, save); ApplyTheme(frame); _overlay = frame; _window.Add(frame); steps.SetFocus();

        (string Id, CheckBox Control)[] AddRadioOptions(string group,
            (string Id, string LabelId)[] options, string selected)
        {
            var controls = new List<(string Id, CheckBox Control)>();
            foreach (var (id, labelId) in options)
            {
                var control = new CheckBox
                {
                    Id = "profile-limits-choice-" + group + "-" + id,
                    X = 0, Y = y++, Width = Dim.Fill(2),
                    Text = ModelPolicyProfileText.Get(_locale, labelId),
                    RadioStyle = true,
                    Value = string.Equals(id, selected, StringComparison.OrdinalIgnoreCase)
                        ? CheckState.Checked : CheckState.UnChecked,
                };
                control.ValueChanged += (_, args) =>
                {
                    if (args.NewValue != CheckState.Checked) return;
                    foreach (var other in controls)
                        if (!ReferenceEquals(other.Control, control)) other.Control.Value = CheckState.UnChecked;
                };
                controls.Add((id, control));
                panel.Add(control);
            }
            return controls.ToArray();
        }

        CheckBox AddFlag(string id, string label, bool isChecked)
        {
            var control = new CheckBox { Id = "profile-limits-" + id,
                X = 0, Y = y++, Width = Dim.Fill(2), Text = label,
                Value = isChecked ? CheckState.Checked : CheckState.UnChecked };
            panel.Add(control); return control;
        }

        static string Selected((string Id, CheckBox Control)[] options) =>
            options.Single(option => option.Control.Value == CheckState.Checked).Id;
    }

    private void ConfirmDeletePolicy(ModelPolicyKeyDto key, long revision, int page)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(_localization.Resolve("shell.delete_policy"), 10);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Text = key.ToString() },
            new Label { X = 2, Y = 4, Width = Dim.Fill(4),
                Text = _localization.Resolve("shell.deletes_the_policy_not_the_model_or_account") });
        var cancel = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = _localization.Resolve("shell.cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(page, false); };
        var confirm = new Button { X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = _localization.Resolve("shell.delete") };
        confirm.Accepted += (_, _) =>
        {
            try { _policies.Delete(key, revision, CancellationToken.None); }
            catch (Exception) { CloseOverlay(); ShowMessage(_localization.Resolve("shell.could_not_delete_the_policy_refresh_the_list")); return; }
            CloseOverlay(); ShowModelPolicies(page, false);
        };
        frame.Add(cancel, confirm); ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        cancel.SetFocus();
    }

    private void RefreshAccountModels(FrameView frame, Label feedback, int page, bool picker = false, Func<ModelPolicyKeyDto?>? highlighted = null)
    {
        if (_catalogCancellation is not null) return;
        try
        {
            _account ??= _accountOverride ?? new TuiAccountHost();
            if (!_account.Status().LoggedIn) { feedback.Text = _localization.Resolve("shell.sign_in_with_f4_to_discover_chatgpt_models"); return; }
        }
        catch (Exception) { feedback.Text = _localization.Resolve("shell.could_not_read_the_chatgpt_session"); return; }
        feedback.Text = _localization.Resolve("shell.loading_available_models");
        var retry = frame.SubViews.OfType<Button>().FirstOrDefault(view => view.Id == "omni-palette-refresh");
        if (retry is not null) retry.Enabled = false;
        var cancellation = _catalogCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = cancellation.Token;
        bool IsCurrent() => ReferenceEquals(_overlay, frame) && ReferenceEquals(_catalogCancellation, cancellation);
        void Finish()
        {
            _catalogCancellation = null;
            if (retry is not null) retry.Enabled = true;
            cancellation.Dispose();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = await _account.ListModelsAsync(token).ConfigureAwait(false);
                _uiActions.Enqueue(() =>
                {
                    if (!IsCurrent()) return;
                    try
                    {
                        var focusedModel = highlighted?.Invoke();
                        var search = frame.SubViews.OfType<TextField>().FirstOrDefault(v => v.Id == "omni-palette-search");
                        var query = search?.Text.ToString() ?? "";
                        var editingSearch = search?.HasFocus == true;
                        _catalogNotice = _policies.ApplyChatGptCatalog(catalog, WorkspaceSelectionId, token)
                            ?? _localization.Resolve("shell.catalog_refreshed");
                        var fromPreferences = _preferencesReturnToCategory;
                        Finish(); RefreshSelectedModel(); CloseOverlay();
                        if (picker)
                        {
                            ShowModelPicker(page, false, query, focusedModel);
                            if (editingSearch) FindPreferenceView(_overlay, "omni-palette-search")?.SetFocus();
                        }
                        else ShowModelPolicies(page, false);
                        _preferencesReturnToCategory = fromPreferences;
                    }
                    catch (Exception)
                    {
                        if (ReferenceEquals(_overlay, frame))
                            feedback.Text = _localization.Resolve("shell.could_not_save_catalog_selection_preserved");
                        if (IsCurrent()) Finish();
                    }
                });
            }
            catch (Exception)
            {
                _uiActions.Enqueue(() =>
                {
                    if (!IsCurrent()) return;
                    feedback.Text = picker
                        ? _localization.Resolve("shell.discovery_failed_selection_preserved_reopen_the_picker")
                        : _localization.Resolve("shell.discovery_failed_selection_preserved_retry_refresh");
                    Finish();
                });
            }
        });
    }

    private void ShowModelPolicySetup(ModelPolicyDraftDto draft)
    {
        if (_window is null) return;
        var model = ModelPolicySetupModel.From(draft.Key.ToString(), draft.RecommendedCategory, draft.Warnings);
        var expectedRevision = _policies.Get(draft.Key, CancellationToken.None)?.Revision ?? 0;
        var frame = OverlayFrame(_localization.Resolve("shell.setup_model"), 14);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Text = model.ModelIdentity });
        var y = 3;
        foreach (var warning in model.Warnings) frame.Add(new Label { X = 1, Y = y++, Width = Dim.Fill(2), Text = "! " + warning });
        foreach (var choice in model.Choices)
        {
            var button = new Button { X = 1, Y = y++, Width = Dim.Fill(2), TextAlignment = Alignment.Start, Text = (choice.Recommended ? "[recommended] " : "") + choice.Category + " — " + choice.Description };
            button.Accepted += (_, _) =>
            {
                try { _policies.Set(draft.Key, expectedRevision, choice.Category, null, CancellationToken.None); }
                catch (Exception) { CloseOverlay(); ShowMessage(_localization.Resolve("shell.could_not_save_the_policy_reopen_the_model")); return; }
                RefreshSelectedModel();
                CloseOverlay(); ShowModelPolicies();
            };
            frame.Add(button);
        }
        var cancel = new Button { X = 1, Y = y, Text = _localization.Resolve("shell.cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowModelPolicies(); };
        frame.Add(cancel); ApplyTheme(frame); _overlay = frame; _window!.Add(frame); frame.SetFocus();
    }

    private void RefreshSelectedModel()
    {
        var selection = _policies.CurrentSelection(WorkspaceSelectionId, CancellationToken.None)?.ModelId;
        if (selection != _selectedModel) MarkExplicitModelSelection();
        _selectedModel = selection;
        if (_header is not null) _header.Text = HeaderText();
        if (_status is not null) _status.Text = StatusText();
    }

    private void MarkExplicitModelSelection()
    {
        _observedModel = null;
        _selectionObservationSession = _cursorSession;
        _selectionObservationAfterSequence = _lastSequence;
    }

    /// <summary>Renders JSON as indented "label: value" lines with strings decoded (no \u escapes).</summary>
    internal static string ReadableJson(System.Text.Json.JsonElement element, Func<string, string?>? labels = null)
    {
        var builder = new System.Text.StringBuilder();
        string Label(string name)
        {
            if (labels?.Invoke(name) is { } known) return known;
            var spaced = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
            return spaced.Length == 0 ? name : char.ToUpperInvariant(spaced[0]) + spaced[1..];
        }
        static string Scalar(System.Text.Json.JsonElement value) => value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
            System.Text.Json.JsonValueKind.Null => "—",
            System.Text.Json.JsonValueKind.True => "✓",
            System.Text.Json.JsonValueKind.False => "✗",
            _ => value.GetRawText(),
        };
        void Write(System.Text.Json.JsonElement value, int depth, string? prefix)
        {
            var pad = new string(' ', depth * 2);
            switch (value.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                case System.Text.Json.JsonValueKind.Array:
                    if (prefix is not null) builder.Append(pad).Append(prefix).AppendLine(":");
                    var childDepth = prefix is null ? depth : depth + 1;
                    if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var property in value.EnumerateObject()) Write(property.Value, childDepth, Label(property.Name));
                    else
                        foreach (var item in value.EnumerateArray()) Write(item, childDepth, "•");
                    break;
                default:
                    var text = Scalar(value);
                    if (text.Contains('\n'))
                    {
                        builder.Append(pad).Append(prefix).AppendLine(":");
                        foreach (var line in text.Replace("\r\n", "\n").Split('\n')) builder.Append(pad).Append("  ").AppendLine(line);
                    }
                    else builder.Append(pad).Append(prefix is null ? "" : prefix == "•" ? "• " : prefix + ": ").AppendLine(text);
                    break;
            }
        }
        Write(element, 0, null);
        return builder.ToString().TrimEnd();
    }

    private void ShowMessage(string message)
    {
        if (_window is null) return;
        CloseOverlay();
        // Query results arrive as JSON; show them as decoded, labelled text instead of escaped JSON.
        var title = _localization.Resolve("shell.notice");
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.EnumerateObject().ToArray() is [var only]
                && only.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (ShellUiResources.ContainsKey("shell.json_title." + only.Name)) title = _localization.Resolve("shell.json_title." + only.Name);
                message = only.Value.GetString() ?? "";
            }
            else message = ReadableJson(root, name => ShellUiResources.ContainsKey("shell.json_label." + name)
                ? _localization.Resolve("shell.json_label." + name) : null);
        }
        catch (System.Text.Json.JsonException) { }
        var lines = message.Replace("\r\n", "\n").Split('\n');
        var available = Math.Max(1, (_window.Frame.Height > 0 ? _window.Frame.Height : 25) - 9);
        var visible = lines.Take(available).ToArray();
        if (lines.Length > available) visible[^1] = "…";
        var frame = OverlayFrame(title, Math.Max(6, visible.Length + 5));
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Height = visible.Length, Text = string.Join("\n", visible) });
        var ok = new Button { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Text = _localization.Resolve("shell.ok") };
        ok.Accepted += (_, e) => { e.Handled = true; CloseOverlay(); }; frame.Add(ok); ApplyTheme(frame); _overlay = frame; _window.Add(frame); frame.SetFocus();
    }

    private void CloseOverlay()
    {
        if (_overlay is null || _window is null) return;
        if (_themeBeforePreview is { } originalTheme) { _theme = originalTheme; _themeBeforePreview = null; }
        if (_suspendedSecurityForm is { } securityDraft)
        {
            _retiredOverlays.Add(securityDraft); _suspendedSecurityForm = null; _cancelSecurityConfirmation = null;
        }
        _preferencesCenterActive = false;
        _preferencesCategoryPage = false;
        _preferencesReturnToCategory = false;
        _animationPreview = null;
        ClearProviderApiKey();
        CancelProviderConnectionOperation();
        var catalog = _catalogCancellation; _catalogCancellation = null;
        if (catalog is not null) { catalog.Cancel(); catalog.Dispose(); }
        var login = _loginCancellation;
        _loginCancellation = null;
        if (login is not null) { try { login.Cancel(); } catch (ObjectDisposedException) { } }
        CancelClaudeLoginOperation();
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

    private string? _modeOverride;
    private string? _modeAtOverride;
    private string? _permissionLevelCache;

    /// <summary>
    /// Mode shown to the user: the active Run's mode, or the next Run's default right after the
    /// user changed it (the projection only learns it on the next run.created event).
    /// </summary>
    private string CurrentModeCode()
    {
        var projected = _state.StatusLine.Mode;
        if (_modeOverride is null) return projected;
        if (!string.Equals(projected, _modeAtOverride, StringComparison.OrdinalIgnoreCase)) { _modeOverride = null; return projected; }
        return _modeOverride;
    }

    private static string ModeCommandCode(string mode) => mode.ToLowerInvariant() switch
    {
        "plan" => "plan", "orchestrate" or "orq" => "orq", _ => "act",
    };

    /// <summary>Shift+Tab: PLAN → ACT → ORQ. Changes the active Run, or the next Run's default when idle.</summary>
    private void CycleMode()
    {
        var next = ModeCommandCode(CurrentModeCode()) switch { "plan" => "act", "act" => "orq", _ => "plan" };
        if (_client is not OmniCore.Protocol.ITrustedUserActionClient trusted)
        { ShowMessage(_localization.Resolve("shell.this_client_cannot_change_local_authority")); return; }
        var runAck = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "run.mode.select")
            + "," + JsonObj.Field("mode", next) + ",\"effort\":\"standard\"}"), CancellationToken.None);
        var target = next == "orq" ? "orchestrate" : next;
        if (runAck.Status == "ok")
        {
            _turnStatus = _localization.Resolve("shell.mode_switched_run", "mode", StatusLinePresentation.ModeLabel(target, _locale));
            PollEvents(); RenderState(); return;
        }
        var preference = _client.Query("modePreference", CancellationToken.None)?.Json ?? "{}";
        if (!TryReadLong(preference, "revision", out var revision))
        { ShowMessage(_localization.Resolve("shell.could_not_read_the_mode_revision")); return; }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "mode.default.set")
            + "," + JsonObj.Field("mode", next) + ",\"expectedRevision\":"
            + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"), CancellationToken.None);
        if (ack.Status != "ok") { ShowMessage(ack.Error ?? _localization.Resolve("shell.mode_change_rejected")); return; }
        _modeAtOverride = _state.StatusLine.Mode; _modeOverride = target;
        _turnStatus = _localization.Resolve("shell.mode_switched_next", "mode", StatusLinePresentation.ModeLabel(target, _locale));
        RenderState();
    }

    /// <summary>Permission level (Restrict / Ask / Free) shown next to the mode; cached until changed in F4.</summary>
    private string? PermissionLevelLabel()
    {
        if (_permissionLevelCache is not null) return _permissionLevelCache.Length == 0 ? null : _permissionLevelCache;
        try
        {
            var host = _securityPreferencesOverride ?? SecurityPreferencesHost.Create(Environment.CurrentDirectory);
            var key = host.Read().Profile switch
            {
                "Conservative" => "level_restrict", "Balanced" => "level_ask", _ => "level_free",
            };
            _permissionLevelCache = _localization.Resolve("shell.permission_level") + _localization.Resolve("securityPreferences." + key);
        }
        catch (Exception) { _permissionLevelCache = ""; }
        return _permissionLevelCache.Length == 0 ? null : _permissionLevelCache;
    }

    internal void InvalidatePermissionLevel() => _permissionLevelCache = null;

    private string ReadableModeField(string json, string field)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? StatusLinePresentation.ModeLabel(value.GetString() == "orq" ? "orchestrate" : value.GetString()!, _locale) : "—";
        }
        catch (System.Text.Json.JsonException) { return "—"; }
    }

    private static string ReadableJsonText(string json)
    {
        try { using var document = System.Text.Json.JsonDocument.Parse(json); return ReadableJson(document.RootElement); }
        catch (System.Text.Json.JsonException) { return json; }
    }

    private string CommandError(string? error)
    {
        if (error is null) return _localization.Resolve("shell.command_failed");
        if (error.StartsWith("command.prompt_not_found", StringComparison.Ordinal)) return _localization.Resolve("shell.command_unknown");
        if (error.StartsWith("command.disabled", StringComparison.Ordinal))
            return _localization.Resolve("shell.command_disabled") + (error.IndexOf(':') is var colon and > 0 && colon < error.Length - 1
                ? " " + DescribeDisabledCommand(error[(colon + 1)..]) : "");
        return _localization.ResolveWire(error);
    }

    /// <summary>Slash commands open the same screens as Preferences; leaving returns to the chat.</summary>
    private bool OpenClientView(ClientActionInvocation action)
    {
        var bare = action.Arguments.Count == 0;
        string? sessionQuery = action.ActionId switch
        {
            ClientActionIds.ContextInspect when bare => "context",
            ClientActionIds.ExtensionsInspect when bare => "extensions",
            ClientActionIds.SkillsInspect when bare => "skills",
            ClientActionIds.MemorySession when bare || action.Arguments is ["session"] => "memory.session",
            _ => null,
        };
        if (sessionQuery is not null)
        {
            CloseOverlay(); _viewFromChat = true; ShowSessionPreferences(sessionQuery); return true;
        }
        if (action.ActionId == ClientActionIds.CommandsInspect && bare)
        { _viewFromChat = true; ShowCommandPreferences(); return true; }
        if (action.ActionId == ClientActionIds.HooksInspect && bare)
        {
            var context = new ClientContext(_client, _localization, CliApp.PermissionsListing);
            var result = new ClientActionHandler().ExecuteAsync(action, context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            _viewFromChat = true;
            ShowPreferenceText(_localization.Resolve("sessionPreferences.title.hooks"), result.Text);
            return true;
        }
        if (action.ActionId == ClientActionIds.McpInspect && bare)
        {
            var context = new ClientContext(_client, _localization, CliApp.PermissionsListing);
            var result = new ClientActionHandler().ExecuteAsync(action, context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            _viewFromChat = true;
            ShowPreferenceText(_localization.Resolve("client.command.mcp"), result.Text);
            return true;
        }
        if (action.ActionId == ClientActionIds.HelpShow)
        {
            _viewFromChat = true; ShowHelp();
            return true;
        }
        if (action.ActionId == ClientActionIds.PermissionsList)
        {
            _viewFromChat = true;
            ShowPreferenceText(_localization.Resolve("shell.permissions_title"),
                (PermissionLevelLabel() ?? "") + "\n" + _localization.Resolve("shell.permissions_change_hint") + "\n\n" + CliApp.PermissionsListing());
            return true;
        }
        return false;
    }

    private bool _viewFromChat;

    /// <summary>Returns true (and goes back to the chat) when the current screen was opened by a slash command.</summary>
    private bool ReturnToChatIfOpenedFromChat()
    {
        if (!_viewFromChat) return false;
        _viewFromChat = false;
        CloseOverlay();
        _composer?.SetFocus();
        return true;
    }

    private string TuiHelpSection()
    {
        var lines = new List<string> { _localization.Resolve("client.help.title"), "", _localization.Resolve("shell.help_tui_section") };
        var aliases = new UserCommandAliases((_policyPreferencesOverride ?? PolicyPreferencesHost.Create(Environment.CurrentDirectory)).Read().CommandAliases);
        var rows = CommandCatalogPresentation.Read(_client.Query("commands", CancellationToken.None)?.Json, _localization)
            .Where(row => !TuiMenuCommands.IsMenuOwned(row.Name, aliases));
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            if (shown.Add(row.Name)) lines.Add("  /" + row.DisplayName + " — " + row.Description);
        foreach (var name in new[] { "new", "sessions", "resume-session", "clear", "compact", "act", "mode", "runmode", "ultracode", "reasoning", "agents", "delegate", "agent", "join", "fanout" })
            if (shown.Add(name)) lines.Add("  /" + HumanCommandAliases.DisplayName(name, _locale) + " — " + CommandDescription("/" + name));
        lines.Add(_localization.Resolve("shell.help_keys"));
        lines.Add("F6 — " + _localization.Resolve("shell.conversation.title"));
        return string.Join("\n", lines);
    }

    private void SendTrustedModeCommand(string command, string fields)
    {
        if (_client is not OmniCore.Protocol.ITrustedUserActionClient trusted)
        { ShowMessage(_localization.Resolve("shell.this_client_cannot_change_local_authority")); return; }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", command)
            + (fields.Length == 0 ? "" : "," + fields) + "}"), CancellationToken.None);
        if (ack.Status != "ok") ShowMessage(ack.Error ?? _localization.Resolve("shell.mode_change_rejected"));
        else ShowMessage(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Deferred
            ? _localization.Resolve("shell.the_active_turn_still_has_a_model_step_retry_after_it_completes")
            : _localization.Resolve("shell.mode_updated"));
    }

    private void HandleReasoningCommand(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            ShowMessage(_localization.Resolve("shell.usage_reasoning_default_run_kind_off_reset_budgettokens_or_reasoning_b"));
            return;
        }

        if (parts[0] == "boost")
        {
            if (_turnHost is null || parts.Length is < 2 or > 3
                || !TryBuildReasoningRequest(parts[1], parts.Length == 3 ? parts[2] : null,
                    out var boostKind, out var boostBudgetTokens)
                || boostKind is null)
            {
                ShowMessage(_localization.Resolve("shell.one_turn_boost_is_invalid_or_unavailable"));
                return;
            }
            if (!_turnHost.SetNextTurnReasoningBoost(boostKind, boostBudgetTokens))
            {
                ShowMessage(_localization.Resolve("shell.a_reasoning_boost_is_already_pending"));
                return;
            }
            ShowMessage(_localization.Resolve("shell.boost_queued_for_the_next_fresh_turn")
                + boostKind + (boostBudgetTokens is { } budget ? ":" + budget : ""));
            return;
        }

        if (parts.Length is < 2 or > 3 || parts[0] is not ("default" or "run"))
        {
            ShowMessage(_localization.Resolve("shell.usage_reasoning_default_run_kind_off_reset_budgettokens"));
            return;
        }
        var isRun = parts[0] == "run";
        var json = _client.Query(isRun ? "runReasoningPreference" : "reasoningPreference",
            CancellationToken.None)?.Json ?? "null";
        long revision = 0;
        if (json != "null" && !TryReadLong(json, "revision", out revision))
        {
            ShowMessage(_localization.Resolve("shell.could_not_read_the_reasoning_revision"));
            return;
        }
        if (parts[1] == "reset")
        {
            if (parts.Length != 2)
            {
                ShowMessage(_localization.Resolve("shell.reset_does_not_accept_a_budget"));
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
            ShowMessage(_localization.Resolve("shell.invalid_reasoning_kind_or_budget"));
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
            ShowMessage(_localization.Resolve("shell.this_client_cannot_change_reasoning"));
            return;
        }
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", command)
            + (fields.Length == 0 ? "" : "," + fields) + "}"), CancellationToken.None);
        if (ack.Status != "ok")
            ShowMessage(ack.Error ?? _localization.Resolve("shell.reasoning_change_rejected"));
        else if (ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Deferred)
            ShowMessage(_localization.Resolve("shell.the_active_turn_still_has_a_model_step_retry_after_it_completes"));
        else
            ShowMessage(_localization.Resolve("shell.reasoning_updated"));
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

    private bool SubmitSessionInput(string text)
    {
        var ack = SendCommand("session.input", JsonObj.Field("text", text));
        if (ack.Status == "ok" && ack.Outcome?.Kind != RuntimeCommandOutcomeKind.Rejected) return true;
        ShowMessage(CommandError(ack.Error));
        return false;
    }

    private CommandAck SendCommand(string command, string fields) => _client.Send(
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
