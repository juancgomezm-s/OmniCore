using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class PetAdmissionClient(IOmniClient inner) : IOmniClient
    {
        public int RejectedActions;
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            if (JsonObj.Parse(command.PayloadJson).GetValueOrDefault("cmd") == "companion.pet")
            {
                RejectedActions++;
                return new(command.MessageId, "busy", "session_writer_owned",
                    RuntimeCommandOutcome.Deferred("session_writer_owned"));
            }
            return inner.Send(command, cancellationToken);
        }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) => inner.Query(name, cancellationToken);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Companion_button_admission_errors_show_localized_recovery_instead_of_wire_keys(string locale) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        var client = new PetAdmissionClient(fx.Server);
        fx.StartTui(client: client, initialColumns: 140, initialRows: 42,
            localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "pet connected");
        if (!fx.Read(() => fx.App.SidebarOpen)) KeyWithEffect(fx, KeyCode.F2, () => fx.App.SidebarOpen, "sidebar");
        var before = fx.Read(() => fx.App.CompanionPet);
        fx.Invoke(() => Descendants(fx.App.Sidebar!).OfType<Button>()
            .Single(view => view.Id == "pixel-widget-action").SetFocus());
        fx.InjectKey(new Key(KeyCode.Enter));
        var expected = new Localization(locale).ResolveWire("session_writer_owned");
        fx.Wait(() => fx.Read(() => fx.App.Overlay is not null && OverlayText(fx.App.Overlay).Contains(expected, StringComparison.Ordinal)),
            "localized writer recovery notice");
        Assert.Equal(1, client.RejectedActions);
        Assert.DoesNotContain("session_writer_owned", fx.Read(() => OverlayText(fx.App.Overlay!)));
        Assert.Equal(before!.Xp, fx.Read(() => fx.App.CompanionPet!.Xp));
    });

    [Fact]
    public void Companion_button_works_in_a_real_readonly_session_without_writing_its_journal() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        var observer = OmniHost.OpenPersistentServer(Path.Combine(fx.JournalDirectory, "journal.db"),
            Path.Combine(fx.Root, "observer-audit"));
        observer.ConfigureWorkspaceRoot(fx.Workspace);
        observer.ConfigurePlatformPaths(() => new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"),
            Path.Combine(fx.Root, "host-config")));
        try
        {
            Assert.False(observer.EnsureWriterOwnershipForCommand());
            var sequence = fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!);
            fx.StartTui(client: observer, initialColumns: 120, initialRows: 40);
            fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "shared pet connected");
            if (!fx.Read(() => fx.App.SidebarOpen)) KeyWithEffect(fx, KeyCode.F2, () => fx.App.SidebarOpen, "sidebar");
            fx.Invoke(() => Descendants(fx.App.Sidebar!).OfType<Button>()
                .Single(view => view.Id == "pixel-widget-action").SetFocus());
            fx.InjectKey(new Key(KeyCode.Enter));
            fx.Wait(() => fx.App.CompanionPet?.Reaction?.AnimationId == "pet", "petting from readonly observer accepted");
            Assert.Null(fx.Read(() => fx.App.Overlay));
            Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!));
            Assert.False(observer.EnsureWriterOwnershipForCommand());
        }
        finally
        {
            fx.Application?.RequestStop();
            fx.Loop?.Join(5000);
            observer.ClosePersistentStore();
        }
    });
}
