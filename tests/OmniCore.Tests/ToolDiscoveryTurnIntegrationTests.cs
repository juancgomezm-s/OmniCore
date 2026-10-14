using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ToolDiscoveryTurnIntegrationTests
{
    [Fact]
    public void Explicit_integer_overflow_is_rejected_with_localized_wire_diagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var read = new ReadFileTool(new PathBoundaryValidator());
            var readCall = new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"), "read-overflow",
                "{\"path\":\"doc.txt\",\"offset\":2147483648}");
            var readRejected = Assert.IsType<PreparationRejected>(read.Prepare(readCall,
                new ToolPreparationContext(root, DateTimeOffset.UtcNow)));
            Assert.Equal(ToolErrorCode.InvalidArguments, readRejected.ErrorCode);
            Assert.Equal("tool.error.read_page_bounds(limit=200,offset=out-of-range)", readRejected.Reason);

            var catalog = new FakeCatalog();
            var search = new ToolSearchTool(catalog);
            var searchCall = new ValidatedToolCall(ToolCallId.New(), new ToolId("tool.search"), "search-overflow",
                "{\"query\":\"read tools\",\"maxResults\":2147483648}");
            var searchRejected = Assert.IsType<PreparationRejected>(search.Prepare(searchCall,
                new ToolPreparationContext(root, DateTimeOffset.UtcNow)));
            Assert.Equal(ToolErrorCode.InvalidArguments, searchRejected.ErrorCode);
            Assert.Equal("tool.error.search_bounds(maxResults=out-of-range,queryLength=10)", searchRejected.Reason);

            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                var localization = new Localization(locale);
                Assert.False(localization.ResolveDetailed("tool.error.read_page_bounds").UsedFallback);
                Assert.False(localization.ResolveDetailed("tool.error.search_bounds").UsedFallback);
                Assert.Contains("out-of-range", localization.Resolve("tool.error.read_page_bounds",
                    new Dictionary<string, string> { ["offset"] = "out-of-range", ["limit"] = "200" }));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Tool_search_bounds_candidate_descriptions_and_serialized_response()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-search-bounds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new FakeCatalog();
            for (var index = 0; index < 8; index++)
                catalog.Add(new SourcedReadTool("fixture.match" + index, TrustLevel.Trusted,
                    "read " + new string('x', 2000)));
            catalog.Add(new ToolSearchTool(catalog));
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                { ["tool.search"] = PermissionDecision.Allow }), root);
            var result = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                new ToolId("tool.search"), "search-bounded",
                "{\"query\":\"read\",\"maxResults\":8}"), false, CancellationToken.None);

            Assert.True(result.Succeeded, result.Summary);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Preview!) <= 8192);
            using var response = JsonDocument.Parse(result.Preview!);
            var matches = response.RootElement.GetProperty("matches").EnumerateArray().ToArray();
            Assert.Equal(8, matches.Length);
            Assert.All(matches, match => Assert.True(match.GetProperty("description").GetString()!.Length <= 161));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Discovered_tools_and_step_fingerprint_survive_user_ask_resume_without_rediscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-discovery-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var schema = new QuestionnaireSchema("Choose", null,
                [new QuestionField("choice", "Which?", null, QuestionKind.SingleChoice,
                    [new QuestionOption("safe", "Safe", null)], null, true, null, null, null)]);
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.git.read"))
                .Add(new UserAskTool());
            catalog.Add(new ToolSearchTool(catalog));
            var executor = ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy(
                new Dictionary<string, PermissionDecision>
                {
                    ["tool.search"] = PermissionDecision.Allow,
                    ["user.ask"] = PermissionDecision.Allow,
                }), root);
            var questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var requests = new List<ModelRequest>();
            var calls = 0;
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                calls++;
                if (calls == 1)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "discover", "tool.search",
                        "{\"query\":\"git read\",\"maxResults\":2}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                if (calls == 2)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "ask", "user.ask",
                        QuestionnaireCodec.EncodeSchema(schema))], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                Assert.Contains(request.Tools, tool => tool.Name == "fixture.git.read");
                return new ModelResponse([new TextBlock("completed")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questionnaires, recordEffectiveFingerprint: true);

            var suspended = turn.Ask("find tools and ask", "fixture system", session, run.RunId,
                run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.Equal(2, requests.Count);
            var originalStart = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            var modelSteps = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepStarted>().ToArray();
            Assert.Equal(2, modelSteps.Length);
            var discoveryPlanHash = originalStart.Fingerprint!.Hash();
            var discoveredSnapshotRef = Assert.IsType<ArtifactRef>(modelSteps[1].ContextSnapshotRef);
            using (var snapshot = System.Text.Json.JsonDocument.Parse(artifacts.GetText(discoveredSnapshotRef.Hash)!))
            {
                var stepFingerprint = snapshot.RootElement.GetProperty("fingerprint").GetString();
                Assert.NotEqual(discoveryPlanHash, stepFingerprint);
                var toolPlanHash = snapshot.RootElement.GetProperty("toolPlan").GetProperty("artifactHash").GetString()!;
                Assert.Equal("3", snapshot.RootElement.GetProperty("toolPlan").GetProperty("componentVersion").GetString());
                using var toolPlan = System.Text.Json.JsonDocument.Parse(artifacts.GetText(ParseHash(toolPlanHash))!);
                var persistedDefinitions = toolPlan.RootElement.GetProperty("tools").EnumerateArray()
                    .Select(entry => (Name: entry.GetProperty("visibleName").GetString(),
                        Description: entry.GetProperty("description").GetString(),
                        Schema: entry.GetProperty("inputSchemaJson").GetString())).ToArray();
                Assert.Contains(persistedDefinitions, entry => entry.Name == "fixture.git.read");
                Assert.Equal(requests[1].Tools.Select(tool => (tool.Name, tool.Description, tool.InputSchemaJson)),
                    persistedDefinitions.Select(entry => (entry.Name!, entry.Description!, entry.Schema!)));
            }

            var interaction = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<InteractionRequested>(), item => item.Kind == InteractionKind.Question);
            var resolution = questionnaires.Resolve(new EventStream(store, codecs, session), interaction.InteractionId,
                [new QuestionAnswer("choice", ["safe"], null, null)], false, null);
            Assert.True(resolution.Accepted, string.Join("; ", resolution.Errors?.Select(error => error.Detail) ?? []));

            var resumed = turn.Ask("continue", "fixture system", session, run.RunId, run.RootLane, "",
                CancellationToken.None);
            Assert.True(resumed.StopReason == StopReason.EndTurn,
                "Resume failed: " + resumed.StopReason + " / " + resumed.FailureDiagnostic + " / final=" + resumed.FinalText
                    + " / calls=" + calls + " requests=" + requests.Count + " / "
                    + string.Join(",", store.ReadFrom(session, 1).Select(codecs.Decode).TakeLast(14).Select(item => item.GetType().Name))
                    + " / " + string.Join("; ", resumed.ToolCalls.Select(call => call.Summary)));
            Assert.Equal(3, requests.Count);
            Assert.Contains(requests[2].Tools, tool => tool.Name == "fixture.git.read");
            Assert.DoesNotContain(requests[2].Messages.SelectMany(message => message.Content)
                .OfType<ToolResultBlock>(), result => result.Content.OfType<TextBlock>()
                .Any(text => text.Text.Contains("Cannot resume", StringComparison.Ordinal)));
            Assert.Equal(3, calls);
        }
        finally { Directory.Delete(root, true); }

        static ContentHash ParseHash(string text)
        {
            var separator = text.IndexOf(':');
            return new ContentHash(text[..separator], text[(separator + 1)..]);
        }
    }

    [Fact]
    public void Discovery_hides_untrusted_and_model_capability_rejected_candidates_from_result_and_plan()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-discovery-hidden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new FakeCatalog()
                .Add(FakeTool.Read("fixture.git.read"))
                .Add(FakeTool.Read("fixture.hidden.read"))
                .Add(new SourcedReadTool("ext.fixture.untrusted-read", TrustLevel.Untrusted));
            catalog.Add(new ToolSearchTool(catalog));
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var capabilities = ModelCapabilityBoundary.CoreTools.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
            capabilities["fixture.git.read"] = ModelToolCapability.WorkspaceRead;
            capabilities["fixture.hidden.read"] = ModelToolCapability.GeneralProcess;
            capabilities["ext.fixture.untrusted-read"] = ModelToolCapability.WorkspaceRead;
            var key = ModelPolicyKey.For("fixture", "fixture");
            var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 8,
                GuidanceLevel.Full, 1, PlanControl.Assisted, 4);
            var userPolicy = ModelPolicyPresets.ObserveOnly();
            var effective = EffectiveModelPolicy.Resolve(key,
                new StoredModelPolicy(key, 1, userPolicy, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), harness);
            var boundary = new ModelCapabilityBoundary(effective, capabilities);
            var requests = new List<ModelRequest>();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                { ["tool.search"] = PermissionDecision.Allow }), root, boundary);
            var providerCalls = 0;
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                providerCalls++;
                return providerCalls == 1
                    ? new ModelResponse([new ToolCallBlock(ToolCallId.New(), "search", "tool.search",
                        "{\"query\":\"fixture read\",\"maxResults\":8}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                harness, boundary);

            var result = turn.Ask("inspect fixture read tools", "fixture system", session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(2, requests.Count);
            Assert.Contains(requests[0].Tools, tool => tool.Name == "tool.search");
            Assert.Contains(requests[1].Tools, tool => tool.Name == "fixture.git.read");
            Assert.DoesNotContain(requests[1].Tools, tool => tool.Name is "fixture.hidden.read" or "ext.fixture.untrusted-read");
            var disclosedResults = requests[1].Messages.SelectMany(message => message.Content)
                .OfType<ToolResultBlock>().SelectMany(block => block.Content).OfType<TextBlock>()
                .Select(block => block.Text).ToArray();
            Assert.Single(disclosedResults);
            Assert.Contains("fixture.git.read", disclosedResults[0]);
            Assert.DoesNotContain("fixture.hidden.read", disclosedResults[0]);
            Assert.DoesNotContain("ext.fixture.untrusted-read", disclosedResults[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Discovered_mode_adds_matching_schema_to_the_next_model_request_and_executes_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tool-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.git.read"));
            catalog.Add(new ToolSearchTool(catalog));
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var artifacts = new FileArtifactStore(root);
            var baseline = new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build");
            var requests = new List<ModelRequest>();
            var providerCalls = 0;
            var executor = ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy(
                new Dictionary<string, PermissionDecision>
                {
                    ["tool.search"] = PermissionDecision.Allow,
                    ["fixture.git.read"] = PermissionDecision.Allow,
                }), root);
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                if (providerCalls++ == 0)
                    return new ModelResponse([
                        new ToolCallBlock(ToolCallId.New(), "search-call", "tool.search",
                            "{\"query\":\"git read\",\"maxResults\":2}"),
                        // A sibling call cannot use a tool disclosed only by the same response.
                        new ToolCallBlock(ToolCallId.New(), "forged-sibling", "fixture.git.read", "{}"),
                    ], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                if (providerCalls == 2)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "read-call", "fixture.git.read", "{}")],
                        StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "fixture", null));
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                baseline,
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 8, GuidanceLevel.Full, 1,
                    PlanControl.Assisted, 4), recordEffectiveFingerprint: true);

            var result = turn.Ask("inspect git history", "fixture system", session, run.RunId,
                run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(3, requests.Count);
            Assert.Contains(requests[0].Tools, tool => tool.Name == "tool.search");
            Assert.DoesNotContain(requests[0].Tools, tool => tool.Name == "fixture.git.read");
            var discovered = Assert.Single(requests[1].Tools, tool => tool.Name == "fixture.git.read");
            Assert.Contains("fixture.git.read", discovered.Name);
            Assert.False(string.IsNullOrWhiteSpace(discovered.InputSchemaJson));
            Assert.Contains(result.ToolCalls, call => call.ToolName == "fixture.git.read" && !call.Succeeded);
            Assert.Contains(result.ToolCalls, call => call.ToolName == "fixture.git.read" && call.Succeeded);

            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var started = Assert.Single(events.OfType<TurnStarted>());
            var steps = events.OfType<ModelStepStarted>().OrderBy(item => item.StepIndex).ToArray();
            Assert.Equal(3, steps.Length);
            Assert.Equal(started.Fingerprint!.Hash(), SnapshotFingerprint(steps[0].ContextSnapshotRef!));
            var discoveredFingerprint = SnapshotFingerprint(steps[1].ContextSnapshotRef!);
            Assert.NotEqual(started.Fingerprint.Hash(), discoveredFingerprint);
            Assert.Equal(discoveredFingerprint, SnapshotFingerprint(steps[2].ContextSnapshotRef!));
            var laneProfile = Assert.Single(events.OfType<LaneCreated>()).AgentProfile;
            var expected = RuntimeFingerprintFactory.WithTurnConfiguration(baseline, catalog, requests[1].Tools,
                "Contexto del workspace (fuentes del run):\nfixture system\nFingerprint: fixture · c", null,
                artifacts, laneProfile);
            Assert.Equal(expected.Hash(), discoveredFingerprint);

            string SnapshotFingerprint(ArtifactRef reference)
            {
                using var snapshot = JsonDocument.Parse(artifacts.GetText(reference.Hash)!);
                return snapshot.RootElement.GetProperty("fingerprint").GetString()!;
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class SourcedReadTool(string id, TrustLevel trust, string? description = null) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new(new ToolId(id), description ?? "fixture read tool",
            new InputSchema("{}"), [], true, false, ToolRisk.Low,
            new ComponentSource(SourceKind.Extension, ScopeLevel.User, trust, "fixture", "1"),
            ToolProtection.None, EffectClass.None);

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));

        public System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(new ToolResult("ok", "ok", null, 2,
                false, EffectOutcome.None));
    }
}
