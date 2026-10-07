// Proposed failure-path regression suite for MetaModelService artifact publication.
// The Host success-path/lease flags are covered by ExplorerContextCheckpointPublicationLeaseTests.
// These tests use the real SQLite journal, FileArtifactStore, and spend ledger, with an offline
// provider and a sink implementing the same short, synchronous prepared-artifact publication seam.
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class MetaArtifactPublicationFailureTests
{
    [Fact]
    public async Task Failed_started_publication_releases_only_the_undispatched_reservation()
    {
        using var fixture = new Fixture(FailurePoint.Started);
        var provider = new ScriptedProvider(ProviderResult.Complete);
        var reservation = fixture.CreateReservationCallbacks();
        var service = fixture.CreateService(provider, reservation);

        await Assert.ThrowsAsync<IOException>(() => service.SummarizeAsync(fixture.Run.RunId,
            "CompressContext", "private old context", 200, CancellationToken.None));

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, reservation.Dispatches);
        Assert.Equal(1, reservation.Releases);
        Assert.Equal(0, reservation.Receipts);
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationStarted>());
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationNotDispatched>());
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationFailed>());
        var input = Assert.Single(fixture.Sink.Published, p => p.Kind == ArtifactKind.Other);
        Assert.True(fixture.Artifacts.Verify(input.Hash, input.Size)); // unreferenced until GC

        fixture.CloseJournal();
        var sweep = fixture.SweepZeroGrace();
        Assert.True(sweep.Deleted >= 1);
        Assert.False(fixture.Artifacts.Verify(input.Hash, input.Size));
        fixture.ReopenJournal();

        Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
            fixture.Ledger.TryReserve("after-release", 0.601m,
                () => [new("daily", "fixture-subject", 1m, 0m)]));
    }

    [Fact]
    public async Task Failed_completed_append_keeps_reported_usage_in_failed_event_and_settles_once()
    {
        using var fixture = new Fixture(FailurePoint.Completed);
        var provider = new ScriptedProvider(ProviderResult.Complete);
        var reservation = fixture.CreateReservationCallbacks();
        var service = fixture.CreateService(provider, reservation);

        await Assert.ThrowsAsync<IOException>(() => service.SummarizeAsync(fixture.Run.RunId,
            "CompressContext", "private old context", 200, CancellationToken.None));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, reservation.Dispatches);
        Assert.Equal(0, reservation.Releases);
        Assert.Equal(1, reservation.Receipts);
        Assert.Equal(1, reservation.ObservedSends);
        var started = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationStarted>());
        var failed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationFailed>());
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(started.InvocationId, failed.InvocationId);
        Assert.Equal(ScriptedProvider.Usage, failed.Usage);
        Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, failed.ReportedUsageFields);
        Assert.Equal(0.10m, failed.CostUsd);
        Assert.True(fixture.Artifacts.Verify(started.InputArtifact.Hash, started.InputArtifact.Size));

        var output = Assert.Single(fixture.Sink.Published, p => p.Kind == ArtifactKind.ModelResponse);
        Assert.True(fixture.Artifacts.Verify(output.Hash, output.Size)); // published, but no completion ref
        fixture.CloseJournal();
        var sweep = fixture.SweepZeroGrace();
        Assert.True(sweep.Deleted >= 1);
        Assert.True(fixture.Artifacts.Verify(started.InputArtifact.Hash, started.InputArtifact.Size));
        Assert.False(fixture.Artifacts.Verify(output.Hash, output.Size));
        fixture.ReopenJournal();
        var reopenedFailure = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationFailed>());
        Assert.Equal(ScriptedProvider.Usage, reopenedFailure.Usage);
        Assert.Equal(0.10m, reopenedFailure.CostUsd);

        // Re-reading the same immutable receipt must not resurrect the bound or double-settle it.
        Assert.True(fixture.Ledger.ReconcileCanonicalReceipt(reservation.InvocationId!, 0.401m, 0.10m,
            "meta-invocation:" + reservation.InvocationId, fullyAccounted: true));
        Assert.True(fixture.Ledger.ReconcileCanonicalReceipt(reservation.InvocationId!, 0.401m, 0.10m,
            "meta-invocation:" + reservation.InvocationId, fullyAccounted: true));

        Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
            fixture.Ledger.TryReserve("after-settlement", 0.60m,
                () => [new("daily", "fixture-subject", 1m, 0.10m)]));
    }

    [Fact]
    public async Task Cancellation_after_started_event_but_before_dispatch_appends_not_dispatched_then_releases()
    {
        using var fixture = new Fixture(FailurePoint.None, cancelAfterStarted: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Sink.CancelAfterStarted = cancellation;
        var provider = new ScriptedProvider(ProviderResult.Complete);
        var reservation = fixture.CreateReservationCallbacks();
        var service = fixture.CreateService(provider, reservation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SummarizeAsync(fixture.Run.RunId,
            "CompressContext", "private old context", 200, cancellation.Token));

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, reservation.Dispatches);
        Assert.Equal(1, reservation.Releases);
        Assert.Equal(0, reservation.Receipts);
        var started = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationStarted>());
        var notDispatched = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationNotDispatched>());
        Assert.Equal(started.InvocationId, notDispatched.InvocationId);
        Assert.True(fixture.Artifacts.Verify(started.InputArtifact.Hash, started.InputArtifact.Size));
        Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
            fixture.Ledger.TryReserve("after-cancel", 0.601m,
                () => [new("daily", "fixture-subject", 1m, 0m)]));
    }

    [Fact]
    public async Task Provider_failure_after_observed_send_keeps_unknown_dispatched_bound()
    {
        using var fixture = new Fixture(FailurePoint.None);
        var provider = new ScriptedProvider(ProviderResult.ThrowAfterSend);
        var reservation = fixture.CreateReservationCallbacks();
        var service = fixture.CreateService(provider, reservation);

        await Assert.ThrowsAsync<IOException>(() => service.SummarizeAsync(fixture.Run.RunId,
            "CompressContext", "private old context", 200, CancellationToken.None));

        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, reservation.Dispatches);
        Assert.Equal(0, reservation.Releases);
        Assert.Equal(1, reservation.Receipts);
        Assert.Null(reservation.LastCost);
        Assert.Equal(1, reservation.ObservedSends);
        var failed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationFailed>());
        Assert.Null(failed.Usage);
        Assert.Equal(TokenUsageFields.None, failed.ReportedUsageFields);
        Assert.Null(failed.CostUsd);

        // A failed response with no usage is not free: admission still sees the original dispatched bound.
        Assert.True(fixture.Ledger.HasFullDispatchedBound(reservation.InvocationId!, 0.401m));
        Assert.Equal(SqliteSpendReservationStore.Admission.Insufficient,
            fixture.Ledger.TryReserve("must-remain-blocked", 0.20m,
                () => [new("daily", "fixture-subject", 0.50m, 0m)]));
    }

    private enum FailurePoint { None, Started, Completed }
    private enum ProviderResult { Complete, ThrowAfterSend }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _journal;
        private SqliteEventStore? _store;
        private readonly EventCodecs _codecs = EventCodecs.Create();

        public Fixture(FailurePoint failure, bool cancelAfterStarted = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "omni-meta-publication-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _journal = Path.Combine(_root, "journal.db");
            _store = new SqliteEventStore(_journal);
            Artifacts = new FileArtifactStore(_root);
            var session = SessionId.New();
            Run = TestRun.Open(_store, session, mode: RunMode.Plan);
            Ledger = new SqliteSpendReservationStore(Path.Combine(_root, "reservations.db"));
            Sink = new DurableMetaSink(new EventStream(_store, _codecs, session), Artifacts,
                failure, cancelAfterStarted);
        }

        public FileArtifactStore Artifacts { get; }
        public SqliteSpendReservationStore Ledger { get; }
        public DurableMetaSink Sink { get; }
        public TestRun.Opened Run { get; }

        public DomainEventPayload[] Payloads() => _store!.ReadFrom(Run.SessionId, 1)
            .Select(_codecs.Decode).ToArray();

        public ReservationCallbacks CreateReservationCallbacks() => new(Ledger);

        public MetaModelService CreateService(IModelProvider provider, ReservationCallbacks callbacks) =>
            new(provider, Artifacts, Sink,
                new ModelSelection(new ModelIdValue("fixture-meta"), 4096, ToolMode.Direct, null),
                costEstimator: _ => 0.10m,
                reserve: callbacks.Reserve,
                dispatch: callbacks.Dispatch,
                afterReceipt: callbacks.AfterReceipt,
                releaseBeforeDispatch: callbacks.Release);

        public void CloseJournal()
        {
            _store?.Close();
            _store = null;
        }

        public void ReopenJournal() => _store = new SqliteEventStore(_journal);

        public ArtifactGc.SweepResult SweepZeroGrace() => new ArtifactGc(_root).Sweep(_journal,
            TimeSpan.Zero, dryRun: false, DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);

        public void Dispose()
        {
            CloseJournal();
            using var connection = new SqliteConnection("DataSource=" + _journal);
            SqliteConnection.ClearPool(connection); // only this fixture's exact journal
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ReservationCallbacks(SqliteSpendReservationStore ledger)
    {
        public int Dispatches { get; private set; }
        public int Releases { get; private set; }
        public int Receipts { get; private set; }
        public long ObservedSends { get; private set; }
        public decimal? LastCost { get; private set; }
        public string? InvocationId { get; private set; }

        public bool Reserve(string id)
        {
            InvocationId = id;
            return ledger.TryReserve(id, 0.401m, () =>
                [new("daily", "fixture-subject", 1m, 0m)]) == SqliteSpendReservationStore.Admission.Reserved;
        }

        public void Dispatch(string id)
        {
            Dispatches++;
            ledger.MarkDispatched(id);
        }

        public void Release(string id)
        {
            Releases++;
            ledger.ReleaseBeforeDispatch(id);
        }

        public void AfterReceipt(string id, decimal? cost, long sends)
        {
            Receipts++;
            LastCost = cost;
            ObservedSends = sends;
            if (cost is { } known)
                ledger.ReconcileCanonicalReceipt(id, 0.401m, known,
                    "meta-invocation:" + id, fullyAccounted: true);
        }
    }

    private sealed class DurableMetaSink(EventStream stream, FileArtifactStore artifacts,
        FailurePoint failure, bool cancelAfterStarted = false) : IContextArtifactPublicationSink
    {
        public List<ArtifactRef> Published { get; } = new();
        public CancellationTokenSource? CancelAfterStarted { get; set; }

        public void AppendPreparedArtifact(IPreparedArtifact artifact, DomainEventPayload payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = ((IArtifactPublicationLease)artifacts).AcquirePublicationLease(CancellationToken.None);
            var reference = artifact.Publish();
            Assert.Equal(artifact.Reference, reference);
            Published.Add(reference);
            if (failure == FailurePoint.Started && payload is MetaModelInvocationStarted
                || failure == FailurePoint.Completed && payload is MetaModelInvocationCompleted)
                throw new IOException("Fixture rejected the meta event before journal append.");
            stream.Append(payload, DurabilityClass.Barrier);
            if (cancelAfterStarted && payload is MetaModelInvocationStarted)
                CancelAfterStarted?.Cancel();
        }

        public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Append(payload, DurabilityClass.Barrier);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedProvider(ProviderResult result) : IModelProvider
    {
        public static TokenUsage Usage { get; } = new(50_000, 50_000, 0, 0, 0);
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public int Calls { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            GenerationRequestAttemptScope.RecordGenerationSend(); // one scripted generation send; not a billing receipt
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (result == ProviderResult.ThrowAfterSend) throw new IOException("scripted provider failure after send");
            yield return new ResponseCompleted(new ModelResponse(
                new ContentBlock[] { new TextBlock("older facts preserved") }, StopReason.EndTurn, Usage, null,
                new ProviderMetadata("scripted-meta", "fixture", null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }
}
