using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Startup_update_notice_preserves_drafts_and_can_defer_without_stopping_the_TUI() => RunTuiTest(fx =>
    {
        using var updates = new ReleaseStartupUpdatesTests.Fixture();
        var ready = updates.Ready().GetAwaiter().GetResult();
        fx.StartTui(); Type(fx, "draft stays here");
        fx.Invoke(() =>
        {
            fx.App.StartupUpdatesOverride = updates.Service;
            fx.App.StartupLaunchIdOverride = updates.First;
            fx.App.PollOnce();
        });
        fx.Wait(() => fx.App.MainWindow!.SubViews.Any(view => view.Id == "startup-update-ready" && view.Visible), "aviso listo");
        fx.Invoke(() => fx.App.MainWindow!.SubViews.Single(view => view.Id == "startup-update-ready").InvokeCommand(Command.Accept));
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "decisión de actualización");
        ClickId(fx, "release-update-install-now");
        Assert.False(fx.Read(() => fx.App.UpdateRestartRequested));
        Assert.Equal("None", updates.Service.Snapshot()!.Decision);
        Assert.Contains("borradores", fx.Read(() => OverlayText(fx.App.Overlay!)));
        Assert.Equal("draft stays here", fx.Read(() => fx.App.Composer!.Text));
        ClickId(fx, "release-update-next-launch");
        Assert.Equal("NextLaunch", updates.Service.Snapshot()!.Decision);
        Assert.Equal(ready.Revision, updates.Service.Snapshot()!.Revision);
        Assert.False(fx.Read(() => fx.App.UpdateRestartRequested));
        Assert.Equal(0, updates.Helper.Calls);
        fx.InjectKey(new Key(KeyCode.Esc));
        Assert.Equal("draft stays here", fx.Read(() => fx.App.Composer!.Text));
    });

    [Fact]
    public void Startup_update_settings_persist_the_explicit_channel_and_download_preference() => RunTuiTest(fx =>
    {
        using var updates = new ReleaseStartupUpdatesTests.Fixture();
        updates.Service.SavePreferences(new(Channel: ReleaseUpdateChannel.Preview, AutomaticDownload: false));
        fx.StartTui();
        fx.Invoke(() => { fx.App.StartupUpdatesOverride = updates.Service; fx.App.StartupLaunchIdOverride = updates.First; });
        OpenGlobalPalette(fx); PaletteSearch(fx, "updates.open"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "preferencias de actualización");
        fx.Invoke(() =>
        {
            var channel = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "release-update-channel");
            Assert.Equal(1, channel.SelectedItem);
            var auto = Descendants(fx.App.Overlay!).OfType<CheckBox>().Single(view => view.Id == "release-update-automatic");
            Assert.Equal(CheckState.UnChecked, auto.Value);
            channel.SelectedItem = 0;
            auto.Value = CheckState.Checked;
        });
        Assert.Equal(ReleaseUpdateChannel.Stable, updates.Service.Preferences().Channel);
        Assert.True(updates.Service.Preferences().AutomaticDownload);
        Assert.Equal(0, updates.Downloads); // Changing preferences does not start a network request.
    });

    [Fact]
    public void Startup_update_restart_records_the_session_then_exits_without_applying_inside_the_TUI() => RunTuiTest(fx =>
    {
        using var updates = new ReleaseStartupUpdatesTests.Fixture(); updates.Ready().GetAwaiter().GetResult();
        fx.StartTui();
        fx.Invoke(() =>
        {
            fx.App.StartupUpdatesOverride = updates.Service; fx.App.StartupLaunchIdOverride = updates.First;
            fx.App.PollOnce();
            fx.App.MainWindow!.SubViews.Single(view => view.Id == "startup-update-ready").InvokeCommand(Command.Accept);
        });
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "pantalla lista");
        ClickId(fx, "release-update-install-now");
        fx.Wait(() => fx.App.UpdateRestartRequested, "reinicio solicitado");
        Assert.Equal("Restart", updates.Service.Snapshot()!.Decision);
        Assert.Equal(Environment.CurrentDirectory, updates.Service.Snapshot()!.Workspace);
        Assert.Equal(0, updates.Helper.Calls);
    });

    [Fact]
    public void Startup_update_restart_does_not_discard_unsaved_preference_changes() => RunTuiTest(fx =>
    {
        using var updates = new ReleaseStartupUpdatesTests.Fixture(); updates.Ready().GetAwaiter().GetResult();
        fx.StartTui();
        fx.Invoke(() => { fx.App.StartupUpdatesOverride = updates.Service; fx.App.StartupLaunchIdOverride = updates.First; });
        fx.InjectKey(new Key(KeyCode.F4)); fx.Wait(() => fx.App.Overlay?.Id == "preferences-center", "preferencias");
        ChoosePreferenceCategory(fx, "general");
        fx.Invoke(() =>
        {
            var locale = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
            locale.SelectedItem = 2; locale.InvokeCommand(Command.Accept);
        });
        ClickId(fx, "preferences-updates-open");
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "actualizaciones desde preferencias");
        ClickId(fx, "release-update-install-now");
        Assert.False(fx.Read(() => fx.App.UpdateRestartRequested));
        Assert.Equal("None", updates.Service.Snapshot()!.Decision);
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay?.Id == "preferences-center", "regreso a preferencias");
        Assert.Equal("fr", fx.Read(() => fx.App.PreferencesDraftLocale));
    });

    [Fact]
    public void Startup_update_unsupported_install_keeps_the_explicit_metadata_checker() => RunTuiTest(fx =>
    {
        using var updates = new ReleaseStartupUpdatesTests.Fixture(development: true);
        var checks = 0;
        fx.StartTui();
        fx.Invoke(() =>
        {
            fx.App.StartupUpdatesOverride = updates.Service;
            fx.App.ReleaseUpdateCheckOverride = (request, _) =>
            {
                Interlocked.Increment(ref checks);
                return System.Threading.Tasks.Task.FromResult(new ReleaseUpdateCheckResult(request.Channel,
                    ReleaseUpdateStatus.UpToDate, ReleaseCompatibility.Unknown, request.InstalledVersion));
            };
        });
        OpenGlobalPalette(fx); PaletteSearch(fx, "updates.open"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "consulta manual conservada");
        ClickId(fx, "release-update-check");
        fx.Wait(() => Volatile.Read(ref checks) == 1, "metadatos consultados");
        Assert.Equal(0, updates.Downloads); Assert.Equal(0, updates.Helper.Calls);
    });
}
