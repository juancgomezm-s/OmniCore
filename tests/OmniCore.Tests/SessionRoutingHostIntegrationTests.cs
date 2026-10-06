using Microsoft.Data.Sqlite;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Production Host/SQLite/protocol/runtime boundaries. No provider call or real usage.</summary>
public sealed class SessionRoutingHostIntegrationTests
{
    private static ModelRoute Route(string endpoint = "https://paid.example/v1") =>
        ModelRoute.DefaultForModel("paid-model", "paid", endpoint, ProviderFamily.OpenAiChatCompatible);

    [Fact]
    public void Initial_policy_uses_explicit_billing_only_and_configuration_cannot_expand_existing_session()
    {
        var loaded = new ConfigLoader().Load("""
            providers:
              local: { baseUrl: 'http://127.0.0.1:8080/v1', auth: none, billingMode: Local }
              unknown: { baseUrl: 'http://127.0.0.1:8081/v1', auth: none }
              paid: { baseUrl: 'https://paid.example/v1', authRef: synthetic, billingMode: MeteredCurrency }
            """, """
            models:
              local-model: { provider: local, context: 8192 }
              unknown-model: { provider: unknown, context: 8192 }
              paid-model: { provider: paid, context: 8192 }
            """);
        var baseline = ModelRoutingHost.InitialSessionPolicy(loaded, "local");
        Assert.Equal(BillingMode.Local, Assert.Single(baseline.AllowedRoutes).BillingMode);
        Assert.Empty(ModelRoutingHost.InitialSessionPolicy(loaded, "unknown").AllowedRoutes);
        Assert.Empty(ModelRoutingHost.InitialSessionPolicy(loaded, "paid").AllowedRoutes);
        var server = OmniHost.CreateInMemoryServer();
        server.ConfigureNewSessionRoutingPolicy(baseline);
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"session.input\",\"text\":\"probe\"}"), CancellationToken.None).Status);
        var session = server.LastSessionId()!;
        server.ConfigureNewSessionRoutingPolicy(SessionRoutingPolicy.Empty());
        Assert.Equal(RuntimeCommandOutcomeKind.NoOp, server.EnsureSessionRoutingPolicy(session).Outcome?.Kind);
        var persisted = SessionRoutingAuthorization.Read(server.AcquireStore().ReadFrom(session, 1), server.AcquireCodecs(), session)!;
        Assert.Equal(baseline.AllowedRoutes, persisted.AllowedRoutes);
        Assert.Equal(1, persisted.Revision);
        Assert.Throws<ArgumentException>(() => server.ConfigureNewSessionRoutingPolicy(new SessionRoutingPolicy(1,
            [AuthorizedModelRoute.From(Route(), BillingMode.MeteredCurrency)], [BillingMode.MeteredCurrency], false, null, "paid")));
    }

    [Theory]
    [InlineData(BillingMode.MeteredCurrency)]
    [InlineData(BillingMode.Unknown)]
    public void Consent_is_deferred_idempotent_correlated_and_survives_sqlite_reopen(BillingMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(db);
            var codecs = EventCodecs.Create();
            var stateFile = Path.Combine(root, "lastsession.txt");
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, new FileArtifactStore(root));
            Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"routing probe\"}"), CancellationToken.None).Status);
            var session = server.LastSessionId()!;
            var run = server.LastRunId()!;
            var before = store.CurrentSequence(session);
            var blocked = server.AuthorizeModelRoute(session, run, Route(), mode);
            Assert.False(blocked.Authorized);
            Assert.NotNull(blocked.Interaction);
            Assert.Equal(RuntimeCommandOutcomeKind.Deferred, blocked.Ack.Outcome?.Kind);
            Assert.Equal(before + 1, blocked.Ack.FirstSeq);
            Assert.Equal(before + 1, blocked.Ack.LastSeq);
            var written = Assert.Single(store.ReadFrom(session, before + 1));
            Assert.Equal(run, written.RunId);
            Assert.IsType<CommandCausation>(written.Causation);
            var again = server.AuthorizeModelRoute(session, run, Route(), mode);
            Assert.Equal(blocked.Interaction, again.Interaction);
            Assert.Equal(before + 1, store.CurrentSequence(session));

            var projection = new ClientProjection(Localization.Spanish());
            var state = ClientState.Empty();
            foreach (var wire in new ProtocolMapper(codecs).Map(store.ReadFrom(session, 1)))
                state = projection.Apply(state, wire);
            var overlay = Assert.Single(state.Overlays, item => item.Id == blocked.Interaction.ToString());
            Assert.Equal(new[] { "deny", "allow_route" }, overlay.OptionIds);
            Assert.Equal("deny", overlay.DefaultOptionId);

            var approval = server.RespondToInteraction(blocked.Interaction!, "allow_route");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, approval.Outcome?.Kind);
            Assert.True(server.AuthorizeModelRoute(session, run, Route(), mode).Authorized);
            store.Close();
            store = new SqliteEventStore(db);
            server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, new FileArtifactStore(root));
            Assert.Equal(session, server.LastSessionId());
            var allowed = server.AuthorizeModelRoute(session, run, Route(), mode);
            Assert.True(allowed.Authorized);
            Assert.Equal(RuntimeCommandOutcomeKind.NoOp, allowed.Ack.Outcome?.Kind);
            Assert.Equal(2, SessionRoutingAuthorization.Read(store.ReadFrom(session, 1), codecs, session)!.Revision);
            Assert.False(server.AuthorizeModelRoute(session, run, Route("https://changed.example/v1"), mode).Authorized);
            Assert.False(server.AuthorizeModelRoute(SessionId.New(), run, Route(), mode).Authorized);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + db);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Runtime_defers_to_live_client_or_denies_without_one_before_invocation(bool liveClient)
    {
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"session.input\",\"text\":\"routing probe\"}"), CancellationToken.None).Status);
        var session = server.LastSessionId()!;
        var run = server.LastRunId()!;
        var runtime = OmniCliRuntime.Create(Path.GetTempPath());
        runtime.UseConsoleInput = false;
        runtime.HasInteractionClient = liveClient;
        var output = new List<string>();
        Assert.Equal(liveClient ? 3 : 1, runtime.AuthorizeRouteForInvocation(server, session, run,
            Route(), BillingMode.Unknown, output.Add, "es"));
        var events = server.AcquireStore().ReadFrom(session, 1).Select(server.AcquireCodecs().Decode).ToArray();
        var request = Assert.Single(events.OfType<InteractionRequested>(), item => item.Kind == InteractionKind.ModelRouteConsent);
        if (liveClient)
        {
            Assert.Contains(output, line => line.Contains("InputRequired") && line.Contains("ModelRouteConsent"));
            Assert.DoesNotContain(events, item => item is InteractionResolved);
            Assert.Equal("ok", server.RespondToInteraction(request.InteractionId, "allow_route").Status);
            Assert.Null(runtime.AuthorizeRouteForInvocation(server, session, run, Route(), BillingMode.Unknown, output.Add, "es"));
        }
        else
        {
            var resolution = Assert.Single(events.OfType<InteractionResolved>());
            Assert.Equal(InteractionCause.NoClient, resolution.Cause);
            Assert.Equal("deny", resolution.OptionId);
            Assert.DoesNotContain(events, item => item is SessionRoutingPolicyRevised);
        }
    }
}
