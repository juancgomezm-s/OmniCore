using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Memory;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class SessionMemoryTurnIntegrationTests
{
    [Fact]
    public void Successful_model_step_updates_last_used_for_memory_in_its_effective_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-memory-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scope = MemoryScope.ForSession(SessionId.New());
        try
        {
            var memoryStore = new SessionMemoryStore(root);
            var record = new MemoryRecord(Guid.NewGuid().ToString("N"), scope, MemoryKind.Fact,
                "The local build command is dotnet build.", 0.7, 1.0, MemorySource.UserStated,
                DateTimeOffset.UtcNow, null, MemoryExpirationKind.Never, ["build"], ["fixture:evidence"], null);
            memoryStore.Add(record, CancellationToken.None);

            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var run = TestRun.Open(store, SessionId.Parse(scope.Id));
            var catalog = new FakeCatalog();
            ModelRequest? observed = null;
            var turn = new ExplorerTurn((request, _) =>
            {
                observed = request;
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(5, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), [new SessionMemoryContextContributor(memoryStore)]),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy());

            var result = turn.Ask("How do I build?", "system", SessionId.Parse(scope.Id), run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.NotNull(observed);
            Assert.Contains(record.Content, observed.Instructions, StringComparison.Ordinal);
            var persisted = Assert.Single(new SessionMemoryStore(root).Read(scope, CancellationToken.None));
            Assert.NotNull(persisted.LastUsedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
