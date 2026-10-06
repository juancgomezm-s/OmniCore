namespace OmniCore.Tests;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>Offline cost/provider fixtures; real SQLite close/reopen and immutable CAS evidence.</summary>
public sealed class MetaModelDailySpendIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Other_workspace_meta_spend_uses_its_own_cas_and_utc_day(bool yesterday, bool missingEvidence)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-user-meta-daily-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "workspaces", "source");
        var current = Path.Combine(root, "workspaces", "current");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(current);
        var sourceJournal = Path.Combine(source, "journal.db");
        var currentJournal = Path.Combine(current, "journal.db");
        var codecs = EventCodecs.Create();
        var sourceStore = new SqliteEventStore(sourceJournal);
        var currentStore = new SqliteEventStore(currentJournal);
        try
        {
            var sourceRun = TestRun.Open(sourceStore, SessionId.New());
            var sourceArtifacts = new FileArtifactStore(source);
            var timestamp = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            AppendMeta(sourceStore, codecs, sourceArtifacts, sourceRun, "other-workspace-charge",
                yesterday ? timestamp.AddDays(-1) : timestamp);
            sourceStore.Close();
            if (missingEvidence)
                foreach (var file in Directory.GetFiles(Path.Combine(source, "blobs"), "*", SearchOption.AllDirectories))
                    File.Delete(file); // Only this fixture's CAS; a missing receipt must not become zero spend.
            var requester = TestRun.Open(currentStore, SessionId.New());
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("fixture done") }, StopReason.EndTurn,
                    new TokenUsage(0, 0, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture-model", null), TokenUsageFields.All);
            }, new NoTools(), new FakeCatalog(),
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture-model", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
                currentStore, codecs, new FileArtifactStore(current), new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                sessionCapUsd: 100m, dailyCapUsd: 0.20m,
                userSpendReader: new UserWorkspaceSpendReader(root, current));
            var result = turn.Ask("continue", "system", requester.SessionId, requester.RunId,
                requester.RootLane, "", CancellationToken.None);
            var allowed = yesterday && !missingEvidence;
            Assert.Equal(allowed ? 1 : 0, calls);
            Assert.Equal(allowed ? StopReason.EndTurn : StopReason.Cancelled, result.StopReason);
            var events = currentStore.ReadFrom(requester.SessionId, 1).Select(codecs.Decode).ToArray();
            if (allowed) Assert.Single(events.OfType<ModelStepCompleted>());
            else
            {
                Assert.Contains(events, evt => evt is InteractionRequested request
                    && request.Kind == InteractionKind.BudgetExceeded);
                Assert.DoesNotContain(events, evt => evt is ModelStepStarted or ModelStepCompleted);
            }
        }
        finally
        {
            sourceStore.Close();
            currentStore.Close();
            using var sourceConnection = new SqliteConnection("DataSource=" + sourceJournal);
            using var currentConnection = new SqliteConnection("DataSource=" + currentJournal);
            SqliteConnection.ClearPool(sourceConnection);
            SqliteConnection.ClearPool(currentConnection);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Daily_meta_spend_survives_reopen_and_invocation_identity_is_session_scoped(
        bool firstChargeYesterday, bool sharedInvocationId)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-meta-daily-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
        var codecs = EventCodecs.Create();
        SqliteEventStore? store = new(journal);
        try
        {
            var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var first = TestRun.Open(store, SessionId.New());
            var second = TestRun.Open(store, SessionId.New());
            var requester = TestRun.Open(store, SessionId.New());
            AppendMeta(store, codecs, artifacts, first, "same-invocation",
                firstChargeYesterday ? today.AddDays(-1) : today);
            AppendMeta(store, codecs, artifacts, second,
                sharedInvocationId ? "same-invocation" : "different-invocation", today);
            var beforeFirst = store.ReadFrom(first.SessionId, 1).ToArray();
            var beforeSecond = store.ReadFrom(second.SessionId, 1).ToArray();
            var requesterSequence = store.CurrentSequence(requester.SessionId);
            store.Close();
            store = new SqliteEventStore(journal);

            var meta = new NeverInvokedMeta();
            var primaryCalls = 0;
            var catalog = new FakeCatalog();
            var turn = new ExplorerTurn((_, _) =>
            {
                primaryCalls++;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(0, 0, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "fixture-model", null), TokenUsageFields.All);
            }, new NoTools(), catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("fixture-model", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                sessionCapUsd: 100m, dailyCapUsd: 0.50m, metaModelProvider: meta);

            var result = turn.Ask("continue", "system", requester.SessionId, requester.RunId,
                requester.RootLane, "", CancellationToken.None);

            Assert.Equal(firstChargeYesterday ? StopReason.EndTurn : StopReason.Cancelled, result.StopReason);
            Assert.Equal(firstChargeYesterday ? 1 : 0, primaryCalls);
            Assert.Equal(0, meta.Calls);
            var appended = store.ReadFrom(requester.SessionId, requesterSequence + 1).ToArray();
            Assert.All(appended, evt => Assert.Equal(requester.SessionId, evt.SessionId));
            if (firstChargeYesterday)
            {
                Assert.DoesNotContain(appended.Select(codecs.Decode).OfType<InteractionRequested>(),
                    request => request.Kind == InteractionKind.BudgetExceeded);
                Assert.Single(appended.Select(codecs.Decode).OfType<ModelStepCompleted>());
            }
            else
            {
                var requestEvent = Assert.Single(appended, evt => codecs.Decode(evt) is InteractionRequested request
                    && request.Kind == InteractionKind.BudgetExceeded);
                Assert.Equal(requester.RunId, requestEvent.RunId);
                Assert.Equal(requester.RootTask, requestEvent.TaskId);
                Assert.Equal(requester.RootLane, requestEvent.LaneId);
                Assert.DoesNotContain(appended.Select(codecs.Decode), payload => payload is
                    MetaModelInvocationStarted or ModelStepStarted or ToolCallRequested);
            }

            // Compare every durable envelope field, payload and artifact reference, not object identity.
            Assert.Equal(beforeFirst.Select(evt => JsonSerializer.Serialize(evt)),
                store.ReadFrom(first.SessionId, 1).Select(evt => JsonSerializer.Serialize(evt)));
            Assert.Equal(beforeSecond.Select(evt => JsonSerializer.Serialize(evt)),
                store.ReadFrom(second.SessionId, 1).Select(evt => JsonSerializer.Serialize(evt)));
            foreach (var evt in beforeFirst.Concat(beforeSecond))
                foreach (var reference in evt.ArtifactRefs)
                    Assert.True(artifacts.Verify(reference.Hash, reference.Size));
            Assert.Equal(2, beforeFirst.Concat(beforeSecond).Select(codecs.Decode)
                .OfType<MetaModelInvocationCompleted>().Count());
            Assert.All(beforeFirst.Concat(beforeSecond).Select(codecs.Decode)
                .OfType<MetaModelInvocationCompleted>(), completion =>
                {
                    Assert.Equal(0.30m, completion.CostUsd);
                    Assert.Equal(new TokenUsage(150_000, 150_000, 0, 0, 0), completion.Usage);
                    Assert.Equal(TokenUsageFields.All, completion.ReportedUsageFields);
                });
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, true);
        }
    }

    private static void AppendMeta(SqliteEventStore store, IEventCodecRegistry codecs,
        IArtifactStore artifacts, TestRun.Opened run, string invocationId, DateTimeOffset timestamp)
    {
        var input = artifacts.PutText("fixture input", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        var output = artifacts.PutText("fixture output", "text/plain", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        DomainEventPayload[] payloads =
        [
            new MetaModelInvocationStarted(invocationId, run.RunId, "CompressContext", "fixture-meta", input),
            new MetaModelInvocationCompleted(invocationId, run.RunId, "CompressContext", "fixture-meta", output,
                new TokenUsage(150_000, 150_000, 0, 0, 0), 0.30m, TokenUsageFields.All),
        ];
        var sequence = store.CurrentSequence(run.SessionId);
        // Raw store is only used to inject explicit historical UTC timestamps; no production clock changes.
        var envelopes = payloads.Select((payload, index) => DomainEvent.Stored(EventId.New(), run.SessionId,
            sequence + index + 1, payload.Type(), payload.SchemaVersion(), timestamp, null, run.RunId,
            run.RunId, run.RootTask, run.RootLane, null, null, null,
            new[] { index == 0 ? input : output }, codecs.CodecFor(payload.Type()).Encode(payload),
            source: "fixture-meta-writer")).ToArray();
        store.AppendBatch(run.SessionId, envelopes, DurabilityClass.Barrier, CancellationToken.None);
    }

    private sealed class NeverInvokedMeta : IModelProvider
    {
        public int Calls { get; private set; }
        public ProviderCapabilities Capabilities => ProviderCapabilities.Local();
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken token)
        {
            Calls++;
            throw new InvalidOperationException("No compaction should occur in this small-context fixture.");
        }
    }

    private sealed class NoTools : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall call, bool approve, CancellationToken token,
            EventStream stream) => throw new InvalidOperationException("Fixture must not execute tools.");
    }
}
