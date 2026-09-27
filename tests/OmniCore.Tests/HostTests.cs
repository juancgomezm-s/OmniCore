using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class HostTests
{
    [Fact]
    public async Task CreateInMemoryServer_serves_sim_command_ok()
    {
        var server = OmniHost.CreateInMemoryServer();
        var payload = "{" + JsonObj.Field("cmd", "sim") + "}";
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), payload), TestContext.Current.CancellationToken);

        Console.WriteLine("sim status=" + ack.Status + " error=" + (ack.Error ?? "-"));
        Assert.Equal("ok", ack.Status);
    }

    [Fact]
    public async Task InMemoryServer_query_state_returns_milestone()
    {
        var server = OmniHost.CreateInMemoryServer();
        var result = server.Query("state", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("M2", ParseField(result!.Json, "milestone"));
    }

    [Fact]
    public async Task Unknown_command_is_rejected()
    {
        var server = OmniHost.CreateInMemoryServer();
        var ack = server.Send(
            WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("query", "unknown") + "}"),
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", ack.Status);
    }

    [Fact]
    public async Task Ordered_events_stream_includes_sim_resume_flow()
    {
        // Flujo completo del CLI sobre IOmniClient (ADR-0019): sim con crash + resume en el
        // mismo server. El server reconcilia la toolcall huérfana.
        var server = OmniHost.CreateInMemoryServer();

        var simAck = server.Send(
            WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "sim")
                + "," + JsonObj.Field("scenario", "with-tool-crash") + "}"),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", simAck.Status); // el crash deja el run Running (esperado) → cp 0

        var resumeAck = server.Send(
            WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "sim.resume") + "}"),
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", resumeAck.Status);

        var events = server.SubscribeSince(0);
        Assert.True(events.Count >= 2, "El server emite eventos de sim y de resume");
    }

    [Fact]
    public async Task Server_feeds_audit_sink_on_permissions()
    {
        // INV-012 + ADR-0043: el audit sink se alimenta de los eventos de permisos del run.
        // Con un escenario Write (Allow), el sim emite permission_granted y el server lo audita.
        var codecs = OmniCore.Infrastructure.EventCodecs.Create();
        var store = new OmniCore.Infrastructure.InMemoryEventStore();
        var audit = new OmniCore.Infrastructure.InMemoryAuditSink();
        var server = new OmniServer(store, codecs, audit);

        var ack = server.Send(
            WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "sim")
                + "," + JsonObj.Field("scenario", "with-tools") + "}"),
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", ack.Status);
        Assert.True(audit.Count() >= 1, "El audit debe registrar al menos un evento de permiso");
        var records = audit.Records();
        Assert.Contains("toolcall.permission_evaluated", records[0].EventName);
    }

    private static string? ParseField(string json, string field)
    {
        var map = JsonObj.Parse(json);
        return map.TryGetValue(field, out var v) ? v : null;
    }
}