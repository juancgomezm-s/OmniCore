using OmniCore.Host;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    internal IWebLinkHost LinkHost { get; set; } = new WebLinkHost();
    internal Task LinkOpenTask { get; private set; } = Task.CompletedTask;
    private bool _openingLink;

    private void ConfigureConversationLinks(ConversationView conversation) =>
        conversation.LinkActivated += target => LinkOpenTask = OpenConversationLinkAsync(target);

    private async Task OpenConversationLinkAsync(string target)
    {
        if (_openingLink) return;
        _openingLink = true;
        try
        {
            if (await LinkHost.OpenAsync(target, CancellationToken.None).ConfigureAwait(false)) return;
            _window?.App?.Invoke(() => MessageBox.Query(_window.App, _localization.Resolve("links.title"),
                _localization.Resolve("links.openFailed"), _localization.Resolve("shell.ok")));
        }
        finally { _openingLink = false; }
    }
}
