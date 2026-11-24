using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private CancellationTokenSource? _claudeLoginCancellation;
    private Task? _claudeLoginTask;
    private bool _returnToClaudeAccounts;
    private bool _returnToChatGptAccount;

    internal Task? ClaudeLoginTask => _claudeLoginTask;

    private string AccountUi(string key) => _localization.Resolve("accounts.ui." + key);
    private string ClaudeCatalogText(string key) => ClaudeModelCatalogResources.Get(_locale, key);

    private void ShowClaudeAccounts(string? notice = null, string? selectedProviderId = null)
    {
        if (_window is null || _overlay is not null) return;
        IReadOnlyList<TuiClaudeAccountRow> rows;
        try { rows = _claudeAccounts.List(CancellationToken.None); }
        catch (Exception)
        {
            ShowMessage(AccountUi("status.unavailable"));
            return;
        }

        var frameHeight = Math.Min(rows.Count == 0 ? 20 : 30, Math.Max(1, _window.Viewport.Height - 2));
        var frame = OverlayFrame(AccountUi("title.claude"), frameHeight);
        frame.Id = "claude-account-preferences";
        var listHeight = rows.Count == 0 ? 0 : Math.Max(2, frameHeight - 22);
        var detailY = rows.Count == 0 ? 4 : 5 + listHeight;
        var statusY = detailY + 4;
        frame.Add(new Label { Id = "claude-account-source-description", X = MenuIndent.Information, Y = 1, Width = Dim.Fill(3), Height = 2,
            Text = AccountUi("account.configSource") });
        var list = new ListView { Id = "claude-account-list", X = 2, Y = 4,
            Width = Dim.Fill(4), Height = listHeight, Visible = rows.Count > 0, CanFocus = rows.Count > 0, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(rows.Select(ClaudeAccountRowText)));
        var selectedIndex = selectedProviderId is null ? -1 : rows.ToList()
            .FindIndex(row => string.Equals(row.ProviderId, selectedProviderId, StringComparison.Ordinal));
        if (rows.Count > 0) list.SelectedItem = selectedIndex >= 0 ? selectedIndex : 0;
        #pragma warning disable CS0618 // Existing pinned Terminal.Gui TextView supplies a scrollable read-only detail pane.
        var detail = new TextView { Id = "claude-account-detail", X = 2, Y = detailY,
            Width = Dim.Fill(4), Height = 4, ReadOnly = true, WordWrap = true, ScrollBars = true };
        #pragma warning restore CS0618
#pragma warning disable CS0618 // Long catalog errors remain readable without overlapping the action column.
        var status = new TextView { Id = "claude-account-status", X = 2, Y = statusY,
            Width = Dim.Fill(4), Height = 2, ReadOnly = true, WordWrap = true, ScrollBars = true,
            CanFocus = !string.IsNullOrEmpty(notice), Text = notice ?? "" };
#pragma warning restore CS0618
        var configure = new CompactRowButton { Id = "claude-account-configure", X = 2, Y = Pos.AnchorEnd(8), Height = 1,
            NoPadding = true, TextAlignment = Alignment.Start, Text = ClaudeSetupText("configure"),
            Enabled = _claudeAccounts is ITuiClaudeAccountConfigurationHost };
        var discover = new CompactRowButton { Id = "claude-account-discover", X = 2, Y = Pos.AnchorEnd(10), Height = 1,
            NoPadding = true, TextAlignment = Alignment.Start, Text = ClaudeCatalogText("refresh"),
            Visible = rows.Count > 0 };
        var login = new CompactRowButton { Id = "claude-account-login", X = 2, Y = Pos.AnchorEnd(6), Height = 1,
            NoPadding = true, TextAlignment = Alignment.Start,
            Text = AccountUi("action.login") };
        var logout = new CompactRowButton { Id = "claude-account-logout", X = 2, Y = Pos.AnchorEnd(4), Height = 1,
            NoPadding = true, TextAlignment = Alignment.Start,
            Text = AccountUi("action.logout") };
        var back = new CompactRowButton { Id = "claude-account-back", X = 2, Y = Pos.AnchorEnd(2), Height = 1,
            NoPadding = true, TextAlignment = Alignment.Start,
            Text = AccountUi("action.back") };
        TuiClaudeAccountRow? Selected() => list.SelectedItem is int index && index >= 0 && index < rows.Count
            ? rows[index] : null;
        void Sync()
        {
            var row = Selected();
            login.Enabled = row?.CanLogin == true || rows.Count == 0 && _claudeAccounts is ITuiClaudeAccountConfigurationHost;
            login.Text = rows.Count == 0 ? ClaudeSetupText("configure_login") : AccountUi("action.login");
            logout.Enabled = row?.CanLogout == true;
            discover.Enabled = row is { Configured: true, SignedIn: true, Dead: false }
                && _claudeAccounts is ITuiClaudeModelCatalogHost;
            detail.Text = row is null ? ClaudeSetupText("empty") : ClaudeAccountDetail(row);
            if (row?.UnavailableReasonKey is { } reason)
                status.Text = _localization.ResolveWire(reason);
            status.CanFocus = status.Text.Length > 0;
        }
        list.ValueChanged += (_, _) => Sync();
        login.Accepted += (_, _) =>
        {
            if (Selected() is { CanLogin: true } row) StartClaudeLogin(row.ProviderId);
            else if (rows.Count == 0) ShowClaudeAccountConfiguration(null, signInAfterSave: true);
        };
        configure.Accepted += (_, _) => ShowClaudeAccountConfiguration(Selected()?.ProviderId, signInAfterSave: false);
        discover.Accepted += (_, _) => { if (Selected() is { } row && discover.Enabled) StartClaudeAccountOperation(row.ProviderId, login: false); };
        logout.Accepted += (_, _) => { if (Selected() is { CanLogout: true } row) ConfirmClaudeLogout(row.ProviderId); };
        back.Accepted += (_, _) => { _returnToClaudeAccounts = false; CloseOverlay(); ShowPreferences("accounts_connections", categoryPage: true); };
        EnterNavigation.Focus(() => discover.Enabled ? discover : login.Enabled ? login : logout.Enabled ? logout : back, list, detail, status);
        EnterNavigation.TraverseTabs(list, detail, status, discover, configure, login, logout, back);
        frame.Add(list, detail, status, discover, configure, login, logout, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true;
        Sync();
        if (rows.Count > 0) list.SetFocus(); else if (login.Enabled) login.SetFocus(); else back.SetFocus();
    }

    private string ClaudeAccountRowText(TuiClaudeAccountRow row)
    {
        var state = row.UnavailableReasonKey is not null ? "status.unavailable"
            : row.Dead ? "status.dead" : row.Expired ? "status.expired" : row.SignedIn ? "status.connected"
            : row.Configured ? "status.disconnected" : "status.notConfigured";
        return row.ProviderId + " · " + AccountUi(state);
    }

    private string ClaudeAccountDetail(TuiClaudeAccountRow row)
    {
        var values = new List<string> { AccountUi("account.configSource") + " · " + row.ProviderId };
        if (!string.IsNullOrWhiteSpace(row.AccountHint)) values.Add(AccountUi("account.email") + ": " + row.AccountHint);
        if (!string.IsNullOrWhiteSpace(row.SubscriptionType)) values.Add(AccountUi("account.plan") + ": " + row.SubscriptionType);
        if (row.ExpiresAt is { } expiry)
            values.Add(AccountUi("account.expires") + ": " + expiry.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(Environment.NewLine, values);
    }

    private void StartClaudeLogin(string providerId) => StartClaudeAccountOperation(providerId, login: true);

    private void StartClaudeAccountOperation(string providerId, bool login)
    {
        if (_window is null || _overlay is null || _claudeLoginCancellation is not null) return;
        CloseOverlay();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _claudeLoginCancellation = cancellation;
        var frame = OverlayFrame(AccountUi("title.claude"), Math.Clamp(_window.Frame.Height - 4, 18, 30));
        frame.Id = "claude-login-preferences";
        frame.Add(MenuIndent.Help(login ? AccountUi("help.loginProgress") : ClaudeCatalogText("progress"), 2, 2, "claude-login-help"));
#pragma warning disable CS0618 // Pinned Terminal.Gui TextView remains the existing scrollable read-only presenter.
        var progress = new TextView { Id = "claude-account-progress", X = 2, Y = 5,
            Width = Dim.Fill(4), Height = Dim.Fill(4), ReadOnly = true, WordWrap = true,
            Text = login ? AccountUi("claude.phase.preparing") : ClaudeCatalogText("progress") };
#pragma warning restore CS0618
        var cancel = new Button { Id = "claude-account-cancel", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2),
            Text = AccountUi("action.cancel") };
        cancel.Accepted += (_, _) => ReturnFromAccountSubpage();
        frame.Add(progress, cancel); ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        _preferencesReturnToCategory = true; _returnToClaudeAccounts = true; cancel.SetFocus();
        void Update(Action update) => _uiActions.Enqueue(() =>
        {
            if (ReferenceEquals(_overlay, frame) && ReferenceEquals(_claudeLoginCancellation, cancellation)
                && !token.IsCancellationRequested) update();
        });
        _claudeLoginTask = Task.Run(async () =>
        {
            try
            {
                var result = login ? await _claudeAccounts.LoginAsync(providerId,
                    value => Update(() => progress.Text = ClaudeLoginProgress(value)), token).ConfigureAwait(false) : new TuiClaudeLoginOutcome(true);
                TuiClaudeModelCatalogOutcome? catalog = null;
                if (result.Success && _claudeAccounts is ITuiClaudeModelCatalogHost models)
                {
                    token.ThrowIfCancellationRequested();
                    Update(() => progress.Text = ClaudeCatalogText("progress"));
                    catalog = await models.DiscoverModelsAsync(providerId, token).ConfigureAwait(false);
                }
                Update(() =>
                {
                    _claudeLoginCancellation = null;
                    _returnToClaudeAccounts = false;
                    var notice = login ? ClaudeLoginResultNotice(result) : "";
                    if (catalog is not null)
                        notice += (notice.Length > 0 ? "\n" : "") + ClaudeCatalogNotice(catalog);
                    if (result.Success && (catalog is null || catalog.Success))
                    {
                        try { _policies.ReloadModels(); }
                        catch (Exception) { notice += "\n" + ClaudeSetupText("unavailable"); }
                    }
                    CloseOverlay();
                    ShowClaudeAccounts(notice, providerId);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                Update(() =>
                {
                    _claudeLoginCancellation = null;
                    _returnToClaudeAccounts = false;
                    CloseOverlay();
                    ShowClaudeAccounts(login ? AccountUi("claude.phase.failed") : ClaudeCatalogText("failed"), providerId);
                });
            }
            finally { cancellation.Dispose(); }
        });
    }

    private string ClaudeCatalogNotice(TuiClaudeModelCatalogOutcome outcome) => !outcome.Success
        ? ClaudeCatalogText(outcome.ErrorKey ?? "failed")
        : outcome.AvailableModels == 0 ? ClaudeCatalogText("empty")
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, ClaudeCatalogText("ready"), outcome.AvailableModels)
            + (outcome.Truncated ? " " + ClaudeCatalogText("partial") : "");

    private string ClaudeLoginProgress(TuiClaudeLoginProgress progress) => progress.Phase switch
    {
        "PreparingChallenge" => AccountUi("claude.phase.preparing"),
        "Listening" => AccountUi("claude.phase.listening"),
        "AwaitingBrowser" => AccountUi("claude.phase.awaiting"),
        "Exchanging" => AccountUi("claude.phase.exchanging"),
        "FetchingProfile" => AccountUi("claude.phase.profile"),
        "Persisting" => AccountUi("claude.phase.persisting"),
        "Succeeded" => AccountUi("claude.phase.succeeded"),
        _ => AccountUi("claude.phase.failed"),
    };

    private string ClaudeLoginResultNotice(TuiClaudeLoginOutcome outcome)
    {
        if (outcome.Success) return AccountUi("claude.phase.succeeded");
        if (outcome.ErrorKey == "claude.oauth.failed.cancelled") return AccountUi("result.loginCancelled");
        return outcome.ErrorKey is { } key && AccountsPreferencesResources.ContainsKey(key)
            ? _localization.ResolveWire(key)
            : AccountUi("claude.phase.failed");
    }

    private void ConfirmClaudeLogout(string providerId)
    {
        if (_window is null || _overlay is null) return;
        CloseOverlay();
        var frame = OverlayFrame(AccountUi("title.claude"), 9);
        frame.Id = "claude-logout-preferences";
        frame.Add(MenuIndent.Help(AccountUi("claude.logoutConfirm"), 2, 2, "claude-logout-help"));
        var confirm = new Button { Id = "claude-logout-confirm", X = 2, Y = 5, Text = AccountUi("action.confirmLogout") };
        var cancel = new Button { Id = "claude-logout-cancel", X = Pos.Right(confirm) + 2, Y = 5, Text = AccountUi("action.cancel") };
        confirm.Accepted += (_, _) =>
        {
            try
            {
                _claudeAccounts.Logout(providerId, CancellationToken.None);
                _returnToClaudeAccounts = false;
                CloseOverlay(); ShowClaudeAccounts(null, providerId);
            }
            catch (Exception) { CloseOverlay(); ShowClaudeAccounts(AccountUi("error.logout"), providerId); }
        };
        cancel.Accepted += (_, _) => { _returnToClaudeAccounts = false; CloseOverlay(); ShowClaudeAccounts(selectedProviderId: providerId); };
        frame.Add(confirm, cancel); ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        _preferencesReturnToCategory = true; _returnToClaudeAccounts = true; cancel.SetFocus();
    }

    private void CancelClaudeLoginOperation()
    {
        var cancellation = _claudeLoginCancellation;
        _claudeLoginCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        _returnToClaudeAccounts = false;
    }

    private void ReturnFromAccountSubpage()
    {
        if (_returnToChatGptAccount)
        {
            _returnToChatGptAccount = false;
            CloseOverlay();
            ShowAccount();
            return;
        }
        if (!_returnToClaudeAccounts) return;
        _returnToClaudeAccounts = false;
        CloseOverlay();
        ShowClaudeAccounts();
    }
}
