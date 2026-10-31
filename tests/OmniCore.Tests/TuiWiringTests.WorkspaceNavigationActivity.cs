using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionOpening_workspace_navigation_preserves_directory_session_and_draft_while_work_is_active(bool backgroundDelegation) => RunTuiTest(fx =>
    {
        var releasePrincipal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var principal = new RouteResumeTurnHost { InvocationGate = releasePrincipal };
        var delegation = new BackgroundDelegationTurnHostFixture();
        ITuiTurnHost host = backgroundDelegation ? delegation : principal;
        fx.StartTui(turnHost: host, selectConversationOnOpen: true);
        fx.Wait(() => fx.App.Overlay?.Id == "conversation-selector", "opening choice visible");
        ClickId(fx, "conversation-continue-last");
        fx.Wait(() => fx.App.Overlay is null && !fx.App.ConversationSelectionPending, "explicit initial selection");
        var originalSession = fx.Server.LastSessionId();
        var originalDirectory = Environment.CurrentDirectory;
        var id = Guid.NewGuid().ToString("D");
        try
        {
            Type(fx, backgroundDelegation ? "/agent run " + id : "consulta principal en curso");
            KeyWithEffect(fx, KeyCode.Enter,
                () => backgroundDelegation ? delegation.Started.ContainsKey(id) : principal.ExecuteCount == 1,
                "controlled work is active");
            fx.Wait(() => fx.App.Composer!.Text == "", "submission clears composer");
            var target = Path.Combine(fx.Root, "another workspace while busy");
            Directory.CreateDirectory(target);
            var draft = "!cd \"" + target + "\"";
            Type(fx, draft);
            KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "navigation rejected while work is active");
            Assert.Null(fx.App.RequestedWorkspace);
            Assert.Equal(originalDirectory, Environment.CurrentDirectory);
            Assert.Equal(originalSession, fx.Server.LastSessionId());
            Assert.Equal(draft, fx.Read(() => fx.App.Composer!.Text));
            if (backgroundDelegation)
            {
                Assert.Empty(delegation.Completed);
                Assert.Empty(delegation.Cancelled);
                Assert.Equal(0, Volatile.Read(ref delegation.RootCalls));
            }
            else Assert.False(releasePrincipal.Task.IsCompleted);
        }
        finally
        {
            releasePrincipal.TrySetResult(0);
            delegation.Release(id);
        }
    });
}
