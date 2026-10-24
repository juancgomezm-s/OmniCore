using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_transcript_import_cli_executable_process_is_passive_and_fails_closed()
    {
        await using var fixture = await NativeImportProcessFixture.StartAsync("cli-os");
        var transcriptPath = Path.Combine(fixture.Workspace, "explicit-transcript.json");
        File.WriteAllText(transcriptPath, NativeImportProcessFixture.TranscriptJson("cli-process-marker"), new UTF8Encoding(false));

        var valid = await fixture.RunCliImportAsync(transcriptPath);
        Assert.Equal(0, valid.ExitCode);
        using (var output = JsonDocument.Parse(valid.Stdout))
        {
            Assert.Equal("Imported", output.RootElement.GetProperty("origin").GetString());
            var session = JsonDocument.Parse(output.RootElement.GetProperty("session").GetString()!);
            fixture.ImportedSessionId = SessionId.Parse(session.RootElement.GetProperty("sessionId").GetString()!);
        }
        fixture.AddLog(new { operation = "cli_import_valid", pid = valid.Pid, exitCode = valid.ExitCode, stdout = valid.Stdout.Trim(), stderr = fixture.Redact(valid.Stderr) });

        var malformedPath = Path.Combine(fixture.Workspace, "malformed-transcript.json");
        File.WriteAllText(malformedPath,
            "{\"formatId\":1,\"schemaVersion\":1,\"messages\":[{\"role\":\"user\",\"text\":\"bad\"}]}",
            new UTF8Encoding(false));
        var malformed = await fixture.RunCliImportAsync(malformedPath);
        Assert.Equal(2, malformed.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(malformed.Stdout));
        fixture.AddLog(new { operation = "cli_import_malformed", pid = malformed.Pid, exitCode = malformed.ExitCode,
            stdout = malformed.Stdout.Trim(), stderr = fixture.Redact(malformed.Stderr) });

        var journal = fixture.JournalPath;
        using var store = new SqliteEventStore(journal, readOnly: true);
        Assert.Single(store.ReadEvents(EventType.Of("session.created")));
        Assert.Single(store.ReadEvents(EventType.Of("conversation.transcript_imported")));
        var imported = store.ReadFrom(fixture.ImportedSessionId!, 1).Select(EventCodecs.Create().Decode).ToArray();
        var policy = Assert.Single(imported.OfType<SessionSecurityPolicySet>());
        Assert.Equal(ModelPolicyCategory.ObserveOnly, policy.Category);
        Assert.Equal(SessionSecurityProvenance.Imported, policy.Provenance);
        Assert.Equal(SessionSecurityPolicySource.ImportReview, policy.Source);
        Assert.Empty(imported.OfType<RunCreated>());
        Assert.Empty(imported.OfType<UserInputReceived>());
        Assert.Empty(imported.OfType<HookApplied>());
        Assert.Equal(0, fixture.Provider.RequestCount);
        fixture.AddLog(new { operation = "cli_import_assertions", sessions = 1, importedEvents = 1,
            runs = 0, inputs = 0, hooks = 0, providerRequests = fixture.Provider.RequestCount,
            observeOnly = true, importedOrigin = true });
    }

    [Fact]
    public async Task Native_transcript_import_two_host_processes_share_receipt_and_replay_stably()
    {
        await using var fixture = await NativeImportProcessFixture.StartAsync("two-host-os");
        var cli = fixture.CliDll;
        var key = RandomNumberGenerator.GetBytes(32);
        fixture.HoldSecret(key);
        var hostA = StartRawHost(cli, fixture.Workspace, Convert.ToHexString(key));
        var hostB = StartRawHost(cli, fixture.Workspace, Convert.ToHexString(key));
        fixture.OwnHost(hostA, "host-a");
        fixture.OwnHost(hostB, "host-b");

        var helloA = RawExchange(hostA, WireEnvelope.FromJson(
            "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"import-process-hello-a\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
        var helloB = RawExchange(hostB, WireEnvelope.FromJson(
            "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"import-process-hello-b\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
        var nonceA = ReadTrustedNonce(helloA);
        var nonceB = ReadTrustedNonce(helloB);
        fixture.AddLog(new
        {
            operation = "hello",
            pidA = hostA.Id,
            pidB = hostB.Id,
            versionA = helloA.Version,
            versionB = helloB.Version,
            nonceA = "[redacted]",
            nonceB = "[redacted]",
            noncePresentA = nonceA.Length > 0,
            noncePresentB = nonceB.Length > 0,
        });
        Assert.NotEqual(hostA.Id, hostB.Id);
        Assert.False(hostA.HasExited);
        Assert.False(hostB.HasExited);
        fixture.AddLog(new { operation = "hosts_ready", pidA = hostA.Id, pidB = hostB.Id,
            nonceA = "[redacted]", nonceB = "[redacted]", bothPidsAliveBeforeRelease = true });

        const int chunkCount = 5;
        var text = new string('W', 60 * 1024);
        var messages = string.Join(",", Enumerable.Range(0, chunkCount).Select(index =>
            "{\"role\":\"user\",\"text\":" + JsonSerializer.Serialize(text + index.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "}"));
        var transcript = "{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"title\":\"OS receipt fixture\",\"messages\":[" + messages + "]}";
        var transcriptBytes = Encoding.UTF8.GetBytes(transcript);
        var commandId = Ids.NewV7();
        var payload = "{\"cmd\":\"conversation.import\",\"documentBase64\":"
            + JsonSerializer.Serialize(Convert.ToBase64String(transcriptBytes)) + "}";
        var command = WireEnvelope.Command(commandId, payload);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var proofA = TrustedActionEnvelope(command, key, nonceA, fixture.Workspace, null, null);
        var proofB = TrustedActionEnvelope(command, key, nonceB, fixture.Workspace, null, null);
        using var release = new ManualResetEventSlim(false);
        var requestA = Task.Run(() => SendConcurrentImport(hostA, proofA, release));
        var requestB = Task.Run(() => SendConcurrentImport(hostB, proofB, release));
        release.Set();
        var initial = await Task.WhenAll(requestA, requestB).WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
        Assert.Contains(initial, response => response.Ack.Status == "ok");
        Assert.All(initial, response => Assert.Contains(response.Ack.Status, new[] { "ok", "busy", "unknown" }));
        var overlapped = initial.Max(item => item.StartedAt) < initial.Min(item => item.CompletedAt);
        fixture.AddLog(new
        {
            operation = "concurrent_import_requests",
            commandId,
            payloadSha256 = requestHash,
            payloadUtf8Bytes = Encoding.UTF8.GetByteCount(command.PayloadJson),
            transcriptUtf8Bytes = transcriptBytes.Length,
            requestA = new { status = initial[0].Ack.Status, outcome = initial[0].Ack.Outcome?.Kind.ToString(),
                firstSeq = initial[0].Ack.FirstSeq, lastSeq = initial[0].Ack.LastSeq },
            requestB = new { status = initial[1].Ack.Status, outcome = initial[1].Ack.Outcome?.Kind.ToString(),
                firstSeq = initial[1].Ack.FirstSeq, lastSeq = initial[1].Ack.LastSeq },
            requestIntervalsOverlap = overlapped,
            sameCommandAndPayload = true,
            perHostNonceSignatures = true,
        });

        var resultA = await WaitForCompletedCommandReceiptAsync(hostA, commandId, requestHash);
        var resultB = await WaitForCompletedCommandReceiptAsync(hostB, commandId, requestHash);
        Assert.Equal(resultA.GetProperty("sessionId").GetString(), resultB.GetProperty("sessionId").GetString());

        var importedSessionId = SessionId.Parse(resultA.GetProperty("sessionId").GetString()!);
        var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), fixture.Workspace);
        var lastSessionPath = Path.Combine(workspaceData, "lastsession.txt");
        var lastSessionBefore = File.ReadAllBytes(lastSessionPath);
        var leaseStateBefore = ReadLeaseFiles(workspaceData);
        var sequenceBefore = ReadSessionSequence(fixture.JournalPath, importedSessionId);
        var sessionASelectionBefore = ReadHostCurrentSessionId(hostA);
        var sessionBSelectionBefore = ReadHostCurrentSessionId(hostB);
        var importedSessionText = importedSessionId.ToString();
        var nonOwner = sessionASelectionBefore == importedSessionText && sessionBSelectionBefore != importedSessionText
            ? hostB
            : sessionBSelectionBefore == importedSessionText && sessionASelectionBefore != importedSessionText
                ? hostA
                : throw new InvalidOperationException("The fixture could not identify one Host process without the imported Session lease.");
        var nonOwnerSession = ReferenceEquals(nonOwner, hostA) ? sessionASelectionBefore : sessionBSelectionBefore;
        fixture.AddLog(new { operation = "host_selection_before_replay", ownerSessionSelectedByA = sessionASelectionBefore == importedSessionText,
            ownerSessionSelectedByB = sessionBSelectionBefore == importedSessionText,
            nonOwnerPid = nonOwner.Id, nonOwnerHasSession = nonOwnerSession is not null });

        var wrongKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var invalidProof = TrustedActionEnvelope(command, wrongKey, nonceB, fixture.Workspace, null, null);
            var invalidReplay = Ack(RawExchange(hostB, invalidProof));
            Assert.Equal("error", invalidReplay.Status);
            Assert.Equal("trusted_action_invalid", invalidReplay.Error);
            fixture.AddLog(new { operation = "invalid_proof_replay", status = invalidReplay.Status, error = invalidReplay.Error,
                rejectionBeforeReceiptLookup = true });
        }
        finally { CryptographicOperations.ZeroMemory(wrongKey); }

        var changedCommand = WireEnvelope.Command(commandId, "{\"cmd\":\"conversation.import\",\"documentBase64\":"
            + JsonSerializer.Serialize(Convert.ToBase64String(Encoding.UTF8.GetBytes(
                NativeImportProcessFixture.TranscriptJson("changed-payload-same-command-id")))) + "}");
        var changedReplay = Ack(RawExchange(hostB,
            TrustedActionEnvelope(changedCommand, key, nonceB, fixture.Workspace, null, null)));
        Assert.Equal("error", changedReplay.Status);
        Assert.Equal("command_id_conflict", changedReplay.Error);
        var freshCommand = WireEnvelope.Command(Ids.NewV7(), command.PayloadJson);
        var nonOwnerNonce = ReferenceEquals(nonOwner, hostA) ? nonceA : nonceB;
        var freshWithoutReceipt = Ack(RawExchange(nonOwner,
            TrustedActionEnvelope(freshCommand, key, nonOwnerNonce, fixture.Workspace, nonOwnerSession, null)));
        Assert.Equal("busy", freshWithoutReceipt.Status);
        var freshResult = ReadCommandReceiptResult(nonOwner, freshCommand.MessageId,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(freshCommand.PayloadJson))));
        Assert.Equal("not_found", freshResult.GetProperty("state").GetString());
        fixture.AddLog(new { operation = "changed_payload_and_new_command", changedIdStatus = changedReplay.Status,
            changedIdError = changedReplay.Error, newIdStatus = freshWithoutReceipt.Status,
            newIdReceiptState = freshResult.GetProperty("state").GetString(), noNewSession = true });

        var replayA = Ack(RawExchange(hostA, TrustedActionEnvelope(command, key, nonceA, fixture.Workspace, null, null)));
        var replayB = Ack(RawExchange(hostB, TrustedActionEnvelope(command, key, nonceB, fixture.Workspace, null, null)));
        AssertOriginalReceipt(resultA, replayA, commandId);
        AssertOriginalReceipt(resultA, replayB, commandId);
        fixture.ImportedSessionId = importedSessionId;
        Assert.Equal(sessionASelectionBefore, ReadHostCurrentSessionId(hostA));
        Assert.Equal(sessionBSelectionBefore, ReadHostCurrentSessionId(hostB));
        Assert.Equal(lastSessionBefore, File.ReadAllBytes(lastSessionPath));
        Assert.Equal(leaseStateBefore, ReadLeaseFiles(workspaceData));
        Assert.Equal(sequenceBefore, ReadSessionSequence(fixture.JournalPath, importedSessionId));
        fixture.AddLog(new { operation = "receipt_replays", commandId, stateA = resultA.GetProperty("state").GetString(),
            stateB = resultB.GetProperty("state").GetString(), ackA = AckSummary(replayA), ackB = AckSummary(replayB),
            exactOriginalAckReturned = true, sessionSelectionStable = true, lastSessionStable = true,
            writerLeaseFilesStable = true, journalSequenceStable = true });

        using (var store = new SqliteEventStore(fixture.JournalPath, readOnly: true))
        {
            Assert.Single(store.ReadEvents(EventType.Of("session.created")));
            Assert.Single(store.ReadEvents(EventType.Of("conversation.transcript_imported")));
            var imported = store.ReadFrom(fixture.ImportedSessionId!, 1).Select(EventCodecs.Create().Decode).ToArray();
            var policy = Assert.Single(imported.OfType<SessionSecurityPolicySet>());
            Assert.Equal(ModelPolicyCategory.ObserveOnly, policy.Category);
            Assert.Equal(SessionSecurityProvenance.Imported, policy.Provenance);
            Assert.Equal(SessionSecurityPolicySource.ImportReview, policy.Source);
            Assert.Equal(chunkCount, Assert.Single(imported.OfType<ConversationTranscriptImported>()).Messages.Count);
            Assert.Empty(imported.OfType<RunCreated>());
            Assert.Empty(imported.OfType<UserInputReceived>());
            Assert.Empty(imported.OfType<HookApplied>());
        }
        Assert.Equal(0, fixture.Provider.RequestCount);
        fixture.AddLog(new { operation = "receipt_assertions", sessions = 1, importedEvents = 1, runs = 0,
            inputs = 0, hooks = 0, providerRequests = fixture.Provider.RequestCount,
            observeOnly = true, importedOrigin = true });
    }

    private static (CommandAck Ack, long StartedAt, long CompletedAt) SendConcurrentImport(Process host,
        WireEnvelope proof, ManualResetEventSlim release)
    {
        release.Wait();
        var started = Stopwatch.GetTimestamp();
        var ack = Ack(RawExchange(host, proof));
        return (ack, started, Stopwatch.GetTimestamp());
    }

    private static string ReadTrustedNonce(WireEnvelope hello)
    {
        using var document = JsonDocument.Parse(hello.PayloadJson);
        return document.RootElement.GetProperty("trustedActionNonce").GetString()
            ?? throw new InvalidDataException("Host hello did not provide a trusted action nonce.");
    }

    private static async Task<JsonElement> WaitForCompletedCommandReceiptAsync(Process host, string commandId, string requestHash)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 30);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var result = ReadCommandReceiptResult(host, commandId, requestHash);
            if (result.TryGetProperty("state", out var state) && state.GetString() == "completed") return result;
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Workspace import receipt did not reach completed state.");
    }

    private static JsonElement ReadCommandReceiptResult(Process host, string commandId, string requestHash)
    {
        var query = WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"command.result\",\"commandId\":"
            + JsonSerializer.Serialize(commandId) + ",\"requestHash\":" + JsonSerializer.Serialize(requestHash) + "}");
        using var response = JsonDocument.Parse(RawExchange(host, query).PayloadJson);
        return response.RootElement.Clone();
    }

    private static string? ReadHostCurrentSessionId(Process host)
    {
        var response = RawExchange(host, WireEnvelope.Command(Ids.NewV7(),
            "{\"operation\":\"query\",\"name\":\"humanActionScope\"}"));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        using var scope = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        var session = scope.RootElement.GetProperty("sessionId");
        return session.ValueKind == JsonValueKind.String ? session.GetString() : null;
    }

    private static string[] ReadLeaseFiles(string workspaceData)
    {
        var leaseRoot = Path.Combine(workspaceData, "journal.db.session-writers");
        if (!Directory.Exists(leaseRoot)) return [];
        return Directory.EnumerateFiles(leaseRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(leaseRoot, path))
            .ToArray();
    }

    private static long ReadSessionSequence(string journal, SessionId sessionId)
    {
        using var store = new SqliteEventStore(journal, readOnly: true);
        return store.CurrentSequence(sessionId);
    }

    private static void AssertOriginalReceipt(JsonElement result, CommandAck replay, string commandId)
    {
        var original = result.GetProperty("ack");
        Assert.Equal(commandId, replay.CommandId);
        Assert.Equal(original.GetProperty("status").GetString(), replay.Status);
        Assert.Equal(original.GetProperty("outcome").GetString(), replay.Outcome?.Kind.ToString());
        Assert.Equal(original.GetProperty("firstSeq").ValueKind == JsonValueKind.Number
            ? original.GetProperty("firstSeq").GetInt64() : (long?)null, replay.FirstSeq);
        Assert.Equal(original.GetProperty("lastSeq").ValueKind == JsonValueKind.Number
            ? original.GetProperty("lastSeq").GetInt64() : (long?)null, replay.LastSeq);
    }

    private static object AckSummary(CommandAck ack) => new
    {
        ack.CommandId,
        ack.Status,
        outcome = ack.Outcome?.Kind.ToString(),
        ack.FirstSeq,
        ack.LastSeq,
    };

    private sealed class NativeImportProcessFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly Dictionary<string, string?> _oldEnvironment;
        private readonly string _oldDirectory;
        private readonly ConcurrentQueue<object> _log = new();
        private readonly Dictionary<string, Process> _hosts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<string>> _stderrCaptures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _hostStderr = new(StringComparer.Ordinal);
        private readonly List<byte[]> _secrets = [];
        private bool _disposed;

        private NativeImportProcessFixture(string root, string workspace, string data, string config,
            string agentHome, string journal, string cliDll, Dictionary<string, string?> oldEnvironment,
            string oldDirectory, CliEndToEndTests.ScriptedHttpProvider provider)
        {
            _root = root;
            Workspace = workspace;
            Data = data;
            Config = config;
            AgentHome = agentHome;
            JournalPath = journal;
            CliDll = cliDll;
            _oldEnvironment = oldEnvironment;
            _oldDirectory = oldDirectory;
            Provider = provider;
        }

        public string Workspace { get; }
        public string Data { get; }
        public string Config { get; }
        public string AgentHome { get; }
        public string JournalPath { get; }
        public string CliDll { get; }
        public SessionId? ImportedSessionId { get; set; }
        public CliEndToEndTests.ScriptedHttpProvider Provider { get; }

        public static async Task<NativeImportProcessFixture> StartAsync(string attempt)
        {
            var root = Path.Combine(Path.GetTempPath(), "omnicore-native-import-process-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var data = Path.Combine(root, "data");
            var config = Path.Combine(root, "config");
            var agentHome = Path.Combine(root, "agent-home");
            foreach (var path in new[] { workspace, data, config, agentHome }) Directory.CreateDirectory(path);
            var externalRoots = new[] { "codex-home", "claude-config", "xdg-config", "opencode-config", "home" }
                .ToDictionary(name => name, name => Path.Combine(root, name), StringComparer.Ordinal);
            foreach (var path in externalRoots.Values) Directory.CreateDirectory(path);
            var names = new[] { "OMNICORE_DATA_DIR", "OMNICORE_CONFIG_DIR", "OMNICORE_AGENT_HOME", "CODEX_HOME",
                "CLAUDE_CONFIG_DIR", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR", "HOME", "USERPROFILE",
                "OMNI_LOCALE", "OMNI_MODEL", "OMNI_BASE_URL", "OMNICORE_TRUSTED_ACTION_BOOTSTRAP",
                "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "AZURE_OPENAI_API_KEY" };
            var oldEnvironment = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            var oldDirectory = Environment.CurrentDirectory;
            var provider = new CliEndToEndTests.ScriptedHttpProvider();
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(new DefaultPlatformPaths(data), workspace), "journal.db");
            var fixture = new NativeImportProcessFixture(root, workspace, data, config, agentHome, journal,
                cliDll, oldEnvironment, oldDirectory, provider);
            try
            {
                Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
                Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
                Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", agentHome);
                Environment.SetEnvironmentVariable("CODEX_HOME", externalRoots["codex-home"]);
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", externalRoots["claude-config"]);
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", externalRoots["xdg-config"]);
                Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", externalRoots["opencode-config"]);
                Environment.SetEnvironmentVariable("HOME", externalRoots["home"]);
                Environment.SetEnvironmentVariable("USERPROFILE", externalRoots["home"]);
                Environment.SetEnvironmentVariable("OMNI_LOCALE", null);
                Environment.SetEnvironmentVariable("OMNI_MODEL", null);
                Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
                Environment.SetEnvironmentVariable("OMNICORE_TRUSTED_ACTION_BOOTSTRAP", null);
                Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
                Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
                Environment.CurrentDirectory = workspace;
                File.WriteAllText(Path.Combine(config, "providers.yaml"), string.Join(Environment.NewLine,
                    "providers:", "  scripted:", "    family: OpenAIResponses", "    profile: api",
                    "    baseUrl: " + provider.BaseUrl, "    auth: none", "    billingMode: Local", ""), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "models.yaml"),
                    "models:\n  scripted-model:\n    provider: scripted\n    context: 8192\n    maxOutput: 512\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(config, "permissions.yaml"), "profile: balanced\nrules:\n", new UTF8Encoding(false));
                var defaults = new SessionSecurityDefaultConfiguration(OmniHost.CreatePlatformPaths());
                var current = defaults.Read();
                defaults.Set(current.Revision, ModelPolicyCategory.FullAgent, CancellationToken.None);
                var canonical = ProjectIdentity.CanonicalWorkspacePath(workspace);
                var trustId = WorkspaceId.Of(canonical).ToString();
                File.WriteAllText(Path.Combine(data, "trust.yaml"), "workspaces:\n  " + trustId
                    + ":\n    canonicalPath: " + canonical + "\n    trusted: true\n", new UTF8Encoding(false));
                fixture.AddLog(new { operation = "fixture_ready", attempt,
                    workspace = "[temporary fixture]", config = "[temporary fixture]", data = "[temporary fixture]",
                    agentHomeEmpty = !Directory.EnumerateFileSystemEntries(agentHome).Any(),
                    externalRootsEmpty = externalRoots.Values.All(path => !Directory.EnumerateFileSystemEntries(path).Any()),
                    providerBaseUrl = provider.BaseUrl, providerRequestsAtStart = provider.RequestCount });
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<(int ExitCode, int Pid, string Stdout, string Stderr)> RunCliImportAsync(string sourcePath)
        {
            if (!Path.GetFullPath(sourcePath).StartsWith(Path.GetFullPath(Workspace) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("CLI source must be inside this fixture workspace.");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Workspace,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(CliDll);
            start.ArgumentList.Add("conversation");
            start.ArgumentList.Add("import");
            start.ArgumentList.Add(sourcePath);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI executable process.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                var cleanupErrors = new List<Exception>();
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Exception exception) { cleanupErrors.Add(exception); }
                using var finalWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(finalWait.Token); }
                catch (OperationCanceledException) when (!process.HasExited)
                { cleanupErrors.Add(new TimeoutException("CLI import process remained alive after tree termination.")); }
                var timeoutError = new TimeoutException("CLI import process did not exit within 45 seconds.");
                if (cleanupErrors.Count > 0) throw new AggregateException(timeoutError.Message, [timeoutError, .. cleanupErrors]);
                throw timeoutError;
            }
            return (process.ExitCode, process.Id, await stdout.WaitAsync(TimeSpan.FromSeconds(5)),
                await stderr.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        public void OwnHost(Process process, string name)
        {
            _hosts.Add(name, process);
            _stderrCaptures.Add(name, process.StandardError.ReadToEndAsync());
        }
        public void HoldSecret(byte[] secret) => _secrets.Add(secret);
        public void AddLog(object entry) => _log.Enqueue(entry);

        public string Redact(string value)
        {
            foreach (var secret in _secrets)
            {
                var hex = Convert.ToHexString(secret);
                value = value.Replace(hex, "[redacted-key]", StringComparison.OrdinalIgnoreCase);
            }
            return value;
        }

        public static string TranscriptJson(string text) => "{\"formatId\":\"omnicore-conversation-import.v1\",\"schemaVersion\":1,\"title\":\"CLI process fixture\",\"messages\":[{\"role\":\"user\",\"text\":"
            + JsonSerializer.Serialize(text) + "}]}";

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            var cleanupErrors = new List<Exception>();
            foreach (var pair in _hosts.ToArray())
            {
                var process = pair.Value;
                try
                {
                    try { if (!process.HasExited) process.StandardInput.Close(); }
                    catch (Exception exception) { cleanupErrors.Add(exception); }
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await process.WaitForExitAsync(wait.Token); }
                    catch (OperationCanceledException)
                    {
                        cleanupErrors.Add(new TimeoutException($"Owned import Host {pair.Key} did not exit after stdin close."));
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                        using var finalWait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try { await process.WaitForExitAsync(finalWait.Token); }
                        catch (OperationCanceledException) when (!process.HasExited)
                        { cleanupErrors.Add(new TimeoutException($"Owned import Host {pair.Key} remained alive after tree termination.")); }
                    }
                }
                catch (Exception exception) { cleanupErrors.Add(exception); }
                finally
                {
                    if (_stderrCaptures.TryGetValue(pair.Key, out var capture))
                    {
                        try { _hostStderr[pair.Key] = Redact(await capture.WaitAsync(TimeSpan.FromSeconds(5))); }
                        catch (Exception exception) { cleanupErrors.Add(exception); }
                    }
                    try { process.Dispose(); } catch (Exception exception) { cleanupErrors.Add(exception); }
                }
            }
            try { await PreserveAttemptAsync(); }
            catch (Exception exception) { cleanupErrors.Add(exception); }
            try { await Provider.DisposeAsync(); } catch (Exception exception) { cleanupErrors.Add(exception); }
            foreach (var secret in _secrets)
                try { CryptographicOperations.ZeroMemory(secret); } catch (Exception exception) { cleanupErrors.Add(exception); }
            foreach (var name in _oldEnvironment.Keys)
                try { Environment.SetEnvironmentVariable(name, _oldEnvironment[name]); }
                catch (Exception exception) { cleanupErrors.Add(exception); }
            try { Environment.CurrentDirectory = _oldDirectory; } catch (Exception exception) { cleanupErrors.Add(exception); }
            try
            {
                using var poolKey = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = JournalPath, Pooling = false }.ToString());
                SqliteConnection.ClearPool(poolKey);
            }
            catch (Exception exception) { cleanupErrors.Add(exception); }
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (Exception exception) { cleanupErrors.Add(exception); }
            if (cleanupErrors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();
            if (cleanupErrors.Count > 1) throw new AggregateException("Native import process fixture cleanup failed.", cleanupErrors);
        }

        private async Task PreserveAttemptAsync()
        {
            var artifactBase = Path.Combine(FindRepositoryRoot(), "artifacts", "m9-native-import-processes");
            Directory.CreateDirectory(artifactBase);
            var name = Path.GetFileName(_root);
            var destination = Path.Combine(artifactBase, name);
            Directory.CreateDirectory(destination);
            CopyTree(Workspace, Path.Combine(destination, "workspace"));
            CopyTree(Config, Path.Combine(destination, "config"));
            CopyTree(AgentHome, Path.Combine(destination, "agent-home"));
            CopyNonDatabaseFiles(Data, Path.Combine(destination, "data"));
            var databases = new List<string>();
            foreach (var source in Directory.EnumerateFiles(Data, "*.db", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Data, source);
                var target = Path.Combine(destination, "data", Path.ChangeExtension(relative, ".backup.db"));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                using var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
                await input.OpenAsync();
                await output.OpenAsync();
                input.BackupDatabase(output);
                databases.Add(Path.GetRelativePath(destination, target));
            }
            File.WriteAllText(Path.Combine(destination, "process-stderr.json"),
                JsonSerializer.Serialize(_hostStderr, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(destination, "protocol-and-assertions.jsonl"),
                string.Join(Environment.NewLine, _log.Select(item => JsonSerializer.Serialize(item))) + Environment.NewLine, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(destination, "provider-fixture.json"), JsonSerializer.Serialize(new
            {
                baseUrl = Provider.BaseUrl,
                requestCount = Provider.RequestCount,
                requests = Provider.RequestBodies,
                responses = Provider.ResponseBodies,
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            var repo = FindRepositoryRoot();
            var sourcePaths = new[]
            {
                "src/OmniCore.Cli/Program.cs", "src/OmniCore.Cli/CliApp.cs",
                "src/OmniCore.Host/StdioProtocolHost.cs", "src/OmniCore.Host/OmniServer.Conversations.cs",
                "src/OmniCore.Domain/ConversationTranscriptImport.cs",
                "tests/OmniCore.Tests/StdioTransportTests.cs", "tests/OmniCore.Tests/StdioTransportTests.NativeImportProcesses.cs",
            }.Select(path => Path.Combine(repo, path)).ToArray();
            var outputDir = Path.GetDirectoryName(CliDll)!;
            var binaries = Directory.EnumerateFiles(outputDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).StartsWith("OmniCore.", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(path), "omni.dll", StringComparison.OrdinalIgnoreCase))
                .Append(CliDll)
                .Append(Path.Combine(repo, "tests", "OmniCore.Tests", "bin", "Debug", "net10.0", "OmniCore.Tests.dll"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var files = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != "manifest.json").Order(StringComparer.Ordinal).ToArray();
            var manifest = new
            {
                fixtureRoot = "temporary and owned by this test",
                exactCapturedFixtureFiles = files.Select(path => Path.GetRelativePath(destination, path)).ToArray(),
                capturedSha256 = files.ToDictionary(path => Path.GetRelativePath(destination, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal),
                sqliteConsistentBackups = databases,
                sourceSha256 = sourcePaths.ToDictionary(path => Path.GetRelativePath(repo, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal),
                executedCliDll = Path.GetRelativePath(repo, CliDll),
                binarySha256 = binaries.Where(File.Exists).ToDictionary(path => Path.GetRelativePath(repo, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal),
                originalFixtureFilesCopiedWithoutRedaction = true,
                hmacKeysAndNonces = "not captured; present only in memory/process environment",
                emptyAgentHome = !Directory.EnumerateFileSystemEntries(AgentHome).Any(),
                providerRequests = Provider.RequestCount,
                importedSessionId = ImportedSessionId?.ToString(),
            };
            File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            foreach (var child in Directory.EnumerateDirectories(source)) CopyTree(child, Path.Combine(target, Path.GetFileName(child)));
        }

        private static void CopyNonDatabaseFiles(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(target, name), overwrite: true);
            }
            foreach (var child in Directory.EnumerateDirectories(source))
                CopyNonDatabaseFiles(child, Path.Combine(target, Path.GetFileName(child)));
        }
    }
}
