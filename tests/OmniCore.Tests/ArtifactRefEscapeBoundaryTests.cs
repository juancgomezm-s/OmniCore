using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Xunit;

namespace OmniCore.Tests;

/// <summary>
/// M55 regresión de límites de escape en la persistencia de ArtifactRefs de
/// SqliteEventStore. Matriz pequeña y determinista de MediaTypes sintéticos
/// con backslashes adyacentes, pipes, punto-y-comas, delimitadores repetidos
/// y backslash final. Cada evento persiste DOS refs para que el separador de
/// refs (';') también quede en el límite. Prueba de almacenamiento de
/// cadenas: no afirma validez MIME de estos valores.
/// </summary>
public sealed class ArtifactRefEscapeBoundaryTests
{
    // Valores sintéticos (runtime): backslashes literales adyacentes,
    // delimitadores repetidos y backslash final.
    public static IEnumerable<object[]> BoundaryMediaTypes => new[]
    {
        new object[] { @"text\\plain" },          // backslashes adyacentes
        new object[] { @"a|b;c\\d" },              // pipe + punto-y-coma + backslashes adyacentes
        new object[] { @"x;;y||z" },              // delimitadores repetidos
        new object[] { @"trailing\" },             // backslash final
        new object[] { @"\\|\\;;\\" },             // backslashes adyacentes a delimitadores repetidos y final
        new object[] { @"\;|\\" },                 // backslash pegado a ambos delimitadores
    };

    private static (string Dir, string Journal) TempJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-artifactref-escape-boundary",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (dir, Path.Combine(dir, "journal.db"));
    }

    private static ArtifactRef MakeRef(
        string hashSuffix, long size, string mediaType,
        ArtifactKind kind, Sensitivity sensitivity, bool redacted) =>
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

    [Theory]
    [MemberData(nameof(BoundaryMediaTypes))]
    public void Append_close_reopen_restores_two_refs_with_boundary_media_types(string mediaType)
    {
        var (dir, journal) = TempJournal();
        var session = SessionId.New();
        var ct = TestContext.Current.CancellationToken;

        // DOS refs por evento: el separador de refs (';') también queda en el límite.
        var refA = MakeRef("aa", 111, mediaType, ArtifactKind.ToolOutput, Sensitivity.Normal, false);
        var refB = MakeRef("bb", 222, mediaType, ArtifactKind.Patch, Sensitivity.Sensitive, true);
        Assert.NotEqual(refA.Id, refB.Id);

        var store = new SqliteEventStore(journal);
        try
        {
            store.Append(session, EventWithRefs(session, "test.escape.boundary", refA, refB),
                DurabilityClass.Standard, ct);
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

            var restored = events[0].ArtifactRefs;
            Assert.NotNull(restored);
            Assert.Equal(2, restored.Count);

            // Orden e igualdad exacta de ambos refs, todos los campos.
            AssertEqualRef(refA, restored[0]);
            AssertEqualRef(refB, restored[1]);
        }
        finally
        {
            reopened.Close();
            TryDelete(dir, journal);
        }
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

    private static void TryDelete(string dir, string journal)
    {
        // Libera únicamente el pool de ESTE journal; Close devuelve la conexión al pool.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + journal);
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        // El directorio es exclusivo de este caso (Guid); incluye sidecars SQLite si quedan.
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
