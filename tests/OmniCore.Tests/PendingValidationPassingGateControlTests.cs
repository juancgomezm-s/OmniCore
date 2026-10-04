using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Control unitario sintético del caso complementario al de consumo: un gate Build/Test que
/// PASA consume las validaciones post-edición pendientes y permite el <c>RunCompleted</c>
/// limpio (sin <c>RunValidationRejected</c>). El consumo exitoso NO exime a las ediciones
/// posteriores: la segunda parte re-entra en el ledger con una mutación de un Turn nuevo y
/// verifica solo la vida del ledger — el Run ya está completado y no se vuelve a proponer su
/// finalización. El delegate devuelve evidencia sintética: no afirma nada sobre procesos
/// reales del host, durabilidad ni comportamientos del sistema operativo.
/// </summary>
public sealed class PendingValidationPassingGateControlTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    public void Passing_gate_consumes_pending_validation_completes_and_later_edits_revalidate(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-pending-passing-gate-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var ledger = new FileReadRegistry().Ledger;
            ledger.Bind(new FileMutationPolicy(FileMutationMode.PatchExisting,
                DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny,
                3, 200, 0.5, true, true, true, false));
            ledger.BeginTurn();
            ledger.RecordMutation("fixture.cs", 1, 2, ToolCallId.New());
            Assert.Single(ledger.PendingValidations());
            var events = store.ReadFrom(session, 1);
            var projection = RunProjection.Replay(session, run.RunId, codecs, events);
            var called = false;
            var completed = new RunCoupon(projection, TaskGraphProjection.Replay(codecs, events),
                PlanProjection.Replay(codecs, events)).CheckCompletionAndGate(new PlanService(),
                new ProgressReconciler(), store, codecs, session, new EventStream(store, codecs, session),
                runExternalGates: () =>
                {
                    called = true;
                    // Invoked after validation starts; this is not proof of a real host process.
                    var started = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<RunValidationStarted>());
                    Assert.Equal(run.RunId, started.RunId);
                    return new[] { new ExternalCompletionGateResult(key, true, "synthetic pass") };
                }, mutationLedger: ledger);
            Assert.True(called);
            Assert.True(completed);
            Assert.Empty(ledger.PendingValidations());
            var payloads = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var runCompleted = Assert.Single(payloads.OfType<RunCompleted>());
            Assert.Equal(run.RunId, runCompleted.RunId);
            Assert.DoesNotContain(payloads, evt => evt is RunValidationRejected);

            // El consumo exitoso no exime a las ediciones posteriores: una mutación de un Turn
            // nuevo vuelve a exigir su gate. Esto es SOLO vida del ledger en memoria — el Run de
            // arriba ya está completed y aquí no se propone otra finalización.
            ledger.BeginTurn();
            ledger.RecordMutation("later.cs", 0, 1, ToolCallId.New());
            var later = Assert.Single(ledger.PendingValidations());
            Assert.Equal("later.cs", later.Path);
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
