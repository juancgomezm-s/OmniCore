using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private const string ProjectGitHub = "https://github.com/juancgomezm-s/OmniCore";
    private const string ProjectEmail = "juancgomezm@gmail.com";

    private void ShowHelp()
    {
        ShowPreferenceText(_localization.Resolve("shell.help_title"), KeyboardHelp(TuiHelpSection()) + "\n/keybindings — " + KeyBindingText("title"));
        if (_overlay is not { } frame) return;
        frame.Id = "help-screen";
        var about = new Button { Id = "help-about", X = Pos.AnchorEnd(22), Y = Pos.AnchorEnd(2),
            Text = _localization.Resolve("shell.about.open") };
        about.Accepted += (_, _) => ShowAbout();
        frame.Add(about); ApplyTheme(frame);
    }

    private void ShowAbout()
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(_localization.Resolve("shell.about.title"), 15);
        frame.Id = "about-screen";
        var email = new TextField { Id = "about-email", X = 4, Y = 4, Width = Dim.Fill(8),
            ReadOnly = true, Text = ProjectEmail };
        var github = new Button { Id = "about-github", X = 4, Y = 7, Width = Dim.Fill(8),
            TextAlignment = Alignment.Start, Text = ProjectGitHub };
        github.Accepted += (_, _) => LinkOpenTask = OpenConversationLinkAsync(ProjectGitHub);
        var back = new Button { Id = "about-back", X = 4, Y = Pos.AnchorEnd(2), Text = Pref("back") };
        back.Accepted += (_, _) => ShowHelp();
        frame.Add(new Label { X = 4, Y = 2, Text = _localization.Resolve("shell.about.email") }, email,
            new Label { X = 4, Y = 6, Text = "GitHub" }, github, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); email.SetFocus();
    }
}
