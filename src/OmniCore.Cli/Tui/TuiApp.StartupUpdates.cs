namespace OmniCore.Cli;

using OmniCore.Host;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    internal ReleaseStartupUpdates? StartupUpdatesOverride { get; set; }
    internal string? StartupLaunchIdOverride { get; set; }
    internal bool UpdateRestartRequested => _updateRestartRequested;
    private ReleaseStartupUpdates? _startupUpdates;
    private bool _updateRestartRequested;
    private Button? _startupNotice;
    private Action? _refreshStartupUpdateView;
    private CancellationTokenSource? _startupPreparation;
    private Task? _startupPreparationTask;

    private ReleaseStartupUpdates? StartupUpdates()
    {
        if (StartupUpdatesOverride is { } injected) return injected.Supported ? injected : null;
        if (Environment.GetEnvironmentVariable("OMNICORE_STARTUP_MANAGED") != "1") return null;
        try
        {
            var service = _startupUpdates ??= ReleaseStartupUpdates.CreateDefault();
            return service.Supported ? service : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return null; }
    }

    private static StartupUpdatePreferences ReadStartupPreferences(ReleaseStartupUpdates? service)
    {
        try { return service?.Preferences() ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException) { return new(); }
    }

    private static StartupUpdateSnapshot? ReadStartupSnapshot(ReleaseStartupUpdates? service)
    {
        try { return service?.Snapshot(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException) { return null; }
    }

    private void BuildStartupUpdateNotice()
    {
        _startupNotice = new Button { Id = "startup-update-ready", X = 2, Y = 1,
            Text = UpdateText("startup.notice"), Visible = false };
        _startupNotice.Accepted += (_, args) => { args.Handled = true; ShowReleaseUpdates(); };
        _window!.Add(_startupNotice);
        PollStartupUpdates();
    }

    private void PollStartupUpdates()
    {
        var service = StartupUpdates();
        var ready = ReadStartupSnapshot(service);
        if (_startupNotice is not null)
            _startupNotice.Visible = ready is { Phase: "Ready", Decision: "None" }
                && ready.Channel == ReadStartupPreferences(service).Channel;
        _refreshStartupUpdateView?.Invoke();
    }

    private void StartStartupPreparation(ReleaseStartupUpdates service, Label status, Button check)
    {
        if (_startupPreparationTask is { IsCompleted: false }) return;
        var deadline = _startupPreparation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        status.Text = UpdateText("checking"); check.Enabled = false;
        var overlay = _overlay;
        _startupPreparationTask = Task.Run(async () =>
        {
            try { await service.PrepareAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException
                or TimeoutException) { /* Preparation never changes the active installation. */ }
            _uiActions.Enqueue(() =>
            {
                if (!ReferenceEquals(overlay, _overlay)) return;
                check.Enabled = true;
                var snapshot = ReadStartupSnapshot(service);
                status.Text = UpdateText(snapshot?.Phase == "Ready" ? "startup.verified" : "startup.unavailable");
                PollStartupUpdates();
            });
        });
    }

    private void ChooseStartupUpdate(string revision, bool restart, Label status)
    {
        if (restart && (HasActiveModelWork || _activeInteraction is not null
            || _localEngineRuntimeTask is { IsCompleted: false } || _claudeLoginTask is { IsCompleted: false }
            || _providerConnectionTask is { IsCompleted: false }
            || !string.IsNullOrEmpty(_companionChatDraft)
            || _releaseUpdateReturnToPreferences && _preferencesDraftLocale != (_preferencesLocaleSnapshot?.Locale ?? _preferencesOriginalLocale)
            || !string.IsNullOrEmpty(_composer?.Text) || _pendingAttachments.Count > 0
            || _conversationDrafts.Values.Any(draft => !string.IsNullOrEmpty(draft.Text) || draft.Attachments.Length > 0)))
        { status.Text = UpdateText("startup.busy"); return; }
        var service = StartupUpdates();
        var launch = StartupLaunchIdOverride ?? Environment.GetEnvironmentVariable("OMNICORE_STARTUP_LAUNCH_ID");
        if (service is null || launch is null) { status.Text = UpdateText("startup.unavailable"); return; }
        try
        {
            var choice = service.Choose(revision, restart ? "Restart" : "NextLaunch", launch,
                restart ? Environment.CurrentDirectory : null, restart ? _cursorSession : null);
            if (!choice.Accepted)
            {
                status.Text = UpdateText(choice.Diagnostic == "update_other_instances_active"
                    ? "startup.other_instances" : "startup.stale"); return;
            }
            if (!restart) { status.Text = UpdateText("startup.deferred"); return; }
            _updateRestartRequested = true;
            _application?.RequestStop();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        { status.Text = UpdateText("startup.unavailable"); }
    }

    private void RestoreUpdateConversation()
    {
        if (Environment.GetEnvironmentVariable("OMNICORE_STARTUP_MANAGED") != "1") return;
        var session = Environment.GetEnvironmentVariable("OMNICORE_RESTART_SESSION");
        Environment.SetEnvironmentVariable("OMNICORE_RESTART_SESSION", null);
        if (Guid.TryParse(session, out _)) ChangeConversation("conversation.resume", session);
    }

    private void CancelStartupPreparation()
    {
        try { _startupPreparation?.Cancel(); } catch (ObjectDisposedException) { }
        // The network worker can be winding down; do not dispose its services/token underneath it.
        var cancellation = _startupPreparation; var service = _startupUpdates;
        _startupPreparation = null; _startupUpdates = null;
        if (_startupPreparationTask is { } task)
            _ = task.ContinueWith(_ => { cancellation?.Dispose(); service?.Dispose(); }, TaskScheduler.Default);
        else { cancellation?.Dispose(); service?.Dispose(); }
    }
}
