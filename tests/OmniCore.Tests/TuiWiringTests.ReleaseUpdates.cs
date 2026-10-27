using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", "Actualizaciones", "Comprobar ahora", "Sin consultar", 80, 25)]
    [InlineData("en", "Updates", "Check now", "Not checked", 130, 38)]
    [InlineData("fr", "Mises à jour", "Vérifier maintenant", "Non vérifié", 80, 25)]
    [InlineData("it", "Aggiornamenti", "Controlla ora", "Non verificato", 130, 38)]
    [InlineData("es", "Actualizaciones", "Comprobar ahora", "Sin consultar", 130, 38)]
    [InlineData("en", "Updates", "Check now", "Not checked", 80, 25)]
    [InlineData("fr", "Mises à jour", "Vérifier maintenant", "Non vérifié", 130, 38)]
    [InlineData("it", "Aggiornamenti", "Controlla ora", "Non verificato", 80, 25)]
    public void Release_update_view_is_local_and_network_is_used_only_by_explicit_check(string locale, string title, string checkLabel, string ready, int width, int height) => RunTuiTest(fx =>
    {
        var requests = 0;
        ReleaseUpdateCheckRequest? sent = null;
        fx.StartTui(localeSelection: new(locale, LocaleSelectionSource.CommandLine), initialColumns: width, initialRows: height);
        fx.App.ReleaseUpdateCheckOverride = (request, _) =>
        {
            Interlocked.Increment(ref requests); sent = request;
            return System.Threading.Tasks.Task.FromResult(new ReleaseUpdateCheckResult(request.Channel, ReleaseUpdateStatus.UpdateAvailable,
                ReleaseCompatibility.Unknown, request.InstalledVersion, "2.0.0", "v2.0.0",
                "1111111111111111111111111111111111111111",
                "https://github.com/juancgomezm-s/OmniCore/releases/tag/v2.0.0", "safe\u001b[31mnotes",
                "win-x64", "package.tgz", 42, new string('a', 64), DiagnosticCode: "fixture",
                Requirements: new("10.0", "10.99", "20.0", "20.99", "1.0", "1.99", "1", "1.99")));
        };
        Type(fx, "draft remains intact");
        OpenGlobalPalette(fx); PaletteSearch(fx, "updates.open");
        fx.Read(() =>
        {
            Assert.Equal(0, requests);
            Assert.Contains("updates.open", OverlayText(fx.App.Overlay!));
        });
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "paleta abre la pantalla sin consultar la red");
        fx.Read(() =>
        {
            Assert.Equal(0, requests);
            Assert.Contains(title, fx.App.Overlay!.Title.ToString());
            Assert.Contains(checkLabel, OverlayText(fx.App.Overlay!));
            Assert.Contains(ready, OverlayText(fx.App.Overlay!));
            var channel = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "release-update-channel");
            channel.SetFocus();
        });
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Read(() =>
        {
            Assert.Equal("release-updates", fx.App.Overlay!.Id);
            Assert.Equal(0, requests);
            var channel = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "release-update-channel");
            channel.SelectedItem = 1;
        });
        ClickId(fx, "release-update-check");
        fx.Wait(() => fx.Read(() => OverlayText(fx.App.Overlay!).Contains("fixture")), "el botón consulta e informa el resultado");
        fx.Read(() =>
        {
            Assert.Equal(1, requests);
            Assert.Equal(ReleaseUpdateChannel.Preview, sent!.Channel);
            var content = OverlayText(fx.App.Overlay!);
            Assert.Contains("2.0.0", content); Assert.Contains("SHA-256", content);
            Assert.DoesNotContain('\u001b', content);
            Assert.Contains(new Localization(locale).Resolve("updates.metadata_pending"), content);
            Assert.Contains(new Localization(locale).Resolve("updates.requirements"), content);
            Assert.Equal("draft remains intact", fx.App.Composer!.Text);
        });
        fx.Invoke(() => Descendants(fx.App.Overlay!).Single(view => view.Id == "release-update-details").SetFocus());
        if (height == 25) fx.InjectKey(new Key(KeyCode.PageDown));
        ExportPreferenceFrame(fx, width, height, "release-updates-" + locale + "-" + width, title);
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "Escape cierra la pantalla de actualizaciones");
        Assert.Equal("draft remains intact", fx.Read(() => fx.App.Composer!.Text));
    }, noColor: false);

    [Fact]
    public void Closing_updates_cancels_the_request_and_ignores_late_completion() => RunTuiTest(fx =>
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<ReleaseUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.StartTui();
        fx.App.ReleaseUpdateCheckOverride = (request, token) => { entered.TrySetResult(token); return complete.Task; };
        OpenGlobalPalette(fx); PaletteSearch(fx, "updates.open"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "pantalla abierta");
        ClickId(fx, "release-update-check");
        Assert.True(entered.Task.Wait(TimeSpan.FromSeconds(3)));
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay is null, "pantalla cerrada");
        Assert.True(entered.Task.Result.IsCancellationRequested);
        complete.TrySetResult(new(ReleaseUpdateChannel.Stable, ReleaseUpdateStatus.UpdateAvailable,
            ReleaseCompatibility.Unknown, "1.0.0", "2.0.0", DiagnosticCode: "late"));
        Thread.Sleep(80); fx.App.PollOnce();
        Assert.Null(fx.Read(() => fx.App.Overlay));
    });

    [Fact]
    public void Approval_arriving_during_check_replaces_the_view_and_ignores_late_result() => RunTuiTest(fx =>
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<ReleaseUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.StartTui(); Type(fx, "approval keeps this draft");
        fx.App.ReleaseUpdateCheckOverride = (request, token) => { entered.TrySetResult(token); return complete.Task; };
        OpenGlobalPalette(fx); PaletteSearch(fx, "updates.open"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "pantalla abierta");
        ClickId(fx, "release-update-check"); Assert.True(entered.Task.Wait(TimeSpan.FromSeconds(3)));
        var interaction = InteractionId.New();
        fx.Invoke(() =>
        {
            fx.AppendInteraction(interaction, "Permission", "{\"operation\":\"test\"}", "[{\"id\":\"deny\"}]", "deny");
            fx.App.PollOnce();
        });
        var approval = fx.Read(() => fx.App.Overlay);
        Assert.NotNull(approval); Assert.True(entered.Task.Result.IsCancellationRequested);
        complete.TrySetResult(new(ReleaseUpdateChannel.Stable, ReleaseUpdateStatus.UpdateAvailable,
            ReleaseCompatibility.Unknown, "1.0.0", "2.0.0", DiagnosticCode: "late"));
        Thread.Sleep(80); fx.App.PollOnce();
        Assert.Same(approval, fx.Read(() => fx.App.Overlay));
        Assert.Equal("approval keeps this draft", fx.Read(() => fx.App.Composer!.Text));
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.Decoded<InteractionResolved>().Any(evt => evt.InteractionId == interaction), "Esc mantiene la denegación predeterminada");
        Assert.Null(fx.Read(() => fx.App.Overlay));
    });

    [Fact]
    public void General_preferences_update_subpage_returns_without_saving_the_locale_draft() => RunTuiTest(fx =>
    {
        var requests = 0;
        fx.StartTui(); Type(fx, "unsaved composer text");
        fx.InjectKey(new Key(KeyCode.F4)); fx.Wait(() => fx.App.Overlay?.Id == "preferences-center", "preferencias generales");
        ChoosePreferenceCategory(fx, "general");
        fx.Invoke(() =>
        {
            var locale = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
            locale.SelectedItem = 2;
            locale.InvokeCommand(Command.Accept);
        });
        fx.App.ReleaseUpdateCheckOverride = (_, _) => { Interlocked.Increment(ref requests); throw new InvalidOperationException("must not run while opening"); };
        ClickId(fx, "preferences-updates-open");
        fx.Wait(() => fx.App.Overlay?.Id == "release-updates", "subpágina abierta");
        Assert.Equal(0, requests);
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay?.Id == "preferences-center", "regresa a preferencias generales");
        fx.Read(() =>
        {
            Assert.Contains("Français", OverlayText(fx.App.Overlay!));
            Assert.Equal("unsaved composer text", fx.App.Composer!.Text);
        });
        Assert.Equal(0, requests);
    });
}
