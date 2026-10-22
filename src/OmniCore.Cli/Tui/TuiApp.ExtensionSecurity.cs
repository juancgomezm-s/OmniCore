using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    internal void ShowExtensionContext()
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(ExtensionText("context.title"), _window.Frame.Height - 3);
        frame.Id = "extension-context"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var packages = JsonSerializer.Deserialize(_client.Query("extensions.context.catalog", CancellationToken.None)?.Json ?? "[]", ExtensionJson.Default.ExtensionPackageStateArray) ?? [];
        var entries = packages.SelectMany(package => (package.ContextContributors ?? []).Select(contributor => (Package: package, Contributor: contributor))).ToArray();
        var list = new ListView { Id = "extension-context-list", X = 2, Y = 2, Width = Dim.Percent(35), Height = Dim.Fill(8), CanFocus = true };
        list.SetSource(new ObservableCollection<string>(entries.Select(item => item.Package.Id + " / " + item.Contributor)));
        if (entries.Length > 0) list.SelectedItem = 0;
#pragma warning disable CS0618
        var content = new TextView { Id = "extension-context-content", X = Pos.Percent(39), Y = 2, Width = Dim.Fill(2), Height = Dim.Fill(8), ReadOnly = true, WordWrap = true };
#pragma warning restore CS0618
        var status = new Label { Id = "extension-context-status", X = 2, Y = Pos.AnchorEnd(5), Width = Dim.Fill(4), Height = 2, Text = ExtensionText("context.help") };
        var preview = new Button { Id = "extension-context-preview", X = 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("context.preview") };
        var approve = new Button { Id = "extension-context-approve", X = Pos.Right(preview) + 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("context.approve"), Enabled = false };
        var revoke = new Button { Id = "extension-context-revoke", X = Pos.Right(approve) + 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("context.revoke") };
        ExtensionContextPreview? displayed = null;
        (ExtensionPackageState Package, string Contributor)? Selected() => list.SelectedItem is int index && index >= 0 && index < entries.Length ? entries[index] : null;
        void Read()
        {
            displayed = null; approve.Enabled = false; content.Text = "";
            if (Selected() is not { } selection) return;
            try
            {
                displayed = (JsonSerializer.Deserialize(_client.Query("extensions.context", CancellationToken.None)?.Json ?? "[]", ExtensionJson.Default.ExtensionContextPreviewArray) ?? [])
                    .SingleOrDefault(item => item.ExtensionId == selection.Package.Id && item.Contributor == selection.Contributor);
                if (displayed is null) return;
                content.Text = displayed.ExtensionId + " @ " + displayed.Version + "\n" + displayed.ContentSha256
                    + "\n\n" + string.Join("\n\n", displayed.Items.Select(item => SessionPreferencesPresentation.SafeText(item.Content)));
                status.Text = ExtensionText(displayed.Approved ? "context.approved" : "context.help");
                approve.Enabled = !displayed.Approved && _client is ITrustedUserActionClient;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException or NotSupportedException)
            { status.Text = ExtensionText("context.session_required"); }
        }
        void Send(string action)
        {
            if (Selected() is not { } selection || _client is not ITrustedUserActionClient human) return;
            var fields = "," + JsonObj.Field("contributor", selection.Contributor)
                + "," + JsonObj.Field("expectedManifestSha256", selection.Package.ManifestSha256)
                + "," + JsonObj.Field("expectedPackageSha256", selection.Package.PackageSha256)
                + (displayed is null ? "" : "," + JsonObj.Field("previewId", displayed.Id) + "," + JsonObj.Field("contentSha256", displayed.ContentSha256));
            try
            {
                var ack = human.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"extensions.package\"," + JsonObj.Field("action", action)
                    + "," + JsonObj.Field("extensionId", selection.Package.Id) + fields + "}"), CancellationToken.None);
                if (ack.Status != "ok") { displayed = null; approve.Enabled = false; status.Text = ExtensionText("context.failed"); return; }
                Read();
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException or NotSupportedException)
            { displayed = null; approve.Enabled = false; status.Text = ExtensionText("context.failed"); }
        }
        preview.Accepted += (_, _) => Send("context-preview");
        approve.Accepted += (_, _) => { if (displayed is not null) Send("context-approve"); };
        revoke.Accepted += (_, _) => Send("context-revoke");
        list.ValueChanged += (_, _) => Read();
        var back = new Button { Id = "extension-context-back", X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => ShowExtensionPackages();
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; ShowExtensionPackages(); } };
        frame.Add(list, content, status, preview, approve, revoke, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); Read(); list.SetFocus();
    }

    private long _extensionNoticeCursor;
    private bool _extensionNoticePending;
    private DateTimeOffset _extensionNoticePoll;
    private CancellationTokenSource? _extensionNoticeCancellation;
    private void PollExtensionNotifications()
    {
        if (_extensionNoticeCancellation?.IsCancellationRequested == true) return;
        if (_extensionNoticePending || DateTimeOffset.UtcNow < _extensionNoticePoll) return;
        _extensionNoticeCancellation ??= new CancellationTokenSource();
        var cancellation = _extensionNoticeCancellation.Token;
        _extensionNoticePending = true; _extensionNoticePoll = DateTimeOffset.UtcNow.AddSeconds(2);
        var session = _cursorSession;
        _ = Task.Run(() =>
        {
            ExtensionNotificationPage? page = null;
            try { page = JsonSerializer.Deserialize(_client.Query("extensions.notifications:" + _extensionNoticeCursor.ToString(CultureInfo.InvariantCulture), cancellation)?.Json ?? "null", ExtensionJson.Default.ExtensionNotificationPage); }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or NotSupportedException or UnauthorizedAccessException or OperationCanceledException) { }
            if (cancellation.IsCancellationRequested) return;
            _uiActions.Enqueue(() =>
            {
                _extensionNoticePending = false;
                if (cancellation.IsCancellationRequested || page is null || session != _cursorSession) return;
                _extensionNoticeCursor = page.NextSequence;
                if (page.Items.Count == 0) return;
                var notices = page.Items.Select(item => new ConversationBlock("extension-notification:" + item.Sequence,
                    ConversationRole.System, ExtensionText("notification.label") + " · " + SessionPreferencesPresentation.SafeText(item.ExtensionId)
                        + " · " + ExtensionText("notification." + item.Level) + "\n" + SessionPreferencesPresentation.SafeText(item.Text), null));
                var blocks = _state.Conversation.Blocks.Concat(notices).ToArray();
                var visible = blocks.Where(block => block.Id.StartsWith("extension-notification:", StringComparison.Ordinal)).TakeLast(64).Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
                _state = new ClientState(_state.Header, new ConversationModel(blocks.Where(block => !block.Id.StartsWith("extension-notification:", StringComparison.Ordinal) || visible.Contains(block.Id)).ToArray()),
                    _state.Sidebar, _state.Composer, _state.StatusLine, _state.Overlays, _state.Connection);
                RenderState();
            });
        });
    }
}
