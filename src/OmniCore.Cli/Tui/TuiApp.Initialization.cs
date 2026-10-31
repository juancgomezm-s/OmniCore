using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private bool HandleInitializationSlash(string input)
    {
        if (!CommandLineParser.TryParse(input, out var parsed) || parsed is null || parsed.Name != "initialize") return false;
        if (parsed.Arguments.Count > 0) { ShowMessage(_localization.Resolve("project.initialize.usage")); return true; }
        if (HasActiveModelWork) { ShowMessage(_localization.Resolve("project.initialize.busy")); return true; }
        if (_turnHost is null) { ShowMessage(_localization.Resolve("project.initialize.unavailable")); return true; }
        try
        {
            var plan = _turnHost.InitializeProject(CancellationToken.None);
            _composer!.Text = "";
            HideCommandHelper();
            InvalidateAutocompleteCache();
            ShowInitializationResult(plan);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            var key = exception.Message.StartsWith("project.initialize.", StringComparison.Ordinal)
                ? exception.Message : "project.initialize.failed";
            ShowMessage(_localization.Resolve(key));
        }
        return true;
    }

    private void ShowInitializationResult(ProjectInitializationPlan plan)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(_localization.Resolve("project.initialize.title"), _window.Frame.Height - 3);
        frame.Id = "project-initialization-result";
        frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        const string start = "<!-- omnicore:project:start -->";
        const string end = "<!-- omnicore:project:end -->";
        var first = plan.Content.IndexOf(start, StringComparison.Ordinal);
        var last = first >= 0 ? plan.Content.IndexOf(end, first, StringComparison.Ordinal) : -1;
        var generated = first >= 0 && last >= first ? plan.Content[first..(last + end.Length)] : "";
#pragma warning disable CS0618 // The pinned Terminal.Gui provides scrolling and copy through its read-only TextView.
        var content = new TextView
        {
            Id = "project-initialization-content", X = 2, Y = 2, Width = Dim.Fill(4), Height = Dim.Fill(4),
            ReadOnly = true, WordWrap = true, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
            Text = ProjectInitializationPresentation.Format(plan, _localization) + "\n\n"
                + _localization.Resolve("project.initialize.content") + "\n" + generated,
        };
#pragma warning restore CS0618
        var close = new Button { Id = "project-initialization-close", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = Pref("close") };
        close.Accepted += (_, args) => { args.Handled = true; CloseOverlay(); _composer!.SetFocus(); };
        frame.KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Esc) return;
            key.Handled = true; CloseOverlay(); _composer!.SetFocus();
        };
        frame.Add(content, close);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); content.SetFocus();
    }
}
