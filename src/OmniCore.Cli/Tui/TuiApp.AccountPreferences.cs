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

        var frameHeight = Math.Clamp(_window.Frame.Height - 4, 18, 30);
        var frame = OverlayFrame(AccountUi("title.claude"), frameHeight);
        var listHeight = Math.Max(4, frameHeight - 16);
        var detailY = 5 + listHeight;
        var statusY = detailY + 4;
        var actionsY = frameHeight - 5;
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(3), Height = 1,
            Text = AccountUi("account.configSource") });
        var list = new ListView { Id = "claude-account-list", X = 2, Y = 4,
            Width = Dim.Fill(4), Height = listHeight, CanFocus = rows.Count > 0, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(rows.Select(ClaudeAccountRowText)));
        var selectedIndex = selectedProviderId is null ? -1 : rows.ToList()
            .FindIndex(row => string.Equals(row.ProviderId, selectedProviderId, StringComparison.Ordinal));
        if (rows.Count > 0) list.SelectedItem = selectedIndex >= 0 ? selectedIndex : 0;
        #pragma warning disable CS0618 // Existing pinned Terminal.Gui TextView supplies a scrollable read-only detail pane.
        var detail = new TextView { Id = "claude-account-detail", X = 2, Y = detailY,
            Width = Dim.Fill(4), Height = 4, ReadOnly = true, WordWrap = true, ScrollBars = true };
        #pragma warning restore CS0618
        var status = new Label { Id = "claude-account-status", X = 2, Y = statusY,
            Width = Dim.Fill(4), Height = 1, Text = notice ?? "" };
        var login = new Button { Id = "claude-account-login", X = 2, Y = actionsY,
            Text = AccountUi("action.login") };
        var logout = new Button { Id = "claude-account-logout", X = Pos.Right(login) + 2, Y = actionsY,
            Text = AccountUi("action.logout") };
        var back = new Button { Id = "claude-account-back", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2),
            Text = AccountUi("action.back") };
        TuiClaudeAccountRow? Selected() => list.SelectedItem is int index && index >= 0 && index < rows.Count
            ? rows[index] : null;
        void Sync()
        {
            var row = Selected();
            login.Enabled = row?.CanLogin == true;
            logout.Enabled = row?.CanLogout == true;
            detail.Text = row is null ? AccountUi("status.noRows") : ClaudeAccountDetail(row);
            if (row?.UnavailableReasonKey is { } reason)
                status.Text = _localization.ResolveWire(reason);
        }
        list.ValueChanged += (_, _) => Sync();
        login.Accepted += (_, _) => { if (Selected() is { CanLogin: true } row) StartClaudeLogin(row.ProviderId); };
        logout.Accepted += (_, _) => { if (Selected() is { CanLogout: true } row) ConfirmClaudeLogout(row.ProviderId); };
        back.Accepted += (_, _) => { _returnToClaudeAccounts = false; CloseOverlay(); ShowPreferences("accounts_connections", categoryPage: true); };
        frame.Add(list, detail, status, login, logout, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true;
        Sync();
        if (rows.Count > 0) list.SetFocus(); else back.SetFocus();
    }

    private string ClaudeAccountRowText(TuiClaudeAccountRow row)
    {
        var state = row.UnavailableReasonKey is not null ? "status.unavailable"
            : row.Dead ? "status.dead" : row.Expired ? "status.expired" : row.SignedIn ? "status.connected" : "status.notConfigured";
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

    private void StartClaudeLogin(string providerId)
    {
        if (_window is null || _overlay is null || _claudeLoginCancellation is not null) return;
        CloseOverlay();
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _claudeLoginCancellation = cancellation;
        var frame = OverlayFrame(AccountUi("title.claude"), Math.Clamp(_window.Frame.Height - 4, 18, 30));
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(3), Height = 2, Text = AccountUi("help.loginProgress") });
#pragma warning disable CS0618 // Pinned Terminal.Gui TextView remains the existing scrollable read-only presenter.
        var progress = new TextView { Id = "claude-account-progress", X = 2, Y = 5,
            Width = Dim.Fill(4), Height = Dim.Fill(4), ReadOnly = true, WordWrap = true,
            Text = AccountUi("claude.phase.preparing") };
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
                var result = await _claudeAccounts.LoginAsync(providerId,
                    value => Update(() => progress.Text = ClaudeLoginProgress(value)), token).ConfigureAwait(false);
                Update(() =>
                {
                    _claudeLoginCancellation = null;
                    _returnToClaudeAccounts = false;
                    CloseOverlay();
                    ShowClaudeAccounts(ClaudeLoginResultNotice(result), providerId);
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
                    ShowClaudeAccounts(AccountUi("claude.phase.failed"), providerId);
                });
            }
            finally { cancellation.Dispose(); }
        });
    }

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
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(3), Height = 2, Text = AccountUi("claude.logoutConfirm") });
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
