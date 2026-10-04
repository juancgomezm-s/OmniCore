using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Xunit;

namespace OmniCore.Tests;

/// <summary>
/// Pruebas M55 de validación del token redacted al parsear ArtifactRefs en SqliteEventStore.
/// El escritor emite exactamente "1"/"0" (ver Parts.Artifacts); el parser debe aceptar solo esos
/// tokens canónicos y rechazar cualquier otro valor (p. ej. "true", "2", "") en vez de mapearlo
/// en silencio a false, lo que ocultaría corrupción del journal.
///
/// Cada prueba persiste un ref válido por la API pública, cierra el store, manipula
/// sintéticamente SOLO el campo redacted de la columna events.artifacts del journal SQLite
/// temporal con SQL parametrizado, y reabre el store para leer por ReadFrom. No se usa ningún
/// proveedor real: es corrupción cruda sintética de la base de datos de prueba.
/// </summary>
public sealed class ArtifactRefRedactionValidationTests
{
    private static string TempJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-artifact-redaction-validation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "journal.db");
    }

    private static DomainEvent EventWithRefs(SessionId sessionId, string type, params ArtifactRef[] refs) =>
        DomainEvent.Create(sessionId, EventType.Of(type), 1, null, null, null, null, null, null,
            null, null, refs, "{}");

    /// <summary>Append de un ref canónico válido (Sensitivity.Normal|Sensitive son los únicos valores legales).</summary>
    private static ArtifactRef AppendValidRef(string journal, SessionId session, bool redacted)
    {
        var reference = new ArtifactRef(
            ArtifactId.New(),
            ContentHash.Sha256("cafe".PadLeft(64, 'a')),
            777,
            "application/json",
            ArtifactKind.ModelResponse,
            Sensitivity.Sensitive,
            redacted);

        var store = new SqliteEventStore(journal);
        try
        {
            store.Append(session, EventWithRefs(session, "test.redaction.validation", reference),
                DurabilityClass.Standard, CancellationToken.None);
        }
        finally
        {
            store.Close();
        }

        return reference;
    }

    /// <summary>
    /// Sustituye sintéticamente SOLO el token redacted —el campo tras el último '|' de
    /// events.artifacts— por <paramref name="token"/>, con SQL parametrizado. El resto del ref
    /// codificado queda intacto.
    /// </summary>
    private static void TamperRedactedToken(string journal, string token)
    {
        using var conn = new SqliteConnection("DataSource=" + journal);
        conn.Open();

        long rowId;
        string artifacts;
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT id, artifacts FROM events ORDER BY id";
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("El journal de prueba no contiene eventos");
            }

            rowId = reader.GetInt64(0);
            artifacts = reader.GetString(1);
        }

        var lastSeparator = artifacts.LastIndexOf('|');
        if (lastSeparator < 0 || (artifacts[artifacts.Length - 1] != '0' && artifacts[artifacts.Length - 1] != '1'))
        {
            throw new InvalidOperationException("Formato canónico inesperado en artifacts: " + artifacts);
        }

        var tampered = artifacts.Substring(0, lastSeparator + 1) + token;

        using (var update = conn.CreateCommand())
        {
            update.CommandText = "UPDATE events SET artifacts = :art WHERE id = :id";
            var pArt = update.CreateParameter();
            pArt.ParameterName = "art";
            pArt.Value = tampered;
            update.Parameters.Add(pArt);
            var pId = update.CreateParameter();
            pId.ParameterName = "id";
            pId.Value = rowId;
            update.Parameters.Add(pId);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
    }

    /// <summary>Reabre el journal y lee por ReadFrom; el ref de 8 campos se decodifica íntegro salvo redacted.</summary>
    private static void AssertRefDecoded(string journal, SessionId session, ArtifactRef appended, bool expectedRedacted)
    {
        var store = new SqliteEventStore(journal);
        try
        {
            var events = store.ReadFrom(session, 1);
            Assert.Single(events);

            var refs = events[0].ArtifactRefs;
            Assert.Single(refs);
            var actual = refs[0];

            Assert.Equal(appended.Id, actual.Id);
            Assert.Equal(appended.Hash, actual.Hash);
            Assert.Equal(appended.Size, actual.Size);
            Assert.Equal(appended.MediaType, actual.MediaType);
            Assert.Equal(appended.Kind, actual.Kind);
            Assert.Equal(appended.Sensitivity, actual.Sensitivity);
            Assert.Equal(expectedRedacted, actual.Redacted);
        }
        finally
        {
            store.Close();
        }
    }

    private static void AssertReadFromRejectsJournal(string journal, SessionId session)
    {
        var store = new SqliteEventStore(journal);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => store.ReadFrom(session, 1));
            Assert.Contains("artifact ref", ex.Message);
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public void Redacted_canonical_zero_token_decodes_false()
    {
        var journal = TempJournal();
        var session = SessionId.New();
        // El escritor persiste "1"; se manipula a "0" para verificar que el token canónico "0"
        // se decodifica como false desde la base, no solo por round-trip del escritor.
        var appended = AppendValidRef(journal, session, redacted: true);
        try
        {
            TamperRedactedToken(journal, "0");
            AssertRefDecoded(journal, session, appended, expectedRedacted: false);
        }
        finally
        {
            Cleanup(journal);
        }
    }

    [Fact]
    public void Redacted_canonical_one_token_decodes_true()
    {
        var journal = TempJournal();
        var session = SessionId.New();
        // El escritor persiste "0"; se manipula a "1" para verificar la decodificación de "1" a true.
        var appended = AppendValidRef(journal, session, redacted: false);
        try
        {
            TamperRedactedToken(journal, "1");
            AssertRefDecoded(journal, session, appended, expectedRedacted: true);
        }
        finally
        {
            Cleanup(journal);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("2")]
    [InlineData("")]
    public void Redacted_invalid_tokens_are_rejected_on_read(string token)
    {
        var journal = TempJournal();
        var session = SessionId.New();
        AppendValidRef(journal, session, redacted: false);
        try
        {
            TamperRedactedToken(journal, token);
            AssertReadFromRejectsJournal(journal, session);
        }
        finally
        {
            Cleanup(journal);
        }
    }

    private static void Cleanup(string journal)
    {
        try
        {
            // Libera las conexiones agrupadas de ESTE journal para poder borrar sus archivos.
            using var conn = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(conn);
        }
        catch
        {
            // La limpieza es best-effort: el journal vive en un directorio temporal aislado.
        }

        TryDelete(journal);
        TryDelete(journal + "-wal");
        TryDelete(journal + "-shm");
        try { Directory.Delete(Path.GetDirectoryName(journal)!, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
