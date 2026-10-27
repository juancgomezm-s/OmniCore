namespace OmniCore.Cli;

using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    private string UpdateText(string key) => _localization.Resolve("updates." + key);

    private void ShowReleaseUpdates(bool returnToPreferences = false)
    {
        if (_window is null || _activeInteraction is not null || _overlay is not null) return;
        _releaseUpdateReturnToPreferences = returnToPreferences;
        var frame = OverlayFrame(UpdateText("title"), _window.Frame.Height - 3);
        frame.Id = "release-updates"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var channel = new ListView { Id = "release-update-channel", X = 2, Y = 2, Width = Dim.Fill(4), Height = 3, ShowMarks = false };
        channel.SetSource(new ObservableCollection<string>([UpdateText("stable"), UpdateText("preview")]));
        channel.SelectedItem = 0;
        var status = new Label { Id = "release-update-status", X = 2, Y = 6, Width = Dim.Fill(4), Height = 2,
            Text = UpdateText("ready") };
#pragma warning disable CS0618 // Pinned Terminal.Gui text inspection, matching other read-only detail panes.
        var details = new TextView { Id = "release-update-details", X = 2, Y = 8, Width = Dim.Fill(4), Height = Dim.Fill(5),
            ReadOnly = true, WordWrap = true, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
            Text = UpdateText("metadata_pending") };
#pragma warning restore CS0618
        var check = new Button { Id = "release-update-check", X = 2, Y = Pos.AnchorEnd(2), Text = UpdateText("check") };
        var close = new Button { Id = "release-update-close", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(2), Text = UpdateText("close") };
        int? selectedChannel = 0;
        channel.ValueChanged += (_, _) =>
        {
            if (selectedChannel == channel.SelectedItem) return;
            selectedChannel = channel.SelectedItem;
            CancelReleaseUpdateCheck();
            status.Text = UpdateText("ready"); details.Text = UpdateText("metadata_pending"); check.Enabled = true;
        };
        channel.Accepted += (_, args) => { args.Handled = true; };
        channel.KeyDown += (_, key) => { if (key.KeyCode.ToString() == "Enter") key.Handled = true; };
        check.Accepted += (_, args) =>
        {
            args.Handled = true;
            StartReleaseUpdateCheck(channel.SelectedItem == 1 ? ReleaseUpdateChannel.Preview : ReleaseUpdateChannel.Stable,
                frame, status, details, check);
        };
        close.Accepted += (_, args) => { args.Handled = true; CloseReleaseUpdates(); };
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 1, Text = UpdateText("channel") }, channel,
            status, details, check, close);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); channel.SetFocus();
    }

    private void CloseReleaseUpdates()
    {
        var returnToPreferences = _releaseUpdateReturnToPreferences;
        _releaseUpdateReturnToPreferences = false;
        CloseOverlay();
        if (returnToPreferences && _activeInteraction is null) ShowPreferences("general", categoryPage: true);
    }

#pragma warning disable CS0618 // Pinned read-only Terminal.Gui detail pane; this surface does not edit content.
    private void StartReleaseUpdateCheck(ReleaseUpdateChannel channel, View frame, Label status, TextView details, Button check)
    {
        CancelReleaseUpdateCheck();
        var generation = _releaseUpdateGeneration;
        var cancellation = _releaseUpdateCancellation = new CancellationTokenSource();
        status.Text = UpdateText("checking"); check.Enabled = false;
        var request = CreateReleaseUpdateRequest(channel);
        _ = Task.Run(async () =>
        {
            ReleaseUpdateCheckResult result;
            try
            {
                if (ReleaseUpdateCheckOverride is { } injected)
                    result = await injected(request, cancellation.Token).ConfigureAwait(false);
                else
                {
                    using var checker = new GitHubReleaseUpdateChecker();
                    result = await checker.CheckAsync(request, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception)
            {
                result = new(channel, ReleaseUpdateStatus.NetworkError, ReleaseCompatibility.Unknown, request.InstalledVersion,
                    DiagnosticCode: "github_check_failed");
            }
            _uiActions.Enqueue(() =>
            {
                if (!ReferenceEquals(_overlay, frame) || generation != _releaseUpdateGeneration
                    || cancellation.IsCancellationRequested) return;
                RenderUpdateResult(status, details, result);
                check.Enabled = true;
            });
        });
    }

    private void RenderUpdateResult(Label status, TextView details, ReleaseUpdateCheckResult result)
    {
        status.Text = UpdateText(ReleaseUpdateResources.StatusKey(result.Status)["updates.".Length..]);
        var lines = new List<string>
        {
            UpdateText("installed") + ": " + SafeDisplay(result.InstalledVersion),
            UpdateText("available") + ": " + SafeDisplay(result.AvailableVersion ?? UpdateText("unknown")),
            UpdateText("channel") + ": " + UpdateText(result.Channel == ReleaseUpdateChannel.Preview ? "preview" : "stable"),
            UpdateText("platform") + ": " + RuntimeInformation.RuntimeIdentifier,
            UpdateText("runtime") + ": .NET " + Environment.Version,
            UpdateText("node") + ": " + UpdateText("unknown"),
            UpdateText("protocol") + ": " + ProtocolVersions.V1,
            UpdateText("configuration") + ": " + UpdateText("unknown"),
            UpdateText("compat_" + (result.Compatibility switch
                { ReleaseCompatibility.Compatible => "ok", ReleaseCompatibility.Incompatible => "no", _ => "unknown" })),
            UpdateText("metadata_pending"),
        };
        if (result.Tag is not null) lines.Add(UpdateText("tag") + ": " + SafeDisplay(result.Tag));
        if (result.CommitSha is not null) lines.Add(UpdateText("commit") + ": " + result.CommitSha);
        if (result.AssetName is not null) lines.Add(UpdateText("asset") + ": " + SafeDisplay(result.AssetName));
        if (result.AssetSize is { } size) lines.Add(UpdateText("size") + ": " + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes");
        if (result.AssetSha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit)) lines.Add(UpdateText("sha256") + ": " + digest.ToLowerInvariant());
        if (result.Requirements is { } requirements)
            lines.Add(UpdateText("requirements") + ": .NET " + Range(requirements.DotNetMinimum, requirements.DotNetMaximum)
                + " · Node.js " + Range(requirements.NodeMinimum, requirements.NodeMaximum)
                + " · " + UpdateText("protocol") + " " + Range(requirements.ProtocolMinimum, requirements.ProtocolMaximum)
                + " · " + UpdateText("configuration") + " " + Range(requirements.ConfigurationMinimum, requirements.ConfigurationMaximum));
        if (IsOfficialReleaseUrl(result.ReleaseUrl)) lines.Add(UpdateText("release_url") + ": " + result.ReleaseUrl);
        if (result.RetryAfterUtc is { } retry) lines.Add(UpdateText("retry_after") + ": " + retry.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (result.DiagnosticCode is { Length: > 0 } code) lines.Add(UpdateText("diagnostic") + ": " + SafeDisplay(code));
        if (result.Notes is { Length: > 0 } notes) lines.Add(UpdateText("notes") + "\n" + SafeDisplay(notes));
        details.Text = string.Join("\n", lines);
    }
#pragma warning restore CS0618

    private void CancelReleaseUpdateCheck()
    {
        _releaseUpdateGeneration++;
        var current = _releaseUpdateCancellation; _releaseUpdateCancellation = null;
        if (current is not null) { try { current.Cancel(); } catch (ObjectDisposedException) { } current.Dispose(); }
    }

    private static string SafeDisplay(string value)
    {
        var safe = new string(value.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());
        return safe.Length > 12_000 ? safe[..12_000] : safe;
    }

    private string Range(string? minimum, string? maximum) => minimum is null && maximum is null ? UpdateText("unknown")
        : (minimum is null ? "?" : SafeDisplay(minimum)) + " – " + (maximum is null ? "?" : SafeDisplay(maximum));

    private static bool IsOfficialReleaseUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath.StartsWith("/juancgomezm-s/OmniCore/releases/tag/", StringComparison.Ordinal);

    internal static ReleaseUpdateCheckRequest CreateReleaseUpdateRequest(ReleaseUpdateChannel channel)
    {
        var info = typeof(TuiApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0+development";
        var development = info.Contains('+', StringComparison.Ordinal);
        return new(channel, info, development,
            new(RuntimeInformation.RuntimeIdentifier, Environment.Version.ToString(), ProtocolVersion: ProtocolVersions.V1));
    }
}
