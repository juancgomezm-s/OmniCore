using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Xunit;

namespace OmniCore.Tests;

/// <summary>
/// Prueba focal para M5.5 Phase A Bug #2: simetría de persistencia de ArtifactRefs
/// en SqliteEventStore. Verifica que al cerrar y reabrir el store, los refs se
/// restauran exactos (igualdad, orden) y que una ausencia sigue siendo ausencia.
/// </summary>
public sealed class SqliteEventStoreArtifactRefsTests
{
    private static string TempJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-sqlite-artifactrefs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
    }

    private static ArtifactRef MakeRef(
        string hashSuffix,
        long size = 123,
        string mediaType = "application/json",
        ArtifactKind kind = ArtifactKind.ModelResponse,
        Sensitivity sensitivity = Sensitivity.Normal,
        bool redacted = false) =>
        new(
            ArtifactId.New(),
            ContentHash.Sha256(hashSuffix.PadLeft(64, 'a')),
            size,
            mediaType,
            kind,
            sensitivity,
            redacted);

    private static DomainEvent EventWithRefs(SessionId sessionId, string type, params ArtifactRef[] refs) =>
        DomainEvent.Create(sessionId, EventType.Of(type), 1, null, null, null, null, null, null,
            null, null, refs, "{}");

    [Fact]
    public void ArtifactRefs_round_trip_exact_equality_and_order()
    {
        var journal = TempJournal();
        var session = SessionId.New();

        // Crear refs con todos los campos variados para verificar igualdad exacta
        var ref1 = MakeRef("1", 100, "text/plain", ArtifactKind.ToolOutput, Sensitivity.Normal, false);
        var ref2 = MakeRef("2", 200, "application/json", ArtifactKind.ModelResponse, Sensitivity.Sensitive, true);
        var ref3 = MakeRef("3", 300, "application/octet-stream", ArtifactKind.ProcessOutput, Sensitivity.Normal, false);
        var ref4 = MakeRef("4", 400, "text/markdown", ArtifactKind.Patch, Sensitivity.Sensitive, true);
        var refs = new[] { ref1, ref2, ref3, ref4 };

        // Escribir eventos con refs
        var store = new SqliteEventStore(journal);
        try
        {
            store.Append(session, EventWithRefs(session, "test.event.with.refs", refs), DurabilityClass.Standard, CancellationToken.None);
            store.Append(session, EventWithRefs(session, "test.event.empty", Array.Empty<ArtifactRef>()), DurabilityClass.Standard, CancellationToken.None);
        }
        finally
        {
            store.Close();
        }

        // Reabrir store y leer
        var reopened = new SqliteEventStore(journal);
        try
        {
            var events = reopened.ReadFrom(session, 1);

            Assert.Equal(2, events.Count);

            // Primer evento: verificar igualdad exacta y orden de los 4 refs
            var evtWithRefs = events[0];
            Assert.Equal(4, evtWithRefs.ArtifactRefs.Count);

            for (int i = 0; i < 4; i++)
            {
                var expected = refs[i];
                var actual = evtWithRefs.ArtifactRefs[i];

                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.Hash, actual.Hash);
                Assert.Equal(expected.Size, actual.Size);
                Assert.Equal(expected.MediaType, actual.MediaType);
                Assert.Equal(expected.Kind, actual.Kind);
                Assert.Equal(expected.Sensitivity, actual.Sensitivity);
                Assert.Equal(expected.Redacted, actual.Redacted);
            }

            // Segundo evento: ausencia de refs sigue siendo ausencia (array vacío, no null)
            var evtEmpty = events[1];
            Assert.Empty(evtEmpty.ArtifactRefs);
            Assert.NotNull(evtEmpty.ArtifactRefs);
        }
        finally
        {
            reopened.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void ArtifactRefs_round_trip_single_ref_all_fields()
    {
        var journal = TempJournal();
        var session = SessionId.New();

        var ref1 = MakeRef("deadbeef", 999, "custom/type", ArtifactKind.Other, Sensitivity.Sensitive, true);
        var refs = new[] { ref1 };

        var store = new SqliteEventStore(journal);
        try
        {
            store.Append(session, EventWithRefs(session, "test.single", refs), DurabilityClass.Barrier, CancellationToken.None);
        }
        finally
        {
            store.Close();
        }

        var reopened = new SqliteEventStore(journal);
        try
        {
            var events = reopened.ReadFrom(session, 1);
            Assert.Single(events);

            var actual = events[0].ArtifactRefs[0];
            Assert.Equal(ref1.Id, actual.Id);
            Assert.Equal(ref1.Hash, actual.Hash);
            Assert.Equal(ref1.Size, actual.Size);
            Assert.Equal(ref1.MediaType, actual.MediaType);
            Assert.Equal(ref1.Kind, actual.Kind);
            Assert.Equal(ref1.Sensitivity, actual.Sensitivity);
            Assert.Equal(ref1.Redacted, actual.Redacted);
        }
        finally
        {
            reopened.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void ArtifactRefs_round_trip_media_type_with_escaped_delimiters_and_backslash()
    {
        var journal = TempJournal();
        var session = SessionId.New();
        const string mediaType = "text/plain; profile=foo|bar\\baz";
        var expected = MakeRef("escaped", 69, mediaType, ArtifactKind.ToolOutput, Sensitivity.Normal, false);

        var store = new SqliteEventStore(journal);
        try
        {
            store.Append(session, EventWithRefs(session, "test.escaped.media-type", expected), DurabilityClass.Standard, CancellationToken.None);
        }
        finally
        {
            store.Close();
        }

        var reopened = new SqliteEventStore(journal);
        try
        {
            var events = reopened.ReadFrom(session, 1);
            Assert.Single(events);
            Assert.Single(events[0].ArtifactRefs);
            AssertEqualRef(expected, events[0].ArtifactRefs[0]);
        }
        finally
        {
            reopened.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void ArtifactRefs_round_trip_multiple_events_preserves_each()
    {
        var journal = TempJournal();
        var session = SessionId.New();

        var refsA = new[] { MakeRef("aaa", 10, "type/a", ArtifactKind.ToolOutput, Sensitivity.Normal, false) };
        var refsB = new[] { MakeRef("bbb", 20, "type/b", ArtifactKind.Patch, Sensitivity.Sensitive, true), MakeRef("ccc", 30, "type/c", ArtifactKind.ContextSnapshot, Sensitivity.Normal, false) };
        var refsC = Array.Empty<ArtifactRef>();

        var store = new SqliteEventStore(journal);
        try
        {
            store.AppendBatch(session, new[]
            {
                EventWithRefs(session, "test.a", refsA),
                EventWithRefs(session, "test.b", refsB),
                EventWithRefs(session, "test.c", refsC),
            }, DurabilityClass.Standard, CancellationToken.None);
        }
        finally
        {
            store.Close();
        }

        var reopened = new SqliteEventStore(journal);
        try
        {
            var events = reopened.ReadFrom(session, 1);
            Assert.Equal(3, events.Count);

            // Evento A: 1 ref
            Assert.Single(events[0].ArtifactRefs);
            AssertEqualRef(refsA[0], events[0].ArtifactRefs[0]);

            // Evento B: 2 refs en orden
            Assert.Equal(2, events[1].ArtifactRefs.Count);
            AssertEqualRef(refsB[0], events[1].ArtifactRefs[0]);
            AssertEqualRef(refsB[1], events[1].ArtifactRefs[1]);

            // Evento C: ausencia
            Assert.Empty(events[2].ArtifactRefs);
        }
        finally
        {
            reopened.Close();
            TryDelete(journal);
        }
    }

    [Fact]
    public void ArtifactRefs_read_empty_string_returns_empty_array()
    {
        // Prueba directa del helper Parts.ParseArtifacts
        var result = Parts.ParseArtifacts(null);
        Assert.Empty(result);
        Assert.NotNull(result);

        result = Parts.ParseArtifacts("");
        Assert.Empty(result);
        Assert.NotNull(result);
    }

    [Fact]
    public void ArtifactRefs_read_persisted_three_field_format_uses_only_neutral_missing_metadata()
    {
        var id = ArtifactId.New();
        var hash = ContentHash.Sha256(new string('b', 64));

        var result = Parts.ParseArtifacts($"{id}|{hash.Algorithm}|{hash.Value}");

        var actual = Assert.Single(result);
        Assert.Equal(id, actual.Id);
        Assert.Equal(hash, actual.Hash);
        Assert.Equal(0, actual.Size);
        Assert.Equal(string.Empty, actual.MediaType);
        Assert.Equal(ArtifactKind.Other, actual.Kind);
        Assert.Equal(Sensitivity.Normal, actual.Sensitivity);
        Assert.False(actual.Redacted);
    }

    private static void AssertEqualRef(ArtifactRef expected, ArtifactRef actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Hash, actual.Hash);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.MediaType, actual.MediaType);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Sensitivity, actual.Sensitivity);
        Assert.Equal(expected.Redacted, actual.Redacted);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
