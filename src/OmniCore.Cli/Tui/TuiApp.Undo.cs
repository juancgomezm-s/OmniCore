using System.Collections.ObjectModel;

using OmniCore.Client;
using OmniCore.Protocol;

using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    internal void ShowUndo(string? requestedEffect = null)
    {
        if (_window is null) return;
        CloseOverlay();
        if (HasActiveModelWork) { ShowMessage(_localization.Resolve("undo.busy")); return; }
        UndoReview? listing;
        try { listing = UndoProtocol.Decode(_client.Query(UndoProtocol.Query, CancellationToken.None)?.Json ?? "null"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException)
        { ShowMessage(_localization.Resolve("undo.unavailable")); return; }
        if (listing is null || listing.Error is not null)
        { ShowMessage(_localization.Resolve(listing?.Error ?? "undo.unavailable")); return; }
        var effects = listing.Files.GroupBy(file => file.EffectId).Select(group => (Id: group.Key, Path: group.First().Path)).ToList();
        if (listing.PendingEffectId is { } pending && !effects.Any(effect => effect.Id == pending))
            effects.Add((pending, _localization.Resolve("undo.recover")));
        var frame = OverlayFrame(_localization.Resolve("undo.title"), _window.Frame.Height - 3);
        frame.Id = "undo-review"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var list = new ListView { Id = "undo-effects", X = 2, Y = 2, Width = Dim.Percent(30), Height = Dim.Fill(6), CanFocus = true };
        list.SetSource(new ObservableCollection<string>(effects.Select(effect => effect.Id[..8] + "  "
            + SessionPreferencesPresentation.SafeText(effect.Path))));
#pragma warning disable CS0618
        var content = new TextView
        {
            Id = "undo-preview",
            X = Pos.Percent(34),
            Y = 2,
            Width = Dim.Fill(2),
            Height = Dim.Fill(6),
            ReadOnly = true,
            WordWrap = true
        };
#pragma warning restore CS0618
        var status = new Label { Id = "undo-status", X = 2, Y = Pos.AnchorEnd(5), Width = Dim.Fill(4), Height = 2 };
        var apply = new Button { Id = "undo-confirm", X = 2, Y = Pos.AnchorEnd(3), Text = _localization.Resolve("undo.apply"), Enabled = false };
        var cancel = new Button { Id = "undo-cancel", X = Pos.Right(apply) + 2, Y = Pos.AnchorEnd(3), Text = _localization.Resolve("undo.back") };
        UndoReview? displayed = null;
        string? selected = null;
        void Read()
        {
            displayed = null; selected = null; apply.Enabled = false;
            if (list.SelectedItem is not int index || index < 0 || index >= effects.Count)
            { content.Text = _localization.Resolve("undo.empty"); return; }
            selected = effects[index].Id;
            try
            {
                displayed = UndoProtocol.Decode(_client.Query(UndoProtocol.QueryFor(selected), CancellationToken.None)?.Json ?? "null");
                content.Text = UndoPresentation.Format(displayed, _localization);
                apply.Enabled = _client is ITrustedUserActionClient && displayed?.SessionId is not null
                    && displayed.Error is null && (displayed.RecoveryRequired || displayed.Files.Count > 0
                    && displayed.Files.All(file => file.State is "Ready" or "AlreadyRestored"));
                apply.Text = _localization.Resolve(displayed?.RecoveryRequired == true ? "undo.recover" : "undo.apply");
                status.Text = _localization.Resolve(displayed?.RecoveryRequired == true ? "undo.recovery_required" : "undo.help");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Text.Json.JsonException)
            { status.Text = _localization.Resolve("undo.unavailable"); }
        }
        apply.Accepted += (_, _) =>
        {
            if (selected is null || displayed is null || !apply.Enabled || HasActiveModelWork) return;
            var result = new ClientActionHandler().ExecuteAsync(new(ClientActionIds.WorkspaceUndo,
                    ["apply", selected, displayed.Revision, "--confirm"], "UndoConfirm"),
                new(_client, _localization), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            CloseOverlay(); ShowMessage(result.Text); PollEvents();
        };
        cancel.Accepted += (_, _) => CloseOverlay();
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; CloseOverlay(); } };
        list.ValueChanged += (_, _) => Read();
        frame.Add(list, content, status, apply, cancel);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        if (effects.Count > 0)
            list.SelectedItem = requestedEffect is null ? 0 : Math.Max(0, effects.FindIndex(effect => effect.Id == requestedEffect));
        Read(); list.SetFocus();
    }
}
