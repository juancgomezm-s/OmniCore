using System.Collections.ObjectModel;
using System.Text.Json;
using OmniCore.Protocol;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly Dictionary<string, string> _extensionViewSelection = new(StringComparer.Ordinal);
    private Action<bool>? _refreshExtensionView;
    private CancellationTokenSource? _extensionViewCancellation;
    private void CancelExtensionViewRefresh()
    {
        _refreshExtensionView = null;
        var cancellation = _extensionViewCancellation; _extensionViewCancellation = null;
        if (cancellation is not null) { cancellation.Cancel(); cancellation.Dispose(); }
    }
    private string ExtensionText(string key) => _localization.Resolve("extension." + key);
    private string ExtensionState(string state)
    {
        var text = _localization.ResolveDetailed("extension.state." + state.ToLowerInvariant());
        return text.UsedFallback ? state : text.Text;
    }
    internal void ShowExtensionPackages()
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(ExtensionText("platform.title"), _window.Frame.Height - 3);
        frame.Id = "extension-packages"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var packages = JsonSerializer.Deserialize(_client.Query("extensions.packages", CancellationToken.None)?.Json ?? "[]",
            ExtensionJson.Default.ExtensionPackageStateArray) ?? [];
        var list = new ListView { Id = "extension-package-list", X = 2, Y = 2, Width = Dim.Percent(40), Height = Dim.Fill(8), CanFocus = true };
        list.SetSource(new ObservableCollection<string>(packages.Select(package => package.Id + " · " + ExtensionState(package.State))));
        if (packages.Length > 0) list.SelectedItem = 0;
#pragma warning disable CS0618
        var details = new TextView { Id = "extension-package-details", X = Pos.Percent(44), Y = 2, Width = Dim.Fill(2), Height = Dim.Fill(8), ReadOnly = true, WordWrap = true };
#pragma warning restore CS0618
        ExtensionPackageState? Selected() => list.SelectedItem is int index && index >= 0 && index < packages.Length ? packages[index] : null;
        void Sync() { details.Text = Selected() is { } package ? JsonSerializer.Serialize(package, ExtensionJson.Default.ExtensionPackageState) : ExtensionText("platform.empty"); }
        list.ValueChanged += (_, _) => Sync();
        var source = new TextField { Id = "extension-package-source", X = 2, Y = Pos.AnchorEnd(6), Width = Dim.Fill(20), Text = "" };
        var install = new Button { Id = "extension-package-install", X = Pos.AnchorEnd(17), Y = Pos.AnchorEnd(6), Text = ExtensionText("platform.install") };
        install.Accepted += (_, _) => ExtensionPackageAction("install", source.Text?.ToString() ?? "", false);
        var enable = new Button { Id = "extension-package-enable", X = 2, Y = Pos.AnchorEnd(4), Text = ExtensionText("platform.enable") };
        var disable = new Button { Id = "extension-package-disable", X = Pos.Right(enable) + 2, Y = Pos.AnchorEnd(4), Text = ExtensionText("platform.disable") };
        var uninstall = new Button { Id = "extension-package-uninstall", X = Pos.Right(disable) + 2, Y = Pos.AnchorEnd(4), Text = ExtensionText("platform.uninstall") };
        var views = new Button { Id = "extension-package-views", X = Pos.Right(uninstall) + 2, Y = Pos.AnchorEnd(4), Text = ExtensionText("platform.views") };
        var config = new Button { Id = "extension-package-config", X = Pos.Right(views) + 2, Y = Pos.AnchorEnd(4), Text = ExtensionText("platform.config") };
        config.Accepted += (_, _) => { if (Selected() is { } package) ShowExtensionConfig(package); };
        enable.Accepted += (_, _) => { if (Selected() is { } package) ConfirmExtensionPackage(package, "enable"); };
        disable.Accepted += (_, _) => { if (Selected() is { } package) ExtensionPackageAction("disable", package.Id, false); };
        uninstall.Accepted += (_, _) => { if (Selected() is { } package) ConfirmExtensionPackage(package, "uninstall"); };
        views.Accepted += (_, _) => ShowExtensionViews();
        var update = new Button { Id = "extension-package-update", X = 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("platform.update") };
        var rollback = new Button { Id = "extension-package-rollback", X = Pos.Right(update) + 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("platform.rollback") };
        update.Accepted += (_, _) => { if (Selected() is { } package) ConfirmExtensionPackage(package, "update", source.Text?.ToString()); };
        rollback.Accepted += (_, _) => { if (Selected() is { } package) ConfirmExtensionPackage(package, "rollback"); };
        var context = new Button { Id = "extension-package-context", X = Pos.Right(rollback) + 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("context.title") };
        context.Accepted += (_, _) => ShowExtensionContext();
        var back = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("extensions_skills", categoryPage: true); };
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; CloseOverlay(); ShowPreferences("extensions_skills", categoryPage: true); } };
        frame.Add(list, details, source, install, enable, disable, uninstall, views, config, update, rollback, context, back,
            new Label { X = 2, Y = Pos.AnchorEnd(7), Width = Dim.Fill(4), Text = ExtensionText("platform.source") });
        Sync(); ApplyTheme(frame); _overlay = frame; _window.Add(frame); list.SetFocus();
    }

    private void ConfirmExtensionPackage(ExtensionPackageState package, string action, string? source = null)
    {
        if (_window is null) return;
        CloseOverlay(); var frame = OverlayFrame(ExtensionText("platform.confirm"), _window.Frame.Height - 3);
        frame.Id = "extension-package-confirm";
#pragma warning disable CS0618
        frame.Add(new TextView { X = 2, Y = 2, Width = Dim.Fill(4), Height = Dim.Fill(4), ReadOnly = true, WordWrap = true,
            Text = ExtensionText(action is "update" or "rollback" ? "platform.update_consent" : action == "uninstall" ? "platform.archive" : package.RuntimeKind is null ? "platform.consent" : "platform.trust")
                + "\n\n" + JsonSerializer.Serialize(package, ExtensionJson.Default.ExtensionPackageState) });
#pragma warning restore CS0618
        var yes = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.confirm") };
        var no = new Button { X = Pos.Right(yes) + 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        yes.Accepted += (_, _) => ExtensionPackageAction(action, package.Id, true, package, source);
        no.Accepted += (_, _) => ShowExtensionPackages(); frame.Add(yes, no);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); no.SetFocus();
    }
    private void ExtensionPackageAction(string action, string value, bool trust, ExtensionPackageState? expected = null, string? source = null)
    {
        if (_client is not ITrustedUserActionClient trusted) return;
        var payload = "{" + JsonObj.Field("cmd", "extensions.package") + "," + JsonObj.Field("action", action)
            + "," + JsonObj.Field(action == "install" ? "source" : "extensionId", value)
            + (expected is null ? "" : "," + JsonObj.Field("expectedManifestSha256", expected.ManifestSha256) + "," + JsonObj.Field("expectedPackageSha256", expected.PackageSha256))
            + (action == "update" ? "," + JsonObj.Field("source", source ?? "") : "")
            + (trust ? ",\"trustProcess\":\"true\"" : "") + "}";
        var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        ShowExtensionPackages();
        if (ack.Status != "ok") ShowExtensionNotice(ack.Error ?? ExtensionText("platform.failed"));
        else if (action is "update" or "rollback") ShowExtensionNotice(ExtensionText("platform.updated_disabled"));
    }
    private void ShowExtensionNotice(string notice)
    {
        if (_overlay is null) return;
        if (_overlay.SubViews.FirstOrDefault(view => view.Id == "extension-notice") is Label existing)
        { existing.Text = notice; return; }
        _overlay.Add(new Label { Id = "extension-notice", X = 2, Y = 0, Width = Dim.Fill(4), Text = notice });
    }
    private void ShowExtensionConfig(ExtensionPackageState package)
    {
        if (_window is null) return;
        CloseOverlay(); var frame = OverlayFrame(ExtensionText("platform.config"), _window.Frame.Height - 3);
        var keys = package.ConfigurationKeys ?? [];
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 2, Text = string.Join(", ", keys) });
        var key = new TextField { X = 2, Y = 5, Width = Dim.Fill(20), Text = keys.FirstOrDefault() ?? "" };
        var value = new TextField { X = 2, Y = 8, Width = Dim.Fill(4), Text = "null" };
        void LoadValue()
        {
            if (!keys.Contains(key.Text?.ToString() ?? "", StringComparer.Ordinal)) return;
            value.Text = _client.Query("extensions.configuration:" + Uri.EscapeDataString("{" + JsonObj.Field("extensionId", package.Id)
                + "," + JsonObj.Field("key", key.Text?.ToString() ?? "") + "}"), CancellationToken.None)?.Json ?? "null";
        }
        LoadValue();
        var load = new Button { X = Pos.AnchorEnd(17), Y = 5, Text = ExtensionText("platform.load") };
        load.Accepted += (_, _) => LoadValue(); frame.Add(load);
        var save = new Button { X = 2, Y = 11, Text = ExtensionText("platform.confirm"), Enabled = keys.Count > 0 };
        save.Accepted += (_, _) =>
        {
            if (_client is not ITrustedUserActionClient trusted) return;
            var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"extensions.package\",\"action\":\"configure\","
                + JsonObj.Field("extensionId", package.Id) + "," + JsonObj.Field("key", key.Text?.ToString() ?? "")
                + "," + JsonObj.Field("value", value.Text?.ToString() ?? "null") + "}"), CancellationToken.None);
            if (ack.Status == "ok") ShowExtensionPackages(); else ShowExtensionNotice(ack.Error ?? ExtensionText("platform.failed"));
        };
        var back = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => ShowExtensionPackages(); frame.Add(key, value, save, back,
            new Label { X = 2, Y = 7, Text = ExtensionText("platform.json") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); key.SetFocus();
    }
    internal void ShowExtensionViews()
    {
        if (_window is null) return;
        CloseOverlay(); var frame = OverlayFrame(ExtensionText("platform.views"), _window.Frame.Height - 3);
        frame.Id = "extension-views";
        using var data = JsonDocument.Parse(_client.Query("extensions.views", CancellationToken.None)?.Json ?? "[]");
        var rows = data.RootElement.EnumerateArray().Select(row => (Extension: row.GetProperty("extensionId").GetString()!, View: row.GetProperty("viewId").GetString()!)).ToArray();
        var list = new ListView { Id = "extension-view-list", X = 2, Y = 2, Width = Dim.Fill(4), Height = Dim.Fill(4), CanFocus = true };
        list.SetSource(new ObservableCollection<string>(rows.Select(row => row.Extension + " · " + row.View)));
        if (rows.Length > 0) list.SelectedItem = 0;
        list.Accepted += (_, args) => { args.Handled = true; if (list.SelectedItem is int index && index >= 0 && index < rows.Length) ShowExtensionView(new(rows[index].Extension, rows[index].View)); };
        var back = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => ShowExtensionPackages(); frame.Add(list, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); list.SetFocus();
    }
    internal void ShowExtensionView(ExtensionViewRequest request)
    {
        if (_window is null) return;
        request = request with { AfterRevision = null, ClientKinds = ["tree", "list", "table", "details", "form", "indicator"] };
        ExtensionView view;
        try
        {
            var result = _client.Query("extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(request)), CancellationToken.None);
            view = ExtensionProtocol.Decode(result?.Json ?? "{}");
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { ShowExtensionViews(); ShowExtensionNotice(ExtensionText("platform.failed") + ": " + error.GetType().Name); return; }
        if (view.Kind is "form" or "indicator") { ShowExtensionTypedView(request, view); return; }
        CloseOverlay(); var frame = OverlayFrame(request.ExtensionId + " / " + request.ViewId + " / " + request.Path, _window.Frame.Height - 3);
        frame.Id = "extension-view"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var list = new ListView { Id = "extension-view-items", X = 2, Y = 2, Width = Dim.Percent(45), Height = Dim.Fill(6), CanFocus = true };
        var widget = ExtensionViewProjection.Present(view, _localization);
        list.SetSource(new ObservableCollection<string>(widget.Rows.Select(row => row.Text)));
        var selectionKey = request.ExtensionId + ":" + request.ViewId + ":" + request.Path;
        if (view.Items.Count > 0) list.SelectedItem = Math.Max(0, view.Items.ToList().FindIndex(item => item.Id == _extensionViewSelection.GetValueOrDefault(selectionKey)));
#pragma warning disable CS0618
        var details = new TextView { Id = "extension-view-details", X = Pos.Percent(48), Y = 2, Width = Dim.Fill(2), Height = Dim.Fill(6), ReadOnly = true, WordWrap = true };
#pragma warning restore CS0618
        var alternative = new Button { Id = "extension-view-alternative", X = 2, Y = Pos.AnchorEnd(4), Text = ExtensionText("action.alternative"), Visible = false };
        ExtensionViewItem? Selected() => list.SelectedItem is int index && index >= 0 && index < view.Items.Count ? view.Items[index] : null;
        void Sync()
        {
            var item = Selected();
            details.Text = ExtensionViewProjection.Describe(item, view, _localization);
            if (item is not null) _extensionViewSelection[selectionKey] = item.Id;
            alternative.Visible = item is not null && view.Actions.Any(action => action.ItemId == item.Id && action.Id == "alternative");
        }
        list.ValueChanged += (_, _) => Sync();
        list.Accepted += (_, args) =>
        {
            args.Handled = true;
            if (Selected() is { HasChildren: true } folder) ShowExtensionView(request with { Path = folder.Id });
            else if (Selected() is { } file && view.Kind == "tree")
            {
                try
                {
                    using var result = JsonDocument.Parse(_client.Query("workspace.panel:" + Uri.EscapeDataString("{\"kind\":\"diff\"," + JsonObj.Field("path", file.Id) + "}"), CancellationToken.None)?.Json ?? "{}");
                    details.Text = result.RootElement.GetProperty("file").GetProperty("text").GetString() ?? ""; details.SetFocus();
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or KeyNotFoundException)
                { ShowExtensionNotice(ExtensionText("platform.failed") + ": " + error.GetType().Name); }
            }
        };
        alternative.Accepted += (_, _) =>
        {
            if (Selected()?.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("checkpoint", out var point)
                && point.ValueKind == JsonValueKind.Object && point.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                ShowExtensionAlternative(request, id.GetString()!);
        };
        var back = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => { if (request.Path.Length > 0) ShowExtensionView(request with { Path = request.Path.Contains('/') ? request.Path[..request.Path.LastIndexOf('/')] : "" }); else ShowExtensionViews(); };
        var status = new Label { Id = "extension-view-status", X = 2, Y = Pos.AnchorEnd(5), Width = Dim.Fill(4), Height = 1,
            Text = view.Diagnostic is null ? ExtensionState(view.State) : _localization.Resolve(view.Diagnostic) };
        frame.Add(list, details, alternative, back, status);
        if (view.Metadata is { ValueKind: JsonValueKind.Object } metadata && metadata.TryGetProperty("git", out var git)
            && git.ValueKind == JsonValueKind.Object)
            frame.Add(new Label { Id = "extension-view-git", X = 2, Y = 1, Width = Dim.Fill(4), Height = 1,
                Text = git.GetProperty("isRepository").GetBoolean()
                    ? git.GetProperty("branch").GetString() + " · " + git.GetProperty("upstream").GetString()
                        + " ↑" + git.GetProperty("ahead").ToString() + " ↓" + git.GetProperty("behind").ToString()
                    : ExtensionText("git.unavailable") });
        if (view.Kind == "tree")
        {
            var search = new TextField { Id = "extension-view-search", X = 2, Y = 0, Width = Dim.Percent(45), Text = request.Search };
            var find = new Button { Id = "extension-view-find", X = Pos.Right(search) + 2, Y = 0, Text = ExtensionText("platform.search") };
            find.Accepted += (_, _) => ShowExtensionView(request with { Search = search.Text?.ToString() ?? "" }); frame.Add(search, find);
        }
        Sync(); ApplyTheme(frame); _overlay = frame; _window.Add(frame); list.SetFocus();
        AttachExtensionViewRefresh(frame, request, view, updated =>
        {
            var id = Selected()?.Id ?? _extensionViewSelection.GetValueOrDefault(selectionKey);
            view = updated;
            list.SetSource(new ObservableCollection<string>(ExtensionViewProjection.Present(view, _localization).Rows.Select(row => row.Text)));
            if (view.Items.Count > 0) list.SelectedItem = Math.Max(0, view.Items.ToList().FindIndex(item => item.Id == id));
            status.Text = view.Diagnostic is null ? ExtensionState(view.State) : _localization.Resolve(view.Diagnostic);
            if (frame.SubViews.FirstOrDefault(child => child.Id == "extension-view-git") is Label header
                && view.Metadata is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty("git", out var currentGit))
                header.Text = currentGit.GetProperty("isRepository").GetBoolean()
                    ? currentGit.GetProperty("branch").GetString() + " · " + currentGit.GetProperty("upstream").GetString()
                        + " ↑" + currentGit.GetProperty("ahead").ToString() + " ↓" + currentGit.GetProperty("behind").ToString()
                    : ExtensionText("git.unavailable");
            Sync();
        }, message => status.Text = message);
        var refresh = new Button { Id = "extension-view-refresh", X = Pos.Right(back) + 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.refresh") };
        refresh.Accepted += (_, _) => _refreshExtensionView?.Invoke(true); frame.Add(refresh); ApplyTheme(refresh);
    }
    private void ShowExtensionAlternative(ExtensionViewRequest request, string checkpoint)
    {
        if (_window is null) return;
        CloseOverlay(); var frame = OverlayFrame(ExtensionText("action.alternative"), _window.Frame.Height - 3);
        frame.Id = "extension-alternative";
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 4,
            Text = ExtensionText("history.files_and_conversation_only") + "\n" + checkpoint });
        var instruction = new TextField { Id = "extension-alternative-instruction", X = 2, Y = 8, Width = Dim.Fill(4), Text = "" };
        var create = new Button { Id = "extension-alternative-create", X = 2, Y = 11, Text = ExtensionText("action.alternative") };
        create.Accepted += (_, _) =>
        {
            if (_client is not ITrustedUserActionClient trusted) return;
            create.Enabled = false;
            var id = Ids.NewV7();
            var ack = trusted.SendUserAction(WireEnvelope.Command(id, "{\"cmd\":\"conversation.alternative\"," + JsonObj.Field("checkpointId", checkpoint)
                + "," + JsonObj.Field("instruction", instruction.Text?.ToString() ?? "") + "}"), CancellationToken.None);
            if (ack.Status != "ok") { create.Enabled = true; ShowExtensionNotice(ack.Error ?? ExtensionText("platform.failed")); return; }
            try
            {
                var result = JsonSerializer.Deserialize(_client.Query("workspace.panel:" + Uri.EscapeDataString("{\"kind\":\"alternative\"," + JsonObj.Field("commandId", id) + "}"), CancellationToken.None)?.Json ?? "{}",
                    TuiAlternativeJson.Default.TuiAlternativeResponse);
                if (result?.Alternative is not { State: "ready" } target || target.CommandId != id || target.CheckpointId != checkpoint)
                    throw new InvalidDataException("alternative_not_published");
                ShowExtensionAlternativeReady(request, target);
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
            { ShowExtensionNotice(ExtensionText("alternative.open_failed")); } // Never resend an admitted creation after an ambiguous result.
        };
        var back = new Button { X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => ShowExtensionView(request); frame.Add(instruction, create, back,
            new Label { X = 2, Y = 7, Width = Dim.Fill(4), Text = ExtensionText("platform.instruction") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); instruction.SetFocus();
    }
    private void ShowExtensionAlternativeReady(ExtensionViewRequest request, WorkspaceAlternative target)
    {
        if (_window is null) return;
        CloseOverlay(); var frame = OverlayFrame(ExtensionText("action.open_alternative"), _window.Frame.Height - 3);
        frame.Id = "extension-alternative-ready";
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 7,
            Text = ExtensionText("alternative.ready") + "\n\n" + target.Workspace + "\n" + target.Branch + "\n" + target.SessionId });
        var open = new Button { Id = "extension-alternative-open", X = 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("action.open_alternative") };
        open.Accepted += (_, _) =>
        {
            if (HasActiveModelWork) { ShowExtensionNotice(_localization.Resolve("shell.conversation.busy")); return; }
            RequestedAlternative = target; CancelExtensionViewRefresh(); _application?.RequestStop();
        };
        var back = new Button { X = Pos.Right(open) + 2, Y = Pos.AnchorEnd(3), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => ShowExtensionView(request);
        frame.Add(open, back); ApplyTheme(frame); _overlay = frame; _window.Add(frame); open.SetFocus();
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(TuiAlternativeResponse))]
internal partial class TuiAlternativeJson : System.Text.Json.Serialization.JsonSerializerContext;
internal sealed record TuiAlternativeResponse(WorkspaceAlternative? Alternative);
