using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_model_selection_initializes_a_legacy_missing_routing_snapshot_with_revision_zero()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-legacy-routing-cas-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        var home = Path.Combine(root, "agent-home");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        var names = new[] { "OMNICORE_DATA_DIR", "OMNICORE_CONFIG_DIR", "OMNICORE_AGENT_HOME", "CODEX_HOME",
            "CLAUDE_CONFIG_DIR", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        var previousDirectory = Environment.CurrentDirectory;
        var key = RandomNumberGenerator.GetBytes(32);
        Process? host = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", home);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(home, ".codex"));
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(home, ".claude"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", Path.Combine(home, ".config", "opencode"));
            Environment.CurrentDirectory = workspace;
            File.WriteAllText(Path.Combine(config, "providers.yaml"), """
                providers:
                  local-fixture:
                    family: OpenAIResponses
                    profile: api
                    baseUrl: http://127.0.0.1:1
                    auth: none
                    billingMode: Local
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  legacy-fixture: { provider: local-fixture, context: 32768, recommendedUsableContext: 24000, maxOutput: 512, parametersBillions: 100 }
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: legacy-fixture\n", new UTF8Encoding(false));

            // This is a valid pre-routing Session journal: the Session and its security ceiling
            // exist, but no routing snapshot or Run was ever written.
            var session = SessionId.New();
            var canonicalWorkspace = ProjectIdentity.CanonicalWorkspacePath(workspace);
            var journalPath = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            using (var store = new SqliteEventStore(journalPath))
            {
                var stream = new EventStream(store, EventCodecs.Create(), session);
                stream.AppendBatch(
                [
                    new SessionCreated(session, WorkspaceId.Of(canonicalWorkspace).ToString(), workspace,
                        ProfileId.New(), DateTimeOffset.UtcNow),
                    SessionSecurityPolicySet.FromPreset(session, 1, ModelPolicyCategory.FullAgent,
                        SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow),
                ], DurabilityClass.Barrier);
            }

            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var hello = ReadNativeHello(host);
            var beforeResume = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(JsonValueKind.Null, beforeResume.GetProperty("selection").ValueKind);
            Assert.Equal(0, beforeResume.GetProperty("selectionRevision").GetInt64());
            Assert.Equal(JsonValueKind.Null, beforeResume.GetProperty("routingPolicyRevision").ValueKind);
            Assert.Equal(JsonValueKind.Null, beforeResume.GetProperty("activeRunId").ValueKind);
            using (var store = new SqliteEventStore(journalPath, readOnly: true))
            {
                var codecs = EventCodecs.Create();
                var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
                Assert.DoesNotContain(events, item => item is SessionRoutingPolicySet);
                Assert.DoesNotContain(events, item => item is RunCreated);
            }

            var scopeBeforeResume = QueryNativeJson(host, "humanActionScope");
            var resume = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            { cmd = "conversation.resume", sessionId = session.ToString() }));
            var resumed = Ack(RawExchange(host, TrustedActionEnvelope(resume, key, hello.Nonce, workspace,
                NullableJsonString(scopeBeforeResume.GetProperty("sessionId")), NullableJsonString(scopeBeforeResume.GetProperty("runId")))));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, resumed.Outcome?.Kind);
            Assert.Equal(session.ToString(), ReadCurrentSessionId(host));

            var selectionBefore = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(session.ToString(), selectionBefore.GetProperty("sessionId").GetString());
            Assert.Equal(JsonValueKind.Null, selectionBefore.GetProperty("selection").ValueKind);
            Assert.Equal(0, selectionBefore.GetProperty("selectionRevision").GetInt64());
            Assert.Equal(JsonValueKind.Null, selectionBefore.GetProperty("routingPolicyRevision").ValueKind);
            Assert.Equal(1, selectionBefore.GetProperty("sessionPolicyRevision").GetInt64());
            Assert.Null(NullableJsonString(selectionBefore.GetProperty("activeRunId")));

            var catalog = QueryNativeJson(host, "models.catalog");
            var catalogRevision = catalog.GetProperty("catalogRevision").GetString()!;
            Assert.Contains(catalog.GetProperty("models").EnumerateArray(),
                model => model.GetProperty("modelId").GetString() == "legacy-fixture");
            var select = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "session.model.select", sessionId = session.ToString(), modelId = "legacy-fixture",
                expectedSelectionRevision = 0, expectedRoutingPolicyRevision = 0,
                expectedSessionPolicyRevision = 1, expectedRunId = (string?)null, catalogRevision,
            }));
            var selectionAck = Ack(RawExchange(host, TrustedActionEnvelope(select, key, hello.Nonce, workspace,
                session.ToString(), null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, selectionAck.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, select).GetProperty("state").GetString());
            var selected = QueryNativeJson(host, "session.model.selection");
            Assert.Equal("legacy-fixture", selected.GetProperty("selection").GetProperty("modelId").GetString());
            Assert.Equal(1, selected.GetProperty("selectionRevision").GetInt64());
            Assert.Equal(1, selected.GetProperty("routingPolicyRevision").GetInt64());
            Assert.Null(NullableJsonString(selected.GetProperty("activeRunId")));
            using (var store = new SqliteEventStore(journalPath, readOnly: true))
            {
                var codecs = EventCodecs.Create();
                var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
                Assert.Single(events.OfType<SessionRoutingPolicySet>());
                Assert.DoesNotContain(events, item => item is RunCreated);
            }
        }
        finally
        {
            if (host is not null)
            {
                try { await StopNativeRawHost(host); } catch (InvalidOperationException) { }
            }
            CryptographicOperations.ZeroMemory(key);
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            Environment.CurrentDirectory = previousDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Native_session_skills_and_memory_use_hmac_cas_isolation_and_durable_source_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-native-session-preferences-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        var agentHome = Path.Combine(root, "agent-home");
        var skillDirectory = Path.Combine(workspace, ".omnicore", "skills", "fixture.skill");
        Directory.CreateDirectory(skillDirectory);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(skillDirectory, "skill.yaml"), """
            id: fixture.skill
            version: 1.0.0
            displayName: Native fixture skill
            sealed: false
            modelInvocable: false
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(skillDirectory, "instructions.md"), "NATIVE_SKILL_MARKER_ALPHA", new UTF8Encoding(false));

        var environmentNames = new[] { "OMNICORE_DATA_DIR", "OMNICORE_CONFIG_DIR", "OMNICORE_AGENT_HOME",
            "CODEX_HOME", "CLAUDE_CONFIG_DIR", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR" };
        var previousEnvironment = environmentNames.ToDictionary(name => name, Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var previousDirectory = Environment.CurrentDirectory;
        var key = RandomNumberGenerator.GetBytes(32);
        Process? host = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", agentHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(agentHome, ".codex"));
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(agentHome, ".claude"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(agentHome, ".config"));
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", Path.Combine(agentHome, ".config", "opencode"));
            Environment.CurrentDirectory = workspace;
            new WorkspaceTrustStore(OmniHost.CreatePlatformPaths()).SetTrusted(workspace, true);

            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var helloInfo = ReadNativeHello(host);
            var nonce = helloInfo.Nonce;
            var negotiated = helloInfo.Capabilities;
            Assert.Contains("command.skills.activate.user", negotiated);
            Assert.Contains("command.skills.release.user", negotiated);
            Assert.Contains("command.memory.session.add.user", negotiated);
            Assert.Contains("command.memory.session.propose.user", negotiated);
            Assert.Contains("command.memory.session.forget.user", negotiated);
            Assert.Contains("command.memory.session.promote.user", negotiated);
            Assert.Contains("command.memory.session.reject.user", negotiated);

            var firstSession = CreatePreferenceSession(host, key, nonce, workspace);
            var skillsBefore = QueryNativeJson(host, "skills");
            var memoryBefore = QueryNativeJson(host, "memory.session");
            Assert.Equal(firstSession, skillsBefore.GetProperty("session").GetString());
            Assert.Equal(firstSession, memoryBefore.GetProperty("session").GetString());
            Assert.Matches("^[0-9A-Fa-f]{64}$", skillsBefore.GetProperty("activationRevision").GetString()!);
            Assert.Matches("^[0-9A-Fa-f]{64}$", memoryBefore.GetProperty("revision").GetString()!);
            var discoveredSkill = Assert.Single(skillsBefore.GetProperty("skills").EnumerateArray(),
                skill => skill.GetProperty("id").GetString() == "fixture.skill");
            Assert.Equal("Discovered", discoveredSkill.GetProperty("state").GetString());
            var manifestHash = discoveredSkill.GetProperty("manifestSha256").GetString()!;
            var originalPackageHash = discoveredSkill.GetProperty("packageContentSha256").GetString()!;
            var sequenceBeforeQueries = FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64();
            var secondSkillsQuery = QueryNativeJson(host, "skills");
            var secondMemoryQuery = QueryNativeJson(host, "memory.session");
            Assert.Equal(skillsBefore.GetProperty("activationRevision").GetString(),
                secondSkillsQuery.GetProperty("activationRevision").GetString());
            Assert.Equal(memoryBefore.GetProperty("revision").GetString(), secondMemoryQuery.GetProperty("revision").GetString());
            Assert.Equal(sequenceBeforeQueries, FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64());

            // A package-content change after discovery invalidates the reviewed package hash.
            File.WriteAllText(Path.Combine(skillDirectory, "instructions.md"), "NATIVE_SKILL_MARKER_BETA", new UTF8Encoding(false));
            var changedPackageSelection = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "skills.activate", skillId = "fixture.skill", scope = "Project",
                    expectedSessionId = firstSession,
                    expectedRevision = skillsBefore.GetProperty("activationRevision").GetString(),
                    expectedManifestSha256 = manifestHash, expectedPackageContentSha256 = originalPackageHash,
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, changedPackageSelection.Ack.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, changedPackageSelection.Command).GetProperty("state").GetString());
            Assert.Equal(sequenceBeforeQueries, FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64());

            var currentSkills = QueryNativeJson(host, "skills");
            var currentSkill = Assert.Single(currentSkills.GetProperty("skills").EnumerateArray(),
                skill => skill.GetProperty("id").GetString() == "fixture.skill");
            var currentPackageHash = currentSkill.GetProperty("packageContentSha256").GetString()!;
            Assert.NotEqual(originalPackageHash, currentPackageHash);
            var activate = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "skills.activate", skillId = "fixture.skill", scope = "Project",
                    expectedSessionId = firstSession,
                    expectedRevision = currentSkills.GetProperty("activationRevision").GetString(),
                    expectedManifestSha256 = manifestHash, expectedPackageContentSha256 = currentPackageHash,
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, activate.Ack.Outcome?.Kind);
            var activationReceipt = LookupNativeReceipt(host, activate.Command);
            Assert.Equal("completed", activationReceipt.GetProperty("state").GetString());
            Assert.Equal("Accepted", activationReceipt.GetProperty("ack").GetProperty("outcome").GetString());
            var activatedSkills = QueryNativeJson(host, "skills");
            Assert.Equal("Activated", Assert.Single(activatedSkills.GetProperty("skills").EnumerateArray(),
                skill => skill.GetProperty("id").GetString() == "fixture.skill").GetProperty("state").GetString());
            var activation = Assert.Single(activatedSkills.GetProperty("activations").EnumerateArray());
            Assert.Equal("fixture.skill", activation.GetProperty("id").GetString());
            Assert.Equal(currentPackageHash, activation.GetProperty("packageContentSha256").GetString());
            var activatedSequence = FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64();
            var duplicateActivation = Ack(RawExchange(host, activate.Proof));
            Assert.Equal(activate.Ack.FirstSeq, duplicateActivation.FirstSeq);
            Assert.Equal(activatedSequence, FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64());

            var staleSkillRevision = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "skills.release", skillId = "fixture.skill", scope = "Project",
                    expectedSessionId = firstSession,
                    expectedRevision = currentSkills.GetProperty("activationRevision").GetString(),
                    expectedActiveManifestSha256 = manifestHash, expectedActivePackageContentSha256 = currentPackageHash,
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, staleSkillRevision.Ack.Outcome?.Kind);
            Assert.Equal(activatedSequence, FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64());

            var initialMemoryRevision = memoryBefore.GetProperty("revision").GetString()!;
            var safeAdd = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "memory.session.propose", expectedSessionId = firstSession,
                    expectedRevision = initialMemoryRevision, content = "The preferred report format is concise.",
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, safeAdd.Ack.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, safeAdd.Command).GetProperty("state").GetString());
            var memoryAfterAdd = QueryNativeJson(host, "memory.session");
            var firstMemory = Assert.Single(memoryAfterAdd.GetProperty("records").EnumerateArray());
            Assert.Equal("The preferred report format is concise.", firstMemory.GetProperty("content").GetString());
            var memoryAfterAddSequence = FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64();
            Assert.Equal(safeAdd.Ack.FirstSeq, Ack(RawExchange(host, safeAdd.Proof)).FirstSeq);
            Assert.Equal(memoryAfterAddSequence, FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64());

            var staleMemory = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "memory.session.add", expectedSessionId = firstSession,
                    expectedRevision = initialMemoryRevision, content = "This stale addition must not be stored.",
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, staleMemory.Ack.Outcome?.Kind);
            Assert.Equal("sessionPreferences.error.memory_changed", staleMemory.Ack.Error);
            Assert.Single(QueryNativeJson(host, "memory.session").GetProperty("records").EnumerateArray());
            Assert.Equal(memoryAfterAddSequence, FindNativeConversation(host, firstSession).GetProperty("journalSequence").GetInt64());

            const string sensitiveFixture = "qa.fixture@example.invalid";
            var sensitiveAdd = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "memory.session.add", expectedSessionId = firstSession,
                    expectedRevision = memoryAfterAdd.GetProperty("revision").GetString(),
                    content = "Contact " + sensitiveFixture + " for the fixture.",
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, sensitiveAdd.Ack.Outcome?.Kind);
            var privacySnapshot = QueryNativeJson(host, "memory.session");
            var withheld = Assert.Single(privacySnapshot.GetProperty("candidates").EnumerateArray(),
                candidate => candidate.GetProperty("decision").GetString() == "rejected");
            Assert.Equal("privacy.sensitive", withheld.GetProperty("reason").GetString());
            Assert.DoesNotContain(sensitiveFixture, privacySnapshot.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(privacySnapshot.GetProperty("records").EnumerateArray(),
                record => record.GetProperty("content").GetString()!.Contains(sensitiveFixture, StringComparison.Ordinal));

            var journalPath = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            using (var journal = new SqliteEventStore(journalPath, readOnly: true))
            {
                var codecs = EventCodecs.Create();
                var events = journal.ReadFrom(SessionId.Parse(firstSession), 1).ToArray();
                Assert.DoesNotContain(events, item => item.PayloadJson.Contains(sensitiveFixture, StringComparison.Ordinal));
                var payloads = events.Select(codecs.Decode).ToArray();
                Assert.Contains(payloads, item => item is MemoryCandidateProposed
                    { Decision: "reject", Content: "[sensitive candidate withheld]" });
                Assert.Contains(payloads, item => item is MemoryRejected { ReasonCode: "privacy.sensitive" });
            }

            var forget = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "memory.session.forget", expectedSessionId = firstSession,
                    expectedRevision = privacySnapshot.GetProperty("revision").GetString(),
                    memoryId = firstMemory.GetProperty("id").GetString(),
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, forget.Ack.Outcome?.Kind);
            Assert.Empty(QueryNativeJson(host, "memory.session").GetProperty("records").EnumerateArray());

            // A second Session gets an independent memory and activation projection.
            var secondSession = CreatePreferenceSession(host, key, nonce, workspace);
            Assert.NotEqual(firstSession, secondSession);
            Assert.Empty(QueryNativeJson(host, "skills").GetProperty("activations").EnumerateArray());
            Assert.Empty(QueryNativeJson(host, "memory.session").GetProperty("records").EnumerateArray());
            var secondMemory = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "memory.session.add", expectedSessionId = secondSession,
                    expectedRevision = QueryNativeJson(host, "memory.session").GetProperty("revision").GetString(),
                    content = "Second Session only marker: SESSION_B_MEMORY_MARKER.",
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, secondMemory.Ack.Outcome?.Kind);

            var crossSession = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "memory.session.add", expectedSessionId = firstSession,
                expectedRevision = QueryNativeJson(host, "memory.session").GetProperty("revision").GetString(),
                content = "Must not cross Session boundary.",
            }));
            var currentScope = QueryNativeJson(host, "humanActionScope");
            var crossSessionAck = Ack(RawExchange(host, TrustedActionEnvelope(crossSession, key, nonce, workspace,
                secondSession, NullableJsonString(currentScope.GetProperty("runId")))));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, crossSessionAck.Outcome?.Kind);
            Assert.Single(QueryNativeJson(host, "memory.session").GetProperty("records").EnumerateArray());

            var resumeFirst = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            { cmd = "conversation.resume", sessionId = firstSession }));
            var resumeScope = QueryNativeJson(host, "humanActionScope");
            var resumed = Ack(RawExchange(host, TrustedActionEnvelope(resumeFirst, key, nonce, workspace,
                secondSession, NullableJsonString(resumeScope.GetProperty("runId")))));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, resumed.Outcome?.Kind);
            Assert.Empty(QueryNativeJson(host, "memory.session").GetProperty("records").EnumerateArray());
            Assert.Single(QueryNativeJson(host, "skills").GetProperty("activations").EnumerateArray());

            var duplicateSkillDirectory = Path.Combine(workspace, ".omnicore", "skills", "duplicate-copy");
            Directory.CreateDirectory(duplicateSkillDirectory);
            File.WriteAllText(Path.Combine(duplicateSkillDirectory, "skill.yaml"), """
                id: fixture.skill
                version: 1.0.0
                displayName: Ambiguous duplicate fixture
                sealed: false
                modelInvocable: false
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(duplicateSkillDirectory, "instructions.md"), "DUPLICATE_PACKAGE_FIXTURE", new UTF8Encoding(false));
            var ambiguousSkills = QueryNativeJson(host, "skills");
            Assert.Equal("unavailable", Assert.Single(ambiguousSkills.GetProperty("activations").EnumerateArray())
                .GetProperty("sourceState").GetString());
            Directory.Delete(duplicateSkillDirectory, recursive: true);

            // A reopened Host projects the durable activation as missing and permits exact release
            // without rediscovering/trusting the vanished source package.
            File.Delete(Path.Combine(skillDirectory, "skill.yaml"));
            File.Delete(Path.Combine(skillDirectory, "instructions.md"));
            Directory.Delete(skillDirectory);
            await StopNativeRawHost(host);
            host = null;
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            nonce = ReadNativeHello(host).Nonce;
            Assert.Equal(firstSession, ReadCurrentSessionId(host));
            var reopenedSkills = QueryNativeJson(host, "skills");
            var durableActivation = Assert.Single(reopenedSkills.GetProperty("activations").EnumerateArray());
            Assert.Equal("missing", durableActivation.GetProperty("sourceState").GetString());
            Assert.Equal(manifestHash, durableActivation.GetProperty("manifestSha256").GetString());
            Assert.Equal(currentPackageHash, durableActivation.GetProperty("packageContentSha256").GetString());

            var release = NativePreferenceCommand(host, key, nonce, workspace,
                JsonSerializer.Serialize(new
                {
                    cmd = "skills.release", skillId = "fixture.skill", scope = "Project",
                    expectedSessionId = firstSession,
                    expectedRevision = reopenedSkills.GetProperty("activationRevision").GetString(),
                    expectedActiveManifestSha256 = durableActivation.GetProperty("manifestSha256").GetString(),
                    expectedActivePackageContentSha256 = durableActivation.GetProperty("packageContentSha256").GetString(),
                }));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, release.Ack.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, release.Command).GetProperty("state").GetString());
            Assert.Empty(QueryNativeJson(host, "skills").GetProperty("activations").EnumerateArray());

            var resumeSecond = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            { cmd = "conversation.resume", sessionId = secondSession }));
            var currentAfterRelease = QueryNativeJson(host, "humanActionScope");
            var secondResumed = Ack(RawExchange(host, TrustedActionEnvelope(resumeSecond, key, nonce, workspace,
                firstSession, NullableJsonString(currentAfterRelease.GetProperty("runId")))));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, secondResumed.Outcome?.Kind);
            Assert.Equal("Second Session only marker: SESSION_B_MEMORY_MARKER.",
                Assert.Single(QueryNativeJson(host, "memory.session").GetProperty("records").EnumerateArray())
                    .GetProperty("content").GetString());
            Assert.Empty(QueryNativeJson(host, "skills").GetProperty("activations").EnumerateArray());
        }
        finally
        {
            if (host is not null)
            {
                try { await StopNativeRawHost(host); } catch (InvalidOperationException) { }
            }
            CryptographicOperations.ZeroMemory(key);
            foreach (var name in environmentNames)
                Environment.SetEnvironmentVariable(name, previousEnvironment[name]);
            Environment.CurrentDirectory = previousDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static (string Nonce, string[] Capabilities) ReadNativeHello(Process host)
    {
        var hello = RawExchange(host, WireEnvelope.FromJson(
            "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"session-preferences-hello-"
            + Guid.NewGuid().ToString("N") + "\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
        using var payload = JsonDocument.Parse(hello.PayloadJson);
        return (payload.RootElement.GetProperty("trustedActionNonce").GetString()!,
            payload.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(item => item.GetString()!).ToArray());
    }

    private static string CreatePreferenceSession(Process host, byte[] key, string nonce, string workspace)
    {
        var command = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}");
        var scope = QueryNativeJson(host, "humanActionScope");
        var ack = Ack(RawExchange(host, TrustedActionEnvelope(command, key, nonce, workspace,
            NullableJsonString(scope.GetProperty("sessionId")), NullableJsonString(scope.GetProperty("runId")))));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
        Assert.Equal("completed", LookupNativeReceipt(host, command).GetProperty("state").GetString());
        return ReadCurrentSessionId(host);
    }

    private static (WireEnvelope Command, WireEnvelope Proof, CommandAck Ack) NativePreferenceCommand(
        Process host, byte[] key, string nonce, string workspace, string json)
    {
        var command = WireEnvelope.Command(Ids.NewV7(), json);
        var scope = QueryNativeJson(host, "humanActionScope");
        var proof = TrustedActionEnvelope(command, key, nonce, workspace,
            NullableJsonString(scope.GetProperty("sessionId")), NullableJsonString(scope.GetProperty("runId")));
        return (command, proof, Ack(RawExchange(host, proof)));
    }
}
