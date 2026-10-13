using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ExplorerTurnGenerationDeadlineTests
{
    [Fact]
    public void Configured_generation_deadline_cancels_provider_and_is_reported_explicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-generation-deadline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(Path.Combine(root, "journal.db"));
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var turn = new ExplorerTurn((_, token) =>
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maxGenerationSeconds: 1);

            var result = turn.Ask("test", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Contains("cancelled generation after 1 seconds", result.FailureDiagnostic, StringComparison.Ordinal);
            Assert.Contains("provider may continue", result.FailureDiagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Single(store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt)).OfType<TurnInterrupted>());
            Assert.Empty(store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt)).OfType<ModelStepCompleted>());
        }
        finally
        {
            store?.Close();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
