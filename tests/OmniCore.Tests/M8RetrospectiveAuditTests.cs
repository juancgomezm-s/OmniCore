using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Memory;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class M8RetrospectiveAuditTests
{
    [Fact]
    public void Official_MCP_discovery_supportedVersions_reaches_catalog()
    {
        using var fx = new Fixture();
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "data"));
        var file = Path.Combine(paths.ConfigDirectory, "mcp", "servers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "[{\"id\":\"audit\",\"transport\":\"http\",\"endpoint\":\"http://127.0.0.1:54321/mcp\",\"allowedHosts\":[\"127.0.0.1:54321\"]}]");
        var store = new McpServerStore(paths, fx.Root, false);
        store.Trust("audit", true);
        var wire = new OfficialMcp();
        Assert.Single(store.DiscoverTools(wire, wire, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completion_terminal_hook_runs_with_or_without_acceptance(bool acceptance)
    {
        using var fx = new Fixture();
        var hook = new Hook(LifecyclePoint.RunCompleted, new HookContinue());
        var turn = fx.Turn([hook]);
        var exit = fx.Runtime.RunActLoop(turn, _ => { }, "audit", "instructions", fx.Session,
            fx.Run, fx.Lane, "", new WorkspaceGatesYaml { Acceptance = acceptance }, null,
            fx.Server, fx.Artifacts, new InMemoryAuditSink(), null, true, "en", CancellationToken.None,
            acceptanceResponder: _ => "accept");
        Assert.Equal(0, exit);
        Assert.Contains(fx.Payloads, item => item is RunCompleted);
        Assert.Equal(1, hook.Calls);
    }

    [Fact]
    public void Failed_validator_does_not_gain_a_second_effect_from_AfterValidation()
    {
        using var fx = new Fixture();
        var rpc = new ChangingValidator();
        var manifest = new ExtensionManifest { Id = "audit", Version = "1", Runtime =
            new ExtensionRuntimeManifest { Kind = "process", Command = "runtime.exe", Args = [] } };
        var extension = new DiscoveredExtension(fx.Root, manifest, ExtensionDiscoveryStatus.Ready,
            null, new string('a', 64), new string('b', 64), TrustLevel.Project);
        var validator = new ExtensionCompletionValidator(extension, ["check"], fx.Root,
            fx.Session, fx.Run, rpc);
        var skills = new SkillCompletionValidator([], [validator]);
        var hook = new Hook(LifecyclePoint.AfterValidation,
            new HookRequireValidation("extension.audit.check", "audit"));
        var exit = fx.Runtime.RunActLoop(fx.Turn([hook]), _ => { }, "audit", "instructions", fx.Session,
            fx.Run, fx.Lane, "", null, null, fx.Server, fx.Artifacts, new InMemoryAuditSink(),
            null, false, "en", CancellationToken.None, skillValidators: skills);
        Assert.DoesNotContain(fx.Payloads, item => item is RunCompleted);
        Assert.NotEqual(0, exit);
        Assert.Equal(1, rpc.Calls);
    }

    [Fact]
    public void Explicit_user_can_add_a_previously_forgotten_memory_again()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => fx.Payloads,
            payload => stream.Append(payload, DurabilityClass.Barrier));
        const string content = "Use the compact display for this session.";
        policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.User);
        var scope = MemoryScope.ForSession(fx.Session);
        var record = Assert.Single(memory.Read(scope, CancellationToken.None));
        Assert.True(policy.Forget(record.Id, TestContext.Current.CancellationToken));
        Assert.Equal("forgotten", policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.Model).Decision);
        Assert.Empty(memory.Read(scope, CancellationToken.None));
        var restored = policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.User);
        Assert.NotEqual(record.Id, "mem-" + restored.Id);
        Assert.Single(memory.Read(scope, CancellationToken.None));
        var reopenedStore = new SessionMemoryStore(fx.Root);
        var reopened = new SessionMemoryPolicy(fx.Session, reopenedStore, () => fx.Payloads,
            payload => new EventStream(fx.Store, fx.Codecs, fx.Session).Append(payload, DurabilityClass.Barrier));
        Assert.Equal(restored.Id, reopened.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.User).Id);
        Assert.Single(reopenedStore.Read(scope, CancellationToken.None));
    }

    [Fact]
    public void User_can_readd_model_origin_memory_after_forget_without_model_resurrection()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => fx.Payloads,
            payload => stream.Append(payload, DurabilityClass.Barrier));
        const string content = "Keep the compact validation summary.";
        var original = policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.Model);
        var oldRecord = Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        Assert.True(policy.Forget(oldRecord.Id, TestContext.Current.CancellationToken));

        Assert.Equal("forgotten", policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.Heuristic).Decision);
        Assert.Empty(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        var readded = policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.User);
        Assert.NotEqual(original.Id, readded.Id);
        Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        var reopened = new SessionMemoryPolicy(fx.Session, new SessionMemoryStore(fx.Root), () => fx.Payloads,
            payload => new EventStream(fx.Store, fx.Codecs, fx.Session).Append(payload, DurabilityClass.Barrier));
        Assert.Equal(readded.Id, reopened.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.User).Id);
        Assert.Single(new SessionMemoryStore(fx.Root).Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
    }

    [Fact]
    public void Failed_forget_receipt_restores_the_record_without_a_durable_tombstone()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => fx.Payloads,
            payload => stream.Append(payload, DurabilityClass.Barrier));
        policy.Propose("Retain the short audit summary.", MemoryKind.Preference, MemoryCandidateOrigin.User);
        var record = Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        var failingForget = new SessionMemoryPolicy(fx.Session, memory, () => fx.Payloads,
            _ => throw new IOException("fixture event-store failure"));
        Assert.Throws<IOException>(() => failingForget.Forget(record.Id, TestContext.Current.CancellationToken));
        var restored = Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        Assert.Equal(record.Id, restored.Id);
        Assert.Equal(record.Content, restored.Content);
        Assert.DoesNotContain(fx.Payloads, item => item is MemoryForgotten);
    }

    [Fact]
    public void Failed_forget_restores_the_entire_supersedes_chain_exactly()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        var scope = MemoryScope.ForSession(fx.Session);
        var at = DateTimeOffset.UtcNow;
        MemoryRecord Record(string id, string content, string? supersedes, int minutes) => new(id, scope,
            MemoryKind.Preference, content, .75, 1, MemorySource.UserStated, at.AddMinutes(minutes), null,
            MemoryExpirationKind.WithScope, [], [], supersedes);
        memory.Add(Record("memory-a", "Prefer concise reports.", null, 0), CancellationToken.None);
        memory.Add(Record("memory-b", "Prefer concise build reports.", "memory-a", 1), CancellationToken.None);
        memory.Add(Record("memory-c", "Prefer concise build and test reports.", "memory-b", 2), CancellationToken.None);
        var before = memory.Read(scope, CancellationToken.None).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var journal = new List<DomainEventPayload>
        {
            new MemoryPromoted("candidate-b", "memory-b", "memory-a", at.AddMinutes(1)),
        };
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => journal,
            _ => throw new IOException("fixture journal failure"));

        Assert.Throws<IOException>(() => policy.Forget("memory-b", TestContext.Current.CancellationToken));
        var after = memory.Read(scope, CancellationToken.None).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    [Fact]
    public void Forget_does_not_restore_a_record_when_the_tombstone_receipt_landed_before_an_error()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => fx.Payloads, payload =>
        {
            stream.Append(payload, DurabilityClass.Barrier);
            if (payload is MemoryForgotten)
                throw new IOException("fixture reported an error after the durable append");
        });
        policy.Propose("Retain the short audit summary.", MemoryKind.Preference, MemoryCandidateOrigin.User);
        var record = Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));

        Assert.True(policy.Forget(record.Id, TestContext.Current.CancellationToken));
        Assert.Empty(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        Assert.Contains(fx.Payloads, item => item is MemoryForgotten forgotten && forgotten.MemoryId == record.Id);
    }

    [Fact]
    public void User_rejection_of_model_origin_candidate_suppresses_model_retry_but_allows_new_user_generation()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        memory.Add(new MemoryRecord("trusted-output", MemoryScope.ForSession(fx.Session), MemoryKind.Preference,
            "Use concise validation summaries.", .9, .95, MemorySource.UserStated,
            DateTimeOffset.UtcNow.AddMinutes(-1), null, MemoryExpirationKind.WithScope, ["session"], [], null),
            CancellationToken.None);
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => fx.Payloads,
            payload => stream.Append(payload, DurabilityClass.Barrier));
        const string content = "Use concise validation output.";
        var model = policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.Model);
        policy.Reject(model.Id);
        Assert.Equal("rejected", policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.Model).Decision);
        var explicitUser = policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.User);
        Assert.NotEqual(model.Id, explicitUser.Id);
        Assert.Equal("ask", explicitUser.Decision); // A fresh explicit candidate still honors the conflict gate.
        Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
    }

    [Fact]
    public void M8_new_permission_and_forgotten_diagnostics_resolve_for_all_supported_locales()
    {
        foreach (var locale in new[] { "es", "en", "fr", "it" })
        {
            var text = new OmniCore.Client.Localization(locale);
            foreach (var key in new[] { "skill.permissions.ceiling_exceeded",
                         "skill.permissions.process_consent_required", "client.memory.state.forgotten",
                         "sessionPreferences.error.memory_forget_rollback_failed" })
            {
                var resolution = text.ResolveDetailed(key);
                Assert.False(resolution.UsedFallback, $"{locale}: {key}");
                Assert.NotEqual(key, resolution.Text);
            }
        }
    }

    [Fact]
    public void User_rejection_is_not_replayed_as_model_memory()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => fx.Payloads,
            payload => new EventStream(fx.Store, fx.Codecs, fx.Session).Append(payload, DurabilityClass.Barrier));
        const string content = "The session prefers concise build reports.";
        var candidate = policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.User);
        policy.Reject(candidate.Id);
        Assert.Equal("rejected", policy.Propose(content, MemoryKind.Preference, MemoryCandidateOrigin.Model).Decision);
        Assert.Empty(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
    }

    [Fact]
    public void Declared_skill_permission_ceiling_from_ADR0026_is_loadable()
    {
        var manifest = new SkillManifestLoader().Load("id: audit.skill\nversion: '1'\nactivation:\n  explicit: true\ntools: [skill.compile]\npermissions:\n  filesystem:\n    read: ['src/**']\n  processes: ['dotnet']\n");
        Assert.Contains("src/**", manifest.Permissions!.Filesystem!.Read!);
        Assert.Contains("dotnet", manifest.Permissions.Processes!);
        Assert.Throws<InvalidDataException>(() => new SkillManifestLoader().Load(
            "id: audit.skill\nversion: '1'\ntools: [filesystem.read]\npermissions:\n  filesystem:\n    read: ['../secrets/**']\n"));
    }

    [Fact]
    public void Skill_execution_metadata_is_compatibility_only_and_unsupported_values_are_diagnosed()
    {
        using var fx = new Fixture();
        var root = Path.Combine(fx.Root, "skills");
        var package = Path.Combine(root, "metadata.skill");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "skill.yaml"),
            "id: metadata.skill\nversion: '1'\nexecution:\n  context: fork\n  preferredModelAlias: local-worker\n");
        File.WriteAllText(Path.Combine(package, "instructions.md"), "Metadata fixture.");
        var loader = new SkillManifestLoader();
        var discovered = Assert.Single(loader.Discover(root, ScopeLevel.Project, TrustLevel.Project,
            workspaceTrusted: true));
        Assert.Equal("fork", discovered.Manifest.Execution!.Context);
        Assert.Equal("local-worker", discovered.Manifest.Execution.PreferredModelAlias);
        Assert.Contains("no runtime consumer", discovered.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidDataException>(() => loader.Load(
            "id: metadata.skill\nversion: '1'\nexecution:\n  context: shared\n"));
    }

    [Fact]
    public void Active_skill_permission_is_a_real_tool_runtime_ceiling_and_does_not_block_unrelated_core_tool()
    {
        var manifest = new SkillManifest { Id = "audit.skill", Version = "1", Tools = ["skill.compile", "skill.read"],
            Permissions = new SkillPermissionManifest { Filesystem = new SkillPathPermissionManifest { Read = ["src/**"] } } };
        var loaded = new LoadedSkill(new DiscoveredSkill(".", manifest, ScopeLevel.Project, TrustLevel.Project,
            new string('a', 64), SkillLifecycleState.Loaded, null), "", new string('b', 64));
        IReadOnlyList<LoadedSkill> active = [loaded];
        var boundary = new SkillPermissionBoundary(() => active);
        var skillTool = new ClaimTool("skill.compile", hasProcess: true);
        var skillRead = new ClaimTool("skill.read", hasProcess: false, readPath: "src/main.cs");
        var unrelated = new ClaimTool("core.unrelated", hasProcess: false);
        var catalog = new FakeCatalog().Add(skillTool).Add(skillRead).Add(unrelated);
        var allow = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        { ["skill.compile"] = PermissionDecision.Allow, ["skill.read"] = PermissionDecision.Allow,
            ["core.unrelated"] = PermissionDecision.Allow });
        var executor = ScriptedToolExecutor.WithWorkspace(catalog, allow, ".", null, null, [boundary]);
        var rejected = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
            skillTool.Descriptor.Id, "skill", "{}"), false, CancellationToken.None);
        Assert.False(rejected.Succeeded);
        Assert.Equal(0, skillTool.Executions);
        var allowed = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
            unrelated.Descriptor.Id, "core", "{}"), false, CancellationToken.None);
        Assert.True(allowed.Succeeded);
        Assert.Equal(1, unrelated.Executions);
        var read = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
            skillRead.Descriptor.Id, "skill-read", "{}"), false, CancellationToken.None);
        Assert.True(read.Succeeded);
        Assert.Equal(1, skillRead.Executions);
        Assert.False(boundary.Evaluate(new ToolIntent(ToolCallId.New(), skillRead.Descriptor.Id, "{}",
            EffectClass.None, new ResourceClaims(["../outside.txt"], [], [], null, []), ToolRisk.Low, null)).Allowed);

        // A ceiling never grants process authority: even a normal Allow becomes a fresh Ask
        // for process use from a Project skill, and no effect runs without its explicit answer.
        manifest.Permissions.Processes = ["dotnet"];
        var askExecutor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
            { ["skill.compile"] = PermissionDecision.Allow }), ".", null, null, [boundary]);
        var asked = askExecutor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
            skillTool.Descriptor.Id, "skill-process", "{}"), false, CancellationToken.None);
        Assert.False(asked.Succeeded);
        Assert.Equal(0, skillTool.Executions);
        manifest.Permissions = new SkillPermissionManifest();
        Assert.False(boundary.Evaluate(new ToolIntent(ToolCallId.New(), skillTool.Descriptor.Id, "{}",
            EffectClass.NonIdempotent, new ResourceClaims([], [], [],
                new ProcessClaim("dotnet", [], "External", false, "."), []), ToolRisk.High, null)).Allowed);
        manifest.Permissions = null;
        var unboundedByManifest = boundary.Evaluate(new ToolIntent(ToolCallId.New(), skillTool.Descriptor.Id, "{}",
            EffectClass.NonIdempotent, new ResourceClaims([], [], [],
                new ProcessClaim("dotnet", [], "External", false, "."), []), ToolRisk.High, null));
        Assert.True(unboundedByManifest.Allowed);
        Assert.True(unboundedByManifest.RequiresAsk); // Project origin still requires consent.
        manifest.Permissions = new SkillPermissionManifest { Processes = ["dotnet"] };
        Assert.False(boundary.Evaluate(new ToolIntent(ToolCallId.New(), skillTool.Descriptor.Id, "{}",
            EffectClass.NonIdempotent, new ResourceClaims([], [], [],
                new ProcessClaim("dotnet", [], "External", true, "."), []), ToolRisk.High, null)).Allowed);
    }

    [Fact]
    public void Skill_file_globs_cannot_be_claimed_as_authority_and_list_search_require_a_subtree_ceiling()
    {
        var manifest = new SkillManifest
        {
            Id = "audit.reader",
            Version = "1",
            Tools = ["filesystem.read", "filesystem.list", "search.text"],
            Permissions = new SkillPermissionManifest
            {
                Filesystem = new SkillPathPermissionManifest { Read = ["src/*"] }
            }
        };
        var loaded = new LoadedSkill(new DiscoveredSkill(".", manifest, ScopeLevel.Project, TrustLevel.Project,
            new string('c', 64), SkillLifecycleState.Loaded, null), "", new string('d', 64));
        var boundary = new SkillPermissionBoundary(() => [loaded]);

        Assert.True(boundary.Evaluate(Intent("filesystem.read", "{\"path\":\"src/file.txt\"}", ["src/file.txt"])).Allowed);
        Assert.False(boundary.Evaluate(Intent("filesystem.read", "{\"path\":\"src/**\"}", ["src/**"])).Allowed);
        Assert.False(boundary.Evaluate(Intent("filesystem.list", "{\"path\":\"src\",\"recursive\":false}", ["src"])).Allowed);
        Assert.False(boundary.Evaluate(Intent("search.text", "{\"path\":\"src\",\"pattern\":\"secret\"}", ["src"])).Allowed);

        manifest.Permissions.Filesystem!.Read = ["src/**"];
        Assert.True(boundary.Evaluate(Intent("filesystem.list", "{\"path\":\"src\",\"recursive\":false}", ["src"])).Allowed);
        Assert.True(boundary.Evaluate(Intent("search.text", "{\"path\":\"src\",\"pattern\":\"secret\"}", ["src"])).Allowed);
        Assert.True(boundary.Evaluate(Intent("filesystem.read", "{\"path\":\"src/**\"}", ["src/**"])).Allowed);
        Assert.False(boundary.Evaluate(Intent("filesystem.read", "{\"path\":\"other/**\"}", ["other/**"])).Allowed);

        static ToolIntent Intent(string tool, string arguments, IReadOnlyList<string> reads) => new(
            ToolCallId.New(), new ToolId(tool), arguments, EffectClass.None,
            new ResourceClaims(reads, [], [], null, []), ToolRisk.Low, null);
    }

    [Fact]
    public void Skill_ceiling_globs_match_concrete_file_segments_without_widening_claim_glob_inclusion()
    {
        var manifest = new SkillManifest { Id = "audit.glob", Version = "1", Tools = ["filesystem.read"],
            Permissions = new SkillPermissionManifest { Filesystem = new SkillPathPermissionManifest { Read = ["src/*.cs"] } } };
        var loaded = new LoadedSkill(new DiscoveredSkill(".", manifest, ScopeLevel.Project, TrustLevel.Project,
            new string('e', 64), SkillLifecycleState.Loaded, null), "", new string('f', 64));
        var boundary = new SkillPermissionBoundary(() => [loaded]);
        Assert.True(Read("src/main.cs").Allowed);
        Assert.False(Read("src/main.txt").Allowed);
        Assert.False(ReadClaim("src/**").Allowed); // src/*.cs cannot prove it covers every descendant.

        manifest.Permissions.Filesystem!.Read = ["**/*.cs"];
        Assert.True(Read("src/main.cs").Allowed);
        Assert.True(Read("main.cs").Allowed); // ** may match zero directory segments.
        Assert.False(Read("src/main.txt").Allowed);

        ModelCapabilityDecision Read(string path) => boundary.Evaluate(new ToolIntent(ToolCallId.New(),
            new ToolId("filesystem.read"), JsonSerializer.Serialize(new { path }), EffectClass.None,
            new ResourceClaims([path], [], [], null, []), ToolRisk.Low, null));
        ModelCapabilityDecision ReadClaim(string path) => boundary.Evaluate(new ToolIntent(ToolCallId.New(),
            new ToolId("filesystem.read"), JsonSerializer.Serialize(new { path }), EffectClass.None,
            new ResourceClaims([path], [], [], null, []), ToolRisk.Low, null));
    }

    [Fact]
    public void Memory_family_generation_follows_journal_order_when_proposal_clock_is_future_dated()
    {
        using var fx = new Fixture();
        var memory = new SessionMemoryStore(fx.Root);
        var journal = new List<DomainEventPayload>();
        var future = DateTimeOffset.UtcNow.AddYears(10);
        void Emit(DomainEventPayload payload) => journal.Add(payload is MemoryCandidateProposed proposed
            ? proposed with { ProposedAt = future } : payload);
        var policy = new SessionMemoryPolicy(fx.Session, memory, () => journal, Emit);
        var original = policy.Propose("Keep concise build reports.", MemoryKind.Preference, MemoryCandidateOrigin.User);
        var originalRecord = Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        Assert.True(policy.Forget(originalRecord.Id, TestContext.Current.CancellationToken));

        var readded = policy.Propose("Keep concise build reports.", MemoryKind.Preference, MemoryCandidateOrigin.User);
        var retried = policy.Propose("Keep concise build reports.", MemoryKind.Preference, MemoryCandidateOrigin.User);
        Assert.NotEqual(original.Id, readded.Id);
        Assert.Equal(readded.Id, retried.Id);
        Assert.Single(memory.Read(MemoryScope.ForSession(fx.Session), CancellationToken.None));
        Assert.Equal(2, journal.OfType<MemoryCandidateProposed>().Count());
    }

    private sealed class ClaimTool(string id, bool hasProcess, string? readPath = null) : ITool
    {
        public int Executions { get; private set; }
        public ToolDescriptor Descriptor { get; } = new(new ToolId(id), id, new InputSchema("{}"), [],
            readOnly: false, destructive: false, ToolRisk.High, ComponentSource.Core(), ToolProtection.None,
            hasProcess ? EffectClass.NonIdempotent : EffectClass.None);
        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) => new Prepared(
            new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                hasProcess ? EffectClass.NonIdempotent : EffectClass.None,
                new ResourceClaims(readPath is null ? [] : [readPath], [], [],
                    hasProcess ? new ProcessClaim("dotnet", [], "External", false, context.WorkspaceRoot) : null, []),
                ToolRisk.High, null));
        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(new ToolResult("ok", null, null, 0, false, EffectOutcome.Applied));
        }
    }

    private sealed class OfficialMcp : IMcpRequestTransport
    {
        public System.Threading.Tasks.Task<string> SendAsync(McpServerDefinition server, string method,
            string parametersJson, CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + (method == "server/discover"
                    ? "{\"resultType\":\"complete\",\"supportedVersions\":[\"2026-07-28\"],\"capabilities\":{\"tools\":{}}}"
                    : "{\"resultType\":\"complete\",\"tools\":[{\"name\":\"lookup\",\"inputSchema\":{\"type\":\"object\"}}]}") + "}");
    }

    [Fact]
    public void Host_skill_composition_intersects_real_read_ceilings_and_release_removes_them_with_fingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-permission-host-" + Guid.NewGuid().ToString("N"));
        var skillsRoot = Path.Combine(root, ".omnicore", "skills");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "other"));
        File.WriteAllText(Path.Combine(root, "src", "allowed.cs"), "allowed");
        File.WriteAllText(Path.Combine(root, "other", "denied.cs"), "other");
        WriteSkill("one.skill", "src/**");
        WriteSkill("two.skill", "other/**");
        try
        {
            var discovered = new SkillManifestLoader().Discover(skillsRoot, ScopeLevel.Project,
                TrustLevel.Project, workspaceTrusted: true).Select(skill => skill with { State = SkillLifecycleState.Eligible }).ToArray();
            var byId = discovered.ToDictionary(skill => skill.Manifest.Id!, StringComparer.Ordinal);
            var session = SessionId.New();
            var activationStore = new SessionSkillActivationStore(Path.Combine(root, "data"));
            foreach (var id in new[] { "one.skill", "two.skill" })
            {
                var loaded = new SkillManifestLoader().Activate(byId[id]);
                activationStore.Activate(session, ScopeLevel.Project, id, byId[id].ManifestSha256,
                    loaded.PackageContentSha256!);
            }
            var composition = new SkillTurnComposition(session, activationStore, () => discovered,
                new SkillSelectionLimits(3, 4096), new FakeTokenCounter(), Path.Combine(root, "data"),
                guidanceEnabled: true, includeWorkingState: false, workingState: "");
            var snapshot = composition.Refresh();
            IReadOnlyList<LoadedSkill> active = snapshot.LoadedSkills;
            var boundary = new SkillPermissionBoundary(() => active, root);
            var readTool = new ReadFileTool(new PathBoundaryValidator());
            var catalog = new FakeCatalog().Add(readTool);
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                { ["filesystem.read"] = PermissionDecision.Allow }), root, null, null, [boundary]);
            var call = new ValidatedToolCall(ToolCallId.New(), readTool.Descriptor.Id, "read",
                "{\"path\":\"src/allowed.cs\"}");
            var intersected = executor.ExecuteToolWithoutJournal(call, false, CancellationToken.None);
            Assert.False(intersected.Succeeded); // two active skills intersect; other/** narrows src/**

            var secondActivation = activationStore.ReadDetailed(session)[new(ScopeLevel.Project, "two.skill")];
            Assert.True(activationStore.Release(session, ScopeLevel.Project, "two.skill"));
            activationStore.SuppressAutomatic(session, ScopeLevel.Project, "two.skill",
                secondActivation.ManifestSha256, secondActivation.PackageContentSha256);
            snapshot = composition.Refresh();
            active = snapshot.LoadedSkills;
            Assert.Single(snapshot.LoadedSkills);
            Assert.Single(snapshot.Fingerprints);
            var allowed = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                readTool.Descriptor.Id, "read", "{\"path\":\"src/allowed.cs\"}"), false,
                CancellationToken.None);
            Assert.True(allowed.Succeeded);
            var outside = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                readTool.Descriptor.Id, "outside", "{\"path\":\"other/denied.cs\"}"), false, CancellationToken.None);
            Assert.False(outside.Succeeded);

            var firstActivation = activationStore.ReadDetailed(session)[new(ScopeLevel.Project, "one.skill")];
            Assert.True(activationStore.Release(session, ScopeLevel.Project, "one.skill"));
            activationStore.SuppressAutomatic(session, ScopeLevel.Project, "one.skill",
                firstActivation.ManifestSha256, firstActivation.PackageContentSha256);
            snapshot = composition.Refresh();
            active = snapshot.LoadedSkills;
            Assert.Empty(snapshot.LoadedSkills);
            Assert.Empty(snapshot.Fingerprints);
            var normalPolicyRead = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                readTool.Descriptor.Id, "released", "{\"path\":\"other/denied.cs\"}"), false, CancellationToken.None);
            Assert.True(normalPolicyRead.Succeeded); // normal policy remains the permission authority.
        }
        finally { Directory.Delete(root, recursive: true); }

        void WriteSkill(string id, string pattern)
        {
            var directory = Path.Combine(skillsRoot, id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "skill.yaml"), $"id: {id}\nversion: 1\ntools: [filesystem.read]\npermissions:\n  filesystem:\n    read: ['{pattern}']\n");
            File.WriteAllText(Path.Combine(directory, "instructions.md"), "Read only within the declared boundary.");
        }
    }

    private sealed class ChangingValidator : IExtensionRpcInvoker
    {
        private int _calls;
        public int Calls => _calls;
        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, ResourceClaims authorizedClaims, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"results\":[{\"id\":\"check\",\"passed\":"
                + (++_calls > 1 ? "true" : "false") + ",\"summary\":\"audit\"}]}}");
    }

    private sealed class Hook(LifecyclePoint point, HookResult result) : IHook
    {
        public int Calls { get; private set; }
        public HookRegistration Registration { get; } = new("audit.hook", ComponentSource.Core(), point,
            new HashSet<HookCapability> { point == LifecyclePoint.RunCompleted ? HookCapability.Observe : HookCapability.RequireValidation },
            new HashSet<HookCapability> { point == LifecyclePoint.RunCompleted ? HookCapability.Observe : HookCapability.RequireValidation },
            HookArgumentAccess.Redacted, true);
        public ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(result); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "OmniCore-M8-audit-" + Guid.NewGuid().ToString("N"));
        public InMemoryEventStore Store { get; } = new();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public FileArtifactStore Artifacts { get; }
        public OmniServer Server { get; }
        public OmniCliRuntime Runtime { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public LaneId Lane { get; }
        public DomainEventPayload[] Payloads => Store.ReadFrom(Session, 1).Select(Codecs.Decode).ToArray();
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink(), Artifacts);
            var ack = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"act\",\"objective\":\"audit\",\"workspace\":"
                + JsonSerializer.Serialize(Root) + "}"), CancellationToken.None);
            Assert.Equal("ok", ack.Status);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
            Lane = Assert.IsType<LaneId>(Server.LastLaneId());
            Runtime = OmniCliRuntime.Create(Root);
        }
        public ExplorerTurn Turn(IReadOnlyList<IHook> hooks)
        {
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), Root);
            return new ExplorerTurn((_, _) => new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "audit", null)),
                executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "audit"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null), Store,
                Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy(), lifecycleHooks: hooks);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
