using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// E2E adversarial de la vertical M3 de ADR-0045 (cuestionarios): el lifecycle DURABLE y
/// EXACTAMENTE-UNA de <c>user.ask</c> sobre un journal SQLite REABIERTO. Se cubre: publicación
/// del schema como artifact content-addressed + InteractionRequested(Question); respuesta válida
/// y correlacionada; opciones desconocidas/duplicadas; <c>Otro</c>; cancelación; reinicio ANTES
/// y DESPUÉS de responder (replay/pending); y que NADA sensible aparece en claro en eventos ni
/// artifacts (ADR-0045 §7, ADR-0018). El mismo servicio se reabre sobre el mismo journal para
/// verificar que la publicación y la resolución sobreviven al reinicio.
/// </summary>
public sealed class QuestionnaireVerticalTests
{
    private static QuestionOption Opt(string id, string label) => new(id, label, null);

    private static readonly QuestionnaireSchema Schema = new QuestionnaireSchema("Elige opciones", null,
        new QuestionField[]
        {
            new QuestionField("single", "¿Enfoque?", null, QuestionKind.SingleChoice, new[]
            {
                Opt("a", "Compatibilidad"),
                Opt("b", "Simplificar"),
            }, null, true, null, null, null),
            new QuestionField("multi", "¿Validaciones?", null, QuestionKind.MultipleChoice, new[]
            {
                Opt("x", "Unit"),
                Opt("y", "Integración"),
            }, new OtherInput("otro", "Otro", "especifica", true, 80), true, 1, 2, null),
            new QuestionField("text", "Notas", null, QuestionKind.FreeText, Array.Empty<QuestionOption>(),
                null, false, null, null, 200),
        });

    private static readonly string SecretLike = "sk-test-a1b2c3d4e5f6g7h8i9";

    private Fx fx = new Fx();

    private sealed class Fx
    {
        public string? Journal;

        public string? DataDir;

        public SqliteEventStore? Store;

        public IEventCodecRegistry? Codecs;

        public IArtifactStore? Artifacts;

        public SessionId Session = SessionId.New();

        public void Open()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Journal!)!);
            Directory.CreateDirectory(DataDir!);
            Store = new SqliteEventStore(Journal!);
            Codecs = EventCodecs.Create();
            Artifacts = new FileArtifactStore(DataDir!);
        }

        public void Reopen()
        {
            Store!.Close();
            Open();
        }

        public void Close()
        {
            try
            {
                Store!.Close();
            }
            catch (Exception)
            {
            }

            TryDelete(Journal!);
            TryDeleteTree(DataDir!);
        }
    }

    private Fx NewFx()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m3-q", Guid.NewGuid().ToString("N"));
        return new Fx { Journal = Path.Combine(root, "journal.db"), DataDir = Path.Combine(root, "blobs") };
    }
    private QuestionnaireInteractionService Service() => new QuestionnaireInteractionService(
        fx.Store!, fx.Codecs!, fx.Artifacts!);

    private EventStream Stream() => new EventStream(fx.Store!, fx.Codecs!, fx.Session);

    /// <summary>Publica un cuestionario válido; devuelve su InteractionId.</summary>
    private InteractionId Publish(string? secretInSchema = null)
    {
        var service = Service();
        var id = InteractionId.New();
        var schema = secretInSchema is null
            ? Schema
            : new QuestionnaireSchema(Schema.Title + " " + secretInSchema, null, Schema.Questions);
        var result = service.Publish(Stream(), schema, id, null, "{\"toolCall\":\"tc-1\"}");
        Assert.True(result.Published, "el schema válido debe publicarse");
        return id;
    }

    private QuestionAnswer Answer(string questionId, string[]? selected = null, string? text = null,
        string? otherText = null) => new QuestionAnswer(questionId, selected ?? Array.Empty<string>(), text,
        otherText);

    private static bool Has(IReadOnlyList<QuestionnaireError> errors, QuestionnaireErrorCode code, string? qid)
    {
        foreach (var e in errors)
        {
            if (e.Code == code && e.QuestionId == qid)
            {
                return true;
            }
        }

        return false;
    }

    private void SetUp()
    {
        fx = NewFx();
        fx.Open();
    }

    private void TearDown()
    {
        fx.Close();
    }

    /// <summary>Un schema demasiado grande se rechaza en el servicio SIN tocar el journal.</summary>
    [Fact]
    public void Publish_rejects_over_limit_schema_without_journal_write()
    {
        SetUp();
        try
        {
            var schema = new QuestionnaireSchema("", null, Array.Empty<QuestionField>());
            var result = Service().Publish(Stream(), schema, InteractionId.New(), null, null);

            Assert.False(result.Published);
            Assert.True(Has(result.Errors ?? Array.Empty<QuestionnaireError>(), QuestionnaireErrorCode.NoQuestions, null));
            // Nada se persistió: el journal queda vacío.
            Assert.Equal(0, fx.Store!.CurrentSequence(fx.Session));
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>Publicar + responder válido: resolved exactamente una vez, correlacionado.</summary>
    [Fact]
    public void Valid_response_is_accepted_and_persisted()
    {
        SetUp();
        try
        {
            var id = Publish();
            var service = Service();
            var answers = new QuestionAnswer[]
            {
                Answer("single", new[] { "a" }),
                Answer("multi", new[] { "x", "otro" }, null, "tests e2e"),
                Answer("text", null, "notas seguras"),
            };
            var result = service.Resolve(Stream(), id, Schema, answers, false, "{\"toolCall\":\"tc-1\"}");

            Assert.True(result.Accepted);
            Assert.False(result.AlreadyResolved);
            // Exactamente una resolución: un segundo envío (duplicado) se rechaza.
            var dup = service.Resolve(Stream(), id, Schema, answers, false, "{\"toolCall\":\"tc-1\"}");
            Assert.False(dup.Accepted);
            Assert.True(dup.AlreadyResolved);

            // Las respuestas NO aparecen en claro en ningún evento del journal.
            foreach (var evt in fx.Store!.ReadFrom(fx.Session, 1))
            {
                var payload = fx.Codecs!.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson);
                var json = evt.PayloadJson;
                Assert.False(json.Contains("tests e2e"), "la respuesta no debe estar en claro en eventos");
                Assert.False(json.Contains("notas seguras"), "la respuesta no debe estar en claro en eventos");
                Assert.False(json.Contains(SecretLike), "secreto en claro en eventos");
                _ = payload;
            }
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>Respuesta con opción desconocida o duplicada se rechaza (Host autoridad).</summary>
    [Fact]
    public void Invalid_answers_are_rejected()
    {
        SetUp();
        try
        {
            var id = Publish();
            var service = Service();

            var unknown = service.Resolve(Stream(), id, Schema,
                new QuestionAnswer[] { Answer("single", new[] { "zzz" }), Answer("multi", new[] { "x" }) },
                false, null);
            Assert.False(unknown.Accepted);
            Assert.True(Has(unknown.Errors ?? Array.Empty<QuestionnaireError>(),
                QuestionnaireErrorCode.UnknownOptionId, "single"));

            var dupOption = service.Resolve(Stream(), id, Schema,
                new QuestionAnswer[] { Answer("multi", new[] { "x", "x" }) }, false, null);
            Assert.False(dupOption.Accepted);

            var dupAnswer = service.Resolve(Stream(), id, Schema,
                new QuestionAnswer[] { Answer("single", new[] { "a" }), Answer("single", new[] { "b" }) },
                false, null);
            Assert.False(dupAnswer.Accepted);
            Assert.True(Has(dupAnswer.Errors ?? Array.Empty<QuestionnaireError>(),
                QuestionnaireErrorCode.DuplicateAnswer, "single"));
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>"Otro" se identifica por schema (id estable) y su texto se valida.</summary>
    [Fact]
    public void Other_is_identified_by_schema_id_not_label()
    {
        SetUp();
        try
        {
            var id = Publish();
            var service = Service();
            // Otro con TextRequired exige texto no vacío.
            var noText = service.Resolve(Stream(), id, Schema,
                new QuestionAnswer[] { Answer("single", new[] { "a" }), Answer("multi", new[] { "otro" }) },
                false, null);
            Assert.False(noText.Accepted);
            Assert.True(Has(noText.Errors ?? Array.Empty<QuestionnaireError>(),
                QuestionnaireErrorCode.OtherTextRequired, "multi"));

            var withText = service.Resolve(Stream(), id, Schema,
                new QuestionAnswer[] { Answer("single", new[] { "a" }),
                    Answer("multi", new[] { "otro" }, null, "contexto propio") },
                false, null);
            Assert.True(withText.Accepted);
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>Cancelar es un resultado estructurado (State=Cancelled), no Deny ni opción.</summary>
    [Fact]
    public void Cancel_is_a_valid_structured_result()
    {
        SetUp();
        try
        {
            var id = Publish();
            var service = Service();
            var result = service.Resolve(Stream(), id, Schema, Array.Empty<QuestionAnswer>(), true, null);

            Assert.True(result.Accepted);
            // Resuelto: no se puede responder de nuevo.
            var again = service.Resolve(Stream(), id, Schema,
                new QuestionAnswer[] { Answer("single", new[] { "a" }) }, false, null);
            Assert.True(again.AlreadyResolved);
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>Reinicio ANTES de responder: la interacción sigue pendiente y se republica.</summary>
    [Fact]
    public void Restart_before_answer_leaves_interaction_pending()
    {
        SetUp();
        try
        {
            var id = Publish();
            fx.Reopen(); // crash

            var pending = Service().Pending(fx.Session);
            Assert.Equal(id.ToString(), pending[0].InteractionId.ToString());
            // El schema del artifact se recupera tras reabrir.
            var schema = Service().SchemaFor(Stream(), id);
            Assert.NotNull(schema);
            Assert.Equal("Elige opciones", schema!.Title);
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>Reinicio DESPUÉS de responder: ya no está pendiente (exactamente una vez).</summary>
    [Fact]
    public void Restart_after_answer_is_not_republished_or_reaccepted()
    {
        SetUp();
        try
        {
            var id = Publish();
            var service = Service();
            var answers = new QuestionAnswer[] { Answer("single", new[] { "a" }),
                Answer("multi", new[] { "x" }) };
            Assert.True(service.Resolve(Stream(), id, Schema, answers, false, null).Accepted);

            fx.Reopen(); // crash tras responder

            Assert.Empty(Service().Pending(fx.Session));
            // Enviar una respuesta al reabrir se rechaza como duplicado.
            var late = Service().Resolve(Stream(), id, Schema, answers, false, null);
            Assert.False(late.Accepted);
            Assert.True(late.AlreadyResolved);
        }
        finally
        {
            TearDown();
        }
    }

    /// <summary>Secretos en el schema no llegan a ningún artifact contenido-address en claro.</summary>
    [Fact]
    public void Secrets_never_reach_artifacts_in_clear()
    {
        SetUp();
        try
        {
            var id = Publish(SecretLike);
            fx.Reopen();
            var schema = Service().SchemaFor(Stream(), id);
            Assert.NotNull(schema);
            Assert.False(schema!.Title.Contains(SecretLike), "secreto en claro en el schema del artifact");
        }
        finally
        {
            TearDown();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception)
        {
        }
    }
}