using System.Text.Json;
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
    public void Host_model_request_invokes_memory_propose_through_tool_runtime_and_auto_promotes()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-propose-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var run = TestRun.Open(store, session);
            IReadOnlyList<DomainEventPayload> Journal() => store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            void Emit(DomainEventPayload payload) => new EventStream(store, codecs, session).Append(payload,
                DurabilityClass.Barrier);
            var memoryStore = new SessionMemoryStore(root);
            var artifacts = new FileArtifactStore(root);
            var hostTools = OmniHost.CreateExplorerTools();
            hostTools.RegisterMemoryProposeTool(new MemoryProposeTool(new SessionMemoryPolicy(session,
                memoryStore, Journal, Emit)));
            var requests = new List<ModelRequest>(); var providerCalls = 0;
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                if (providerCalls++ == 0)
                    return new ModelResponse([
                        new ToolCallBlock(ToolCallId.New(), "propose-memory", "memory.propose",
                            "{\"content\":\"Run dotnet test for focused checks.\",\"kind\":\"Convention\"}"),
                        new ToolCallBlock(ToolCallId.New(), "propose-sensitive-memory", "memory.propose",
                            "{\"content\":\"Contact alice@example.com for the deployment.\",\"kind\":\"Fact\"}")],
                        StopReason.ToolUse, new TokenUsage(7, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null));
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(4, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                    { ["memory.propose"] = PermissionDecision.Allow }), root), hostTools.Catalog(),
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), store, codecs,
                artifacts, new InMemoryAuditSink(), new RedactionPolicy());

            var result = turn.Ask("remember the test command", "system", session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.True(result.StopReason == StopReason.EndTurn, result.FinalText ?? "stop=" + result.StopReason);
            Assert.Contains(requests[0].Tools, tool => tool.Name == "memory.propose");
            Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode), item => item is MemoryCandidateProposed);
            Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode), item => item is MemoryPromoted);
            Assert.Contains(memoryStore.Read(MemoryScope.ForSession(session), CancellationToken.None),
                item => item.Content == "Run dotnet test for focused checks.");
            var memoryEvents = store.ReadFrom(session, 1).Where(item => codecs.Decode(item) is
                MemoryCandidateProposed or MemoryPromoted);
            Assert.DoesNotContain(memoryEvents,
                item => item.PayloadJson.Contains("alice@example.com", StringComparison.Ordinal));
            var memoryRequests = store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ToolCallRequested>().Where(item => item.ToolName == "memory.propose").ToArray();
            Assert.Equal(2, memoryRequests.Length);
            Assert.All(memoryRequests, request => Assert.Equal("{\"screenedByMemoryPolicy\":true}",
                request.ArgumentsJson));
            Assert.DoesNotContain(memoryStore.Read(MemoryScope.ForSession(session), CancellationToken.None),
                item => item.Content.Contains("alice@example.com", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Runtime_observes_repeated_successful_dotnet_builds_and_materializes_only_evidence_based_heuristic()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-memory-runtime-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New(); var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        try
        {
            var run = TestRun.Open(store, session);
            IReadOnlyList<DomainEventPayload> Journal() => store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            void Emit(DomainEventPayload payload) => new EventStream(store, codecs, session).Append(payload,
                DurabilityClass.Barrier);
            var memoryStore = new SessionMemoryStore(root);
            var policy = new SessionMemoryPolicy(session, memoryStore, Journal, Emit);
            var catalog = new FakeCatalog().Add(FakeTool.Read("process.exec"));
            var requests = new List<ModelRequest>(); var providerCalls = 0;
            const string arguments = "{\"executable\":\"dotnet\",\"argv\":[\"build\"],\"cwd\":\".\",\"timeoutSeconds\":30}";
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                if (providerCalls++ < 3)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "build-" + providerCalls,
                        "process.exec", arguments)], StopReason.ToolUse, new TokenUsage(5, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null));
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(3, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                    { ["process.exec"] = PermissionDecision.Allow }), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), [new SessionMemoryContextContributor(memoryStore)]),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), store, codecs,
                new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                sessionMemoryPolicy: policy);

            var result = turn.Ask("Run the build checks", "system", session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(4, requests.Count);
            Assert.DoesNotContain("Repeated successful .NET build or test validation observed in this Session.", requests[3].Instructions ?? "", StringComparison.Ordinal);
            Assert.Contains(requests[3].Messages.SelectMany(message => message.Content).OfType<TextBlock>(),
                block => block.Text.Contains("Repeated successful .NET build or test validation observed in this Session.", StringComparison.Ordinal));
            var heuristic = Assert.Single(Journal().OfType<MemoryCandidateProposed>(),
                item => item.Origin == MemoryCandidateOrigin.Heuristic);
            Assert.Equal(3, heuristic.EvidenceRefs.Count);
            Assert.DoesNotContain(Journal(), item => item is MemoryCandidateProposed
                { Origin: MemoryCandidateOrigin.Model, Content: var content }
                && content.Contains("Repeated successful", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
            Assert.DoesNotContain(record.Content, observed.Instructions ?? "", StringComparison.Ordinal);
            Assert.Contains(observed.Messages.SelectMany(message => message.Content).OfType<TextBlock>(), block => block.Text.Contains(record.Content, StringComparison.Ordinal));
            var persisted = Assert.Single(new SessionMemoryStore(root).Read(scope, CancellationToken.None));
            Assert.NotNull(persisted.LastUsedAt);
            var memoryUsed = store.ReadFrom(SessionId.Parse(scope.Id), 1).Select(codecs.Decode)
                .OfType<MemoryUsed>().Single();
            Assert.Contains(record.Id, memoryUsed.MemoryIds);
            Assert.Equal(0, memoryUsed.ModelStepIndex);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Memory_omitted_by_context_budget_and_pre_send_provider_failure_never_records_used()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-memory-not-used-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var scope = MemoryScope.ForSession(SessionId.New());
        try
        {
            var memoryStore = new SessionMemoryStore(root);
            var record = new MemoryRecord(Guid.NewGuid().ToString("N"), scope, MemoryKind.Fact,
                "A deliberately lengthy memory about the local release validation procedure.", 0.7, 0.2,
                MemorySource.Promoted, DateTimeOffset.UtcNow, null, MemoryExpirationKind.Never,
                ["release"], [], null);
            memoryStore.Add(record, CancellationToken.None);
            var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
            var session = SessionId.Parse(scope.Id); var run = TestRun.Open(store, session);
            var calls = 0;
            var catalog = new FakeCatalog();
            var turn = new ExplorerTurn((request, _) =>
            {
                calls++;
                Assert.DoesNotContain(record.Content, request.Instructions ?? "", StringComparison.Ordinal);
                throw new InvalidOperationException("fixture fails before provider send");
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), [new SessionMemoryContextContributor(memoryStore)]),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 7, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy());

            var result = turn.Ask("question", "system prompt", session, run.RunId, run.RootLane, "",
                CancellationToken.None);

            Assert.Equal(StopReason.ContextOverflow, result.StopReason);
            Assert.Equal(0, calls);
            var payloads = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var snapshot = Assert.Single(payloads.OfType<TurnStarted>()).ContextSnapshotRef!;
            using (var context = JsonDocument.Parse(new FileArtifactStore(root).GetText(snapshot.Hash)!))
                Assert.Contains(context.RootElement.GetProperty("diagnostics").EnumerateArray(), item =>
                    item.GetProperty("itemId").GetString() == "memory." + record.Id
                    && item.GetProperty("decision").GetString() == ContextDecision.OmittedByBudget.ToString());
            Assert.DoesNotContain(payloads, item => item is MemoryUsed);
            Assert.Null(Assert.Single(memoryStore.Read(scope, CancellationToken.None)).LastUsedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
