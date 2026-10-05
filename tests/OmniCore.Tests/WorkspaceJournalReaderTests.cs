using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class WorkspaceJournalReaderTests
{
    private static readonly EventType SelectedType = EventType.Of("test.workspace.selected");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadEvents_selects_exact_type_across_sessions_with_session_sequences(bool sqlite)
    {
        using var fixture = new JournalFixture(sqlite);
        var reader = Assert.IsAssignableFrom<IWorkspaceJournalReader>(fixture.Store);
        Assert.Empty(reader.ReadEvents(SelectedType));

        var first = SessionId.New();
        var second = SessionId.New();
        var unrelated = SessionId.New();
        var firstA = Event(first, SelectedType);
        var firstB = Event(first, SelectedType);
        var secondA = Event(second, SelectedType);
        var secondB = Event(second, SelectedType);
        Append(fixture.Store, firstA);
        Append(fixture.Store, Event(second, EventType.Of("test.workspace.selected.extra")));
        Append(fixture.Store, secondA);
        Append(fixture.Store, Event(first, EventType.Of("test.workspace.Selected")));
        Append(fixture.Store, Event(unrelated, EventType.Of("test.workspace.other")));
        Append(fixture.Store, firstB);
        Append(fixture.Store, secondB);

        var events = reader.ReadEvents(SelectedType);

        Assert.Equal(4, events.Count);
        Assert.All(events, evt => Assert.Equal(SelectedType, evt.Type));
        Assert.True(new[] { firstA.EventId, firstB.EventId, secondA.EventId, secondB.EventId }.ToHashSet()
            .SetEquals(events.Select(evt => evt.EventId)));
        Assert.Equal(new[] { firstA.EventId, firstB.EventId },
            events.Where(evt => evt.SessionId == first).Select(evt => evt.EventId));
        Assert.Equal(new long[] { 1, 3 },
            events.Where(evt => evt.SessionId == first).Select(evt => evt.Sequence));
        Assert.Equal(new[] { secondA.EventId, secondB.EventId },
            events.Where(evt => evt.SessionId == second).Select(evt => evt.EventId));
        Assert.Equal(new long[] { 2, 3 },
            events.Where(evt => evt.SessionId == second).Select(evt => evt.Sequence));
        Assert.DoesNotContain(events, evt => evt.SessionId == unrelated);
        Assert.Empty(reader.ReadEvents(EventType.Of("test.workspace.missing")));
        Assert.Equal(7, new[] { first, second, unrelated }.Sum(session => fixture.Store.ReadFrom(session, 1).Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadEvents_preserves_full_envelope_and_ordered_artifact_metadata(bool sqlite)
    {
        using var fixture = new JournalFixture(sqlite);
        var written = PopulateEnvelopes(fixture.Store);

        AssertEnvelopes(written, ((IWorkspaceJournalReader)fixture.Store).ReadEvents(SelectedType));
    }

    [Fact]
    public void Sqlite_ReadEvents_preserves_sessions_envelopes_and_artifacts_after_close_reopen()
    {
        using var fixture = new JournalFixture(true);
        var written = PopulateEnvelopes(fixture.Store);
        ((SqliteEventStore)fixture.Store).Close();
        var reopened = new SqliteEventStore(fixture.Path!);
        try
        {
            AssertEnvelopes(written, ((IWorkspaceJournalReader)reopened).ReadEvents(SelectedType));
            Assert.Empty(((IWorkspaceJournalReader)reopened).ReadEvents(EventType.Of("test.workspace.missing")));
        }
        finally
        {
            reopened.Close();
        }
    }

    private static DomainEvent[] PopulateEnvelopes(IEventStore store)
    {
        var first = SessionId.New();
        var second = SessionId.New();
        var refs = new[]
        {
            new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)),
                7, "text/plain; profile=a|b\\c", ArtifactKind.ToolOutput, Sensitivity.Normal, false),
            new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('b', 64)),
                9223372036854775806, "application/json", ArtifactKind.ProviderOpaqueState, Sensitivity.Sensitive, true),
        };
        var full = DomainEvent.Stored(EventId.New(), first, 0, SelectedType, 7,
            new DateTimeOffset(2026, 10, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567),
            new CommandCausation(CommandId.New()), RunId.New(), RunId.New(), TaskId.New(),
            LaneId.New(), TurnId.New(), PlanItemId.New(), ToolCallId.New(), refs,
            "{\"text\":\"metadata | \\u00f1\",\"value\":123}");
        var empty = Event(second, SelectedType);
        var caused = DomainEvent.Stored(EventId.New(), first, 0, SelectedType, 2,
            full.Timestamp.AddMinutes(1), new EventCausation(full.EventId), null, null, null,
            null, null, null, null, Array.Empty<ArtifactRef>(), "{\"next\":true}");
        Append(store, Event(second, EventType.Of("test.workspace.other")));
        Append(store, full);
        Append(store, empty);
        Append(store, Event(first, EventType.Of("test.workspace.selected.extra")));
        Append(store, caused);

        // Expected sequences come from the append contract, independently of either read path.
        return new[] { WithSequence(full, 1), WithSequence(empty, 2), WithSequence(caused, 3) };
    }

    private static DomainEvent WithSequence(DomainEvent evt, long sequence) =>
        DomainEvent.Stored(evt.EventId, evt.SessionId, sequence, evt.Type, evt.SchemaVersion, evt.Timestamp,
            evt.Causation, evt.CorrelationId, evt.RunId, evt.TaskId, evt.LaneId, evt.TurnId,
            evt.PlanItemId, evt.ToolCallId, evt.ArtifactRefs, evt.PayloadJson);

    private static void AssertEnvelopes(IReadOnlyList<DomainEvent> expected, IReadOnlyList<DomainEvent> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var evt in expected)
        {
            var read = Assert.Single(actual, candidate => candidate.EventId == evt.EventId);
            Assert.Equal(evt.EventId, read.EventId);
            Assert.Equal(evt.SessionId, read.SessionId);
            Assert.Equal(evt.Sequence, read.Sequence);
            Assert.Equal(evt.Type, read.Type);
            Assert.Equal(evt.SchemaVersion, read.SchemaVersion);
            Assert.Equal(evt.Timestamp, read.Timestamp);
            Assert.Equal(evt.Causation, read.Causation);
            Assert.Equal(evt.CorrelationId, read.CorrelationId);
            Assert.Equal(evt.RunId, read.RunId);
            Assert.Equal(evt.TaskId, read.TaskId);
            Assert.Equal(evt.LaneId, read.LaneId);
            Assert.Equal(evt.TurnId, read.TurnId);
            Assert.Equal(evt.PlanItemId, read.PlanItemId);
            Assert.Equal(evt.ToolCallId, read.ToolCallId);
            Assert.Equal(evt.PayloadJson, read.PayloadJson);
            Assert.Equal(evt.ArtifactRefs.Count, read.ArtifactRefs.Count);
            for (var i = 0; i < evt.ArtifactRefs.Count; i++)
            {
                var reference = evt.ArtifactRefs[i];
                var restored = read.ArtifactRefs[i];
                Assert.Equal(reference.Id, restored.Id);
                Assert.Equal(reference.Hash.Algorithm, restored.Hash.Algorithm);
                Assert.Equal(reference.Hash.Value, restored.Hash.Value);
                Assert.Equal(reference.Size, restored.Size);
                Assert.Equal(reference.MediaType, restored.MediaType);
                Assert.Equal(reference.Kind, restored.Kind);
                Assert.Equal(reference.Sensitivity, restored.Sensitivity);
                Assert.Equal(reference.Redacted, restored.Redacted);
            }
        }
    }

    private static DomainEvent Event(SessionId session, EventType type) =>
        DomainEvent.Create(session, type, 1, null, null, null, null, null, null, null, null,
            Array.Empty<ArtifactRef>(), "{}");

    private static void Append(IEventStore store, DomainEvent evt) =>
        store.Append(evt.SessionId, evt, DurabilityClass.Standard, CancellationToken.None);

    private sealed class JournalFixture : IDisposable
    {
        public IEventStore Store { get; }
        public string? Path { get; }

        public JournalFixture(bool sqlite)
        {
            if (sqlite)
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "omnicore-workspace-reader-" + Guid.NewGuid().ToString("N") + ".db");
                Store = new SqliteEventStore(Path);
            }
            else
            {
                Store = new InMemoryEventStore();
            }
        }

        public void Dispose()
        {
            if (Store is SqliteEventStore sqlite)
            {
                sqlite.Close();
                // Close returns the connection to its pool; release this fixture's pool before deleting.
                SqliteConnection.ClearPool((SqliteConnection)sqlite.Connection);
                File.Delete(Path!);
                File.Delete(Path + "-wal");
                File.Delete(Path + "-shm");
            }
        }
    }
}
