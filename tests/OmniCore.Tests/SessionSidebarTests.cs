using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Microsoft.Data.Sqlite;

namespace OmniCore.Tests;

public sealed class SessionSidebarTests
{
    private static CommandAck Send(OmniServer server, string json) => server.Send(WireEnvelope.Command(Ids.NewV7(), json), TestContext.Current.CancellationToken);

    [Fact]
    public void Empty_host_has_no_fabricated_session_or_plan()
    {
        var server = new OmniServer(new InMemoryEventStore(), EventCodecs.Create(), new InMemoryAuditSink());
        Assert.Null(SidebarJson.Decode(server.Query("sessionSidebar", TestContext.Current.CancellationToken)!.Json));
    }

    [Fact]
    public void Read_model_keeps_interleaved_runs_separate_including_late_plan_updates()
    {
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        Assert.Equal("ok", Send(server, """{"cmd":"session.input","text":"primera sesión","mode":"act"}""").Status);
        var session = server.LastSessionId()!; var run = server.LastRunId()!;
        var stream = new EventStream(store, codecs, session);
        var original = PlanProjection.Replay(codecs, store.ReadFrom(session, 1)).Latest()!;
        var child = PlanItemId.New();
        stream.Append(new PlanItemAdded(child, original.Id, "Anterior", 2, original.Items[0].Id, [], true, []));
        Assert.Equal("ok", Send(server, """{"cmd":"run.cancel"}""").Status);
        Assert.Equal("ok", Send(server, """{"cmd":"session.input","text":"segundo run","mode":"act"}""").Status);
        var secondRun = server.LastRunId()!;
        // A late update belongs to the first Run despite appearing after the second run.created.
        using (ExecutionScope.Begin(new(run)))
        {
            stream.Append(new PlanItemUpdated(child, "Descripción corregida", new() { ["category"] = "validation" }));
            stream.Append(new PlanItemStarted(child));
            stream.Append(new PlanItemBlocked(child, "missing input"));
        }
        var before = store.ReadFrom(session, 1).Count;
        var first = SessionSidebarReader.Read(store, codecs, session, run, false);
        Assert.False(first.ProjectionUnavailable);
        Assert.Equal("primera sesión", first.Title);
        var row = Assert.Single(first.Plan!.Items, i => i.Id == child.ToString());
        Assert.Equal("Descripción corregida", row.Description); Assert.Equal("Blocked", row.State);
        Assert.Equal("validation", PlanProjection.Replay(codecs, store.ReadFrom(session, 1).Where(e => e.RunId == run).ToArray()).Item(child)!.Metadata["category"]);
        var latest = SidebarJson.Decode(server.Query("sessionSidebar", TestContext.Current.CancellationToken)!.Json)!;
        Assert.Equal(secondRun.ToString(), latest.RunId);
        Assert.DoesNotContain(latest.Plan!.Items, i => i.Id == child.ToString());
        Assert.Equal(before, store.ReadFrom(session, 1).Count); // queries never append
    }

    [Fact]
    public void Client_discards_stale_or_cross_session_snapshots_and_clears_on_switch()
    {
        var projection = new SessionSidebarProjection(); projection.Activate("one");
        var snapshot = new SessionSidebarSnapshot("one", "run", 10, "Title", null, "act", "Running", null, false, false);
        Assert.True(projection.Apply(SidebarJson.Decode(SidebarJson.Encode(snapshot))!));
        Assert.False(projection.Apply(snapshot with { BasedOnJournalSequence = 9 }));
        Assert.False(projection.Apply(snapshot with { SessionId = "other" }));
        Assert.False(projection.Apply(snapshot with { Plan = new("plan", "other-run", 1, []) }));
        projection.Activate("two"); Assert.Null(projection.Snapshot);
        Assert.False(projection.Apply(snapshot));
    }

    [Fact]
    public void Persistent_host_reopens_the_same_sidebar_without_appending_events()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-sidebar-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var database = Path.Combine(root, "journal.db");
        OmniServer? server = null;
        try
        {
            server = OmniHost.OpenPersistentServer(database, Path.Combine(root, "audit"));
            server.ConfigureWorkspaceRoot(workspace);
            Send(server, """{"cmd":"session.input","text":"Reabrir presentación","mode":"act"}""");
            var session = server.LastSessionId()!;
            var stream = new EventStream(server.AcquireStore(), server.AcquireCodecs(), session);
            var plan = PlanProjection.Replay(server.AcquireCodecs(), server.AcquireStore().ReadFrom(session, 1)).Latest()!;
            var child = PlanItemId.New();
            stream.Append(new PlanItemAdded(child, plan.Id, "Revisión persistida", 2, plan.Items[0].Id, [], true, []));
            stream.Append(new PlanItemStarted(child));
            var first = server.Query("sessionSidebar", TestContext.Current.CancellationToken)!.Json;
            var before = server.AcquireStore().ReadFrom(session, 1).Count;
            Assert.IsType<SqliteEventStore>(server.AcquireStore()).Close(); server = null; SqliteConnection.ClearAllPools();
            server = OmniHost.OpenPersistentServer(database, Path.Combine(root, "audit"));
            Assert.Equal(first, server.Query("sessionSidebar", TestContext.Current.CancellationToken)!.Json);
            Assert.Equal(before, server.AcquireStore().ReadFrom(session, 1).Count);
        }
        finally
        {
            (server?.AcquireStore() as SqliteEventStore)?.Close(); SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Context_and_session_consumption_are_distinct_and_foreign_observations_are_ignored()
    {
        static Metric<long?> Number(long value) => new(MetricAvailability.Reported, value, "fixture");
        var now = DateTimeOffset.UtcNow;
        var canonical = new SessionSidebarSnapshot("s", "r", 3, "title", null, "act", "Running", null, false, false);
        var context = new SessionContextMeasurement("s", "turn", 1, "actual-model", Number(100), Number(16000), Number(8192),
            new(MetricAvailability.Reported, .625, "fixture"), [new("Files", new(MetricAvailability.Estimated, 40, "fixture"))], Number(5), now);
        var usage = new ConversationUsageMeasurement("s", new(MetricAvailability.Reported, new(900, 100, 30, 10), "fixture"), Number(1000),
            new(MetricAvailability.Estimated, new(.04m, "USD"), "fixture"), 3, 2, 0, now);
        var observation = new SessionObservabilitySnapshot("s", 3, now, true, context, null, usage, [], [], 0);
        string Text(SessionObservabilitySnapshot obs) => string.Join("\n", SessionSidebarPresentation.Build(canonical, obs, "actual-model", "act", "es", true)
            .SelectMany(w => ((ListWidgetModel)w.Build(ClientState.Empty(), WidgetSize.Expanded)).Rows).Select(r => r.Text));
        var text = Text(observation);
        Assert.Contains("Tokens: 100", text); Assert.Contains("Tokens sesión: 1,000", text);
        Assert.Contains("Files: ≈40", text); Assert.Contains("Costo sesión: ≈0.0400 USD", text);
        Assert.Contains("Actualizando solicitud", text);
        var foreign = Text(observation with { SessionId = "foreign" });
        Assert.DoesNotContain("Tokens: 100", foreign); Assert.DoesNotContain("Tokens sesión: 1,000", foreign);
    }

    [Fact]
    public void Session_stays_fixed_above_blocked_plan_and_leaf_progress_excludes_containers()
    {
        var plan = new SidebarPlan("p", "r", 3, [new("root", null, "Container", "InProgress", 1),
            new("a", "root", "Done", "Completed", 1), new("b", "root", "Waiting", "Blocked", 2)]);
        var snapshot = new SessionSidebarSnapshot("s", "r", 1, "Real title", null, "act", "Running", plan, false, false);
        var widgets = new SidebarHost(SessionSidebarPresentation.Build(snapshot, null, "actual/model", "act", "es", false)).Build(ClientState.Empty(), WidgetSize.Normal);
        Assert.Equal("core.session", widgets[0].Widget.Id);
        Assert.Equal("core.plan", widgets[1].Widget.Id);
        var rendered = Assert.IsType<ListWidgetModel>(widgets[1].Model);
        Assert.Equal("PLAN 1/2 · rev.3", rendered.Title);
        Assert.Equal("  Waiting", rendered.Rows[2].Text); Assert.Equal(ThemeRole.Attention, rendered.Rows[2].Role);
        Assert.Contains(Assert.IsType<ListWidgetModel>(widgets[0].Model).Rows, row => row.Text.Contains("actual/model"));
    }

    [Theory]
    [InlineData(MetricAvailability.Unknown, 50L, "—")]
    [InlineData(MetricAvailability.NotSupported, null, "—")]
    [InlineData(MetricAvailability.Estimated, 50L, "≈50")]
    [InlineData(MetricAvailability.Reported, 0L, "0")]
    [InlineData(MetricAvailability.Stale, 50L, "anterior · 50")]
    public void Metrics_keep_availability_instead_of_inventing_zero(MetricAvailability availability, long? value, string expected) =>
        Assert.Equal(expected, SessionSidebarPresentation.Count(new(availability, value, "fixture")));

    [Fact]
    public void Invalid_plan_is_unavailable_and_not_a_healthy_empty_plan()
    {
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        Send(server, """{"cmd":"session.input","text":"test"}""");
        var session = server.LastSessionId()!; var run = server.LastRunId()!;
        var payload = new PlanItemStarted(PlanItemId.New());
        store.Append(session, DomainEvent.Create(session, payload.Type(), 1, null, run, run, null, null, null, null, null, [],
            codecs.CodecFor(payload.Type()).Encode(payload)), DurabilityClass.Standard, TestContext.Current.CancellationToken);
        var snapshot = SidebarJson.Decode(server.Query("sessionSidebar", TestContext.Current.CancellationToken)!.Json)!;
        Assert.True(snapshot.ProjectionUnavailable); Assert.Null(snapshot.Plan);
        var widgets = SessionSidebarPresentation.Build(snapshot, null, "model", "plan", "en", true);
        var diagnostics = Assert.IsType<ListWidgetModel>(widgets.Single(w => w.Id == "core.diagnostics").Build(ClientState.Empty(), WidgetSize.Expanded));
        Assert.Contains(diagnostics.Rows, row => row.Text == "Plan unavailable" && row.Role == ThemeRole.Error);
    }

    [Fact]
    public void Renaming_a_failed_container_does_not_freeze_its_derived_state_after_child_reopens()
    {
        var root = PlanItemId.New(); var child = PlanItemId.New(); var plan = PlanId.New(); var run = RunId.New();
        var projection = PlanProjection.FromPayloads([
            new PlanCreated(plan, run, root, "Original"),
            new PlanItemAdded(child, plan, "Child", 1, root, [], true, []),
            new PlanItemStarted(child), new PlanItemFailed(child, "failure"),
            new PlanItemUpdated(root, "Corrected", null), new PlanItemReopened(child, "Retry")]);
        Assert.Equal("Corrected", projection.Item(root)!.Description);
        Assert.Equal(PlanItemState.Pending, projection.Item(root)!.State);
        Assert.Equal(PlanItemState.Ready, projection.Item(child)!.State);
    }
}
