using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Peer_message_shows_a_lateral_notice_without_starting_a_human_turn_or_changing_the_draft() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        fx.Wait(() => host.CompanionHub.Read().Instances.Length == 1, "presencia del principal");
        var here = host.CompanionHub.Read().Instances.Single();
        fx.Invoke(() => fx.App.Composer!.Text = "borrador conservado");
        host.CompanionHub.Heartbeat(new CompanionInstance("peer", "another-project", "another-session", null,
            "Disponible", DateTimeOffset.UtcNow, [], SupportsLateral: true));
        host.CompanionHub.SendPeer("peer", "another-session", here.Id, "Dato lateral; no es una orden del usuario", Guid.NewGuid().ToString());
        fx.Wait(() => host.CompanionHub.Read().Notices.Any(n => n.Title.StartsWith("Mensaje lateral")), "aviso lateral");
        // A new UI poll actually attempts normal instruction claims; peers must stay out of that path.
        fx.Wait(() => host.CompanionHub.Read().Instances.Single(i => i.Id == here.Id).Seen > here.Seen.AddSeconds(2), "siguiente sondeo");
        Assert.Null(host.Input);
        Assert.Equal("borrador conservado", fx.Read(() => fx.App.Composer!.Text));
        Assert.Equal("lateral", Assert.Single(host.CompanionHub.ReadPeer(here.Id, here.Session).Messages).Relation);
    });
}
