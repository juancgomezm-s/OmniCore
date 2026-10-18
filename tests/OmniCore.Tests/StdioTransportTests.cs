using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed partial class StdioTransportTests
{
    [Fact]
    public void Stdio_host_query_discovers_a_trusted_project_skill_from_the_configured_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-stdio-agent-source-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var home = Path.Combine(root, "agent-home");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        var skill = Path.Combine(workspace, ".agents", "skills", "stdio-guide");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: stdio-guide\ndescription: Visible metadata\n---\nBody never leaves the source.", new UTF8Encoding(false));
        var canonical = ProjectIdentity.CanonicalWorkspacePath(workspace);
        var trustId = OmniCore.Domain.WorkspaceId.Of(canonical).ToString();
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "trust.yaml"), "workspaces:\n  " + trustId + ":\n    canonicalPath: "
            + canonical + "\n    trusted: true\n", new UTF8Encoding(false));
        var names = new[] { "OMNICORE_DATA_DIR", "OMNICORE_CONFIG_DIR", "OMNICORE_AGENT_HOME", "CODEX_HOME",
            "CLAUDE_CONFIG_DIR", "XDG_CONFIG_HOME", "OPENCODE_CONFIG_DIR" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        var previousDirectory = Environment.CurrentDirectory;
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

            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            using var client = StdioOmniClient.Start("dotnet", [cli, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            var query = Assert.IsType<SessionQueryResult>(client.Query("agent.sources", TestContext.Current.CancellationToken));
            using var json = JsonDocument.Parse(query.Json);
            Assert.True(json.RootElement.GetProperty("projectRootTrusted").GetBoolean());
            Assert.Contains(json.RootElement.GetProperty("sources").EnumerateArray(), source =>
                source.GetProperty("kind").GetString() == "Skill"
                && source.GetProperty("state").GetString() == "Discovered"
                && source.GetProperty("path").GetString()!.Contains("stdio-guide", StringComparison.Ordinal));
            Assert.DoesNotContain("Body never leaves the source.", query.Json, StringComparison.Ordinal);

            var catalog = Assert.IsType<SessionQueryResult>(client.Query("native.catalog", TestContext.Current.CancellationToken));
            using var catalogJson = JsonDocument.Parse(catalog.Json);
            var queryIds = catalogJson.RootElement.GetProperty("queries").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(queryIds.Length, queryIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(queryIds, id => id == "state");
            Assert.Contains(queryIds, id => id == "ui.locale");
            var locale = Assert.Single(catalogJson.RootElement.GetProperty("queries").EnumerateArray(),
                item => item.GetProperty("id").GetString() == "ui.locale");
            Assert.Equal("uiLocaleSettings", locale.GetProperty("wireName").GetString());
            Assert.Equal("uiLocaleSettings", locale.GetProperty("resultName").GetString());
            var uploadCommands = catalogJson.RootElement.GetProperty("commands").EnumerateArray()
                .Where(item => item.GetProperty("id").GetString()!.StartsWith("attachment.upload.", StringComparison.Ordinal)).ToArray();
            Assert.Equal(3, uploadCommands.Length);
            Assert.All(uploadCommands, item => Assert.Equal("", item.GetProperty("receipt").GetString()));
            Assert.Equal("sessionObservability:123", Assert.Single(StdioProtocol.NativeQueries,
                queryDescriptor => queryDescriptor.Name == "sessionObservability").DispatchName("sessionObservability:123"));
            Assert.Equal("uiLocaleSettings", Assert.Single(StdioProtocol.NativeQueries,
                queryDescriptor => queryDescriptor.Name == "ui.locale").DispatchName("ui.locale"));
            Assert.NotNull(client.Query("uiLocaleSettings", TestContext.Current.CancellationToken));
            var localeAlias = Assert.IsType<SessionQueryResult>(client.Query("ui.locale", TestContext.Current.CancellationToken));
            Assert.Equal("uiLocaleSettings", localeAlias.Name);
            var tools = Assert.IsType<SessionQueryResult>(client.Query("tools", TestContext.Current.CancellationToken));
            using var toolsJson = JsonDocument.Parse(tools.Json);
            Assert.NotEmpty(toolsJson.RootElement.GetProperty("tools").EnumerateArray());
            Assert.DoesNotContain("simulated", tools.Json, StringComparison.OrdinalIgnoreCase);
            var skills = Assert.IsType<SessionQueryResult>(client.Query("skills", TestContext.Current.CancellationToken));
            using var skillsJson = JsonDocument.Parse(skills.Json);
            Assert.Contains(skillsJson.RootElement.GetProperty("skills").EnumerateArray(), item =>
                item.GetProperty("id").GetString() == "stdio-guide");
            var context = Assert.IsType<SessionQueryResult>(client.Query("context", TestContext.Current.CancellationToken));
            using var contextJson = JsonDocument.Parse(context.Json);
            Assert.True(contextJson.RootElement.TryGetProperty("snapshot", out _));
            Assert.ThrowsAny<Exception>(() => client.Query("openrouter.credits", TestContext.Current.CancellationToken));
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            Environment.CurrentDirectory = previousDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Session_writer_lock_is_exclusive_between_processes_and_released_on_owner_death()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var session = OmniCore.Domain.SessionId.New();
        using (new SqliteEventStore(journal)) { }
        var helper = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
            ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
        var start = new ProcessStartInfo(helper)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("session-lock");
        start.ArgumentList.Add(journal);
        start.ArgumentList.Add(session.ToString());
        using var owner = Process.Start(start)!;
        var ready = await owner.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("READY", ready);
        try
        {
            using var contender = new SqliteEventStore(journal);
            Assert.Throws<SessionWriterLeaseUnavailableException>(() => contender.AcquireSessionWriterLease(session));
            owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            contender.AcquireSessionWriterLease(session);
            contender.Close();
        }
        finally
        {
            try { if (!owner.HasExited) owner.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { if (!owner.HasExited) await owner.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Command_receipt_survives_reopen_and_never_replays_changed_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var id = Guid.NewGuid().ToString("D");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("safe payload"u8));
        var store = new SqliteEventStore(journal);
        var first = store.ReserveCommandReceipt(id, hash, sessionId: null);
        Assert.Equal(CommandReceiptState.Reserved, first.State);
        var sessionId = OmniCore.Domain.SessionId.New().ToString();
        store.CompleteCommandReceipt(id, hash, "ok", "Accepted", null, 1, 2, sessionId: sessionId);
        store.PurgeSession(OmniCore.Domain.SessionId.Parse(sessionId), TestContext.Current.CancellationToken);
        store.Close();

        using var reopened = new SqliteEventStore(journal);
        var duplicate = reopened.ReserveCommandReceipt(id, hash, sessionId: "session-a");
        Assert.Equal(CommandReceiptState.Completed, duplicate.State);
        Assert.Equal(sessionId, duplicate.Receipt.SessionId);
        Assert.Equal(OmniCore.Infrastructure.CommandReceiptState.Completed, duplicate.State);
        Assert.Equal(CommandReceiptState.IdConflict,
            reopened.ReserveCommandReceipt(id, new string('0', 64), sessionId: "session-a").State);
        Assert.Equal("ok", reopened.ReadCommandReceipt(id)?.AckStatus);
        reopened.Close();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Crash_receipt_recovery_stress_requires_every_attempt_to_preserve_effects()
    {
        // This is a bounded crash stress test, not a retry: any failed iteration fails
        // the test, and all twelve independent journals must preserve every assertion.
        for (var attempt = 0; attempt < 12; attempt++)
            await Unread_ack_reconnect_lookup_and_unknown_reservation_never_repeat_command_effects();
    }

    [Fact]
    public async Task Unread_ack_reconnect_lookup_and_unknown_reservation_never_repeat_command_effects()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-lost-ack-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        Process? firstHost = null;
        SqliteEventStore OpenJournal(string journal, bool readOnly = false)
        {
            try { return new SqliteEventStore(journal, readOnly); }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                var files = string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(journal)!)
                    .Select(path => Path.GetFileName(path) + "=" + new FileInfo(path).Length));
                throw new InvalidOperationException($"Journal open failed: readOnly={readOnly}; "
                    + $"SQLite={error.SqliteErrorCode}/{error.SqliteExtendedErrorCode}; "
                    + $"hostExited={firstHost?.HasExited}; files={files}", error);
            }
        }
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var command = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"persist once after an unread acknowledgment\"}");
            firstHost = StartRawHost(cliDll, workspace);
            WireFrame.Write(firstHost.StandardInput.BaseStream,
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"hello-lost-ack\",\"payload\":{\"supportedVersions\":[\"1.0\"],\"client\":\"test\"}}");
            var hello = WireEnvelope.FromJson(WireFrame.Read(firstHost.StandardOutput.BaseStream)!);
            Assert.Equal("response", hello.MessageType);
            var request = WireEnvelope.Command("outer-lost-ack", "{\"operation\":\"command\",\"command\":"
                + command.ToJson() + "}");
            WireFrame.Write(firstHost.StandardInput.BaseStream, request.ToJson());

            CommandReceipt? receipt = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(journal))
                {
                    using var read = OpenJournal(journal, readOnly: true);
                    receipt = read.ReadCommandReceipt(command.MessageId);
                    if (receipt?.State == CommandReceiptState.Completed) break;
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(receipt);
            Assert.Equal(CommandReceiptState.Completed, receipt.State);
            Assert.False(firstHost.HasExited);
            var session = OmniCore.Domain.SessionId.Parse(receipt.SessionId!);
            long committedCount;
            using (var read = OpenJournal(journal, readOnly: true))
                committedCount = read.CountEvents(session);
            firstHost.Kill(entireProcessTree: true); // response was deliberately left unread by this client.
            await firstHost.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // The next writer performs WAL crash recovery. Compare its replay against
            // the count observed before the crash, rather than requiring a read-only
            // connection to recover an abruptly terminated Windows WAL.
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var unknownId = "00000000-0000-7000-8000-000000000099";
            using (var reserveUnknown = OpenJournal(journal))
                Assert.Equal(CommandReceiptState.Reserved,
                    reserveUnknown.ReserveCommandReceipt(unknownId, hash, sessionId: receipt.SessionId).State);

            using var reconnected = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            var recovered = reconnected.LookupCommandResult(command, TestContext.Current.CancellationToken);
            Assert.Equal(receipt.SessionId, recovered.SessionId);
            Assert.Equal(receipt.FirstSequence, recovered.Ack.FirstSeq);
            var duplicate = reconnected.Send(command, TestContext.Current.CancellationToken);
            Assert.Equal(recovered.Ack.FirstSeq, duplicate.FirstSeq);
            var unknown = WireEnvelope.Command(unknownId, command.PayloadJson);
            var unknownAck = reconnected.Send(unknown, TestContext.Current.CancellationToken);
            Assert.Equal("unknown", unknownAck.Status);
            Assert.Contains("command_result_unknown", unknownAck.Error, StringComparison.Ordinal);
            using var after = OpenJournal(journal, readOnly: true);
            Assert.Equal(committedCount, after.CountEvents(session));
            Assert.Equal(CommandReceiptState.Unknown, after.ReadCommandReceipt(unknownId)?.State);
        }
        finally
        {
            if (firstHost is not null)
            {
                try { if (!firstHost.HasExited) firstHost.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { if (!firstHost.HasExited) await firstHost.WaitForExitAsync(TestContext.Current.CancellationToken); }
                catch (InvalidOperationException) { }
                firstHost.Dispose();
            }
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static Process StartRawHost(string cliDll, string workspace)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(cliDll);
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--stdio");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start raw stdio Host fixture.");
    }

    [Fact]
    public async Task Trusted_stdio_actions_are_connection_bound_scoped_and_receipt_idempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-user-action-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(dataDirectory);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        Process? host = null;
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", dataDirectory);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            var canonicalWorkspace = ProjectIdentity.CanonicalWorkspacePath(workspace);
            var trustId = OmniCore.Domain.WorkspaceId.Of(canonicalWorkspace).ToString();
            File.WriteAllText(Path.Combine(dataDirectory, "trust.yaml"), "workspaces:\n  " + trustId
                + ":\n    canonicalPath: " + canonicalWorkspace + "\n    trusted: true\n", new UTF8Encoding(false));
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cliDll, workspace, Convert.ToHexString(key));

            var hello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"trusted-hello-1\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var helloPayload = JsonDocument.Parse(hello.PayloadJson);
            var capabilities = helloPayload.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("command.session.input", capabilities); // M9-A/B generic behavior remains available.
            Assert.Contains("command.session.input.user", capabilities);
            Assert.Contains("query.humanActionScope", capabilities);
            var nonce = helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;

            var scopeResponse = RawExchange(host, WireEnvelope.Command("scope-query-1",
                "{\"operation\":\"query\",\"name\":\"humanActionScope\"}"));
            using var scopePayload = JsonDocument.Parse(scopeResponse.PayloadJson);
            var initialScope = JsonDocument.Parse(scopePayload.RootElement.GetProperty("json").GetString()!);
            Assert.Equal(JsonValueKind.Null, initialScope.RootElement.GetProperty("sessionId").ValueKind);
            Assert.Equal(JsonValueKind.Null, initialScope.RootElement.GetProperty("runId").ValueKind);

            const string consentJson = "{\"cmd\":\"external.memory.consent\",\"decision\":\"allow\",\"scope\":\"global\"}";
            var noProof = Ack(RawExchange(host, WireEnvelope.Command("memory-consent-no-proof",
                "{\"operation\":\"command\",\"command\":" + WireEnvelope.Command("memory-consent-command", consentJson).ToJson() + "}")));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, noProof.Outcome?.Kind);
            Assert.Contains("trusted_user_action_required", noProof.Error, StringComparison.Ordinal);
            const string invalidScopeJson = "{\"cmd\":\"external.memory.consent\",\"decision\":\"allow\",\"scope\":\"other\"}";
            var invalidConsentScope = Ack(RawExchange(host, TrustedActionEnvelope(
                WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), invalidScopeJson), key, nonce, workspace, null, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, invalidConsentScope.Outcome?.Kind);
            Assert.Contains("trusted_action_invalid", invalidConsentScope.Error, StringComparison.Ordinal);

            var globalAllowed = Ack(RawExchange(host, TrustedActionEnvelope(
                WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), consentJson), key, nonce, workspace, null, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, globalAllowed.Outcome?.Kind);
            Assert.Equal((true, false), ReadAgentMemoryConsent(host));
            const string projectAllowedJson = "{\"cmd\":\"external.memory.consent\",\"decision\":\"allow\",\"scope\":\"project\"}";
            var projectAllowed = Ack(RawExchange(host, TrustedActionEnvelope(
                WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), projectAllowedJson), key, nonce, workspace, null, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, projectAllowed.Outcome?.Kind);
            Assert.Equal((true, true), ReadAgentMemoryConsent(host));
            const string projectRevokedJson = "{\"cmd\":\"external.memory.consent\",\"decision\":\"revoke\",\"scope\":\"project\"}";
            var projectRevoked = Ack(RawExchange(host, TrustedActionEnvelope(
                WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), projectRevokedJson), key, nonce, workspace, null, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, projectRevoked.Outcome?.Kind);
            Assert.Equal((true, false), ReadAgentMemoryConsent(host));
            const string globalRevokedJson = "{\"cmd\":\"external.memory.consent\",\"decision\":\"revoke\",\"scope\":\"global\"}";
            var globalRevoked = Ack(RawExchange(host, TrustedActionEnvelope(
                WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), globalRevokedJson), key, nonce, workspace, null, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, globalRevoked.Outcome?.Kind);
            Assert.Equal((false, false), ReadAgentMemoryConsent(host));

            const string guardedInputJson = "{\"cmd\":\"session.input\",\"text\":\"no debe pasar sin firma valida\",\"mode\":\"plan\",\"expectedRevision\":null}";
            var forgedCommand = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), guardedInputJson);
            var forgedOuter = TrustedActionEnvelope(forgedCommand, key, nonce, workspace, null, null);
            var signature = JsonDocument.Parse(forgedOuter.PayloadJson).RootElement.GetProperty("actionProof").GetProperty("signature").GetString()!;
            var tamperedSignature = signature[..^1] + (signature[^1] == '0' ? "1" : "0");
            var invalidSignature = Ack(RawExchange(host, WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                forgedOuter.PayloadJson.Replace(signature, tamperedSignature, StringComparison.Ordinal))));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, invalidSignature.Outcome?.Kind);
            Assert.Contains("trusted_action_invalid", invalidSignature.Error, StringComparison.Ordinal);

            var wrongWorkspace = Ack(RawExchange(host, TrustedActionEnvelope(
                WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), guardedInputJson), key, nonce,
                workspace + "-alias", null, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, wrongWorkspace.Outcome?.Kind);
            Assert.Contains("trusted_action_invalid", wrongWorkspace.Error, StringComparison.Ordinal);
            var afterInvalidScope = RawExchange(host, WireEnvelope.Command("scope-after-invalid-proof",
                "{\"operation\":\"query\",\"name\":\"humanActionScope\"}"));
            using (var afterInvalidPayload = JsonDocument.Parse(afterInvalidScope.PayloadJson))
            using (var afterInvalid = JsonDocument.Parse(afterInvalidPayload.RootElement.GetProperty("json").GetString()!))
            {
                Assert.Equal(JsonValueKind.Null, afterInvalid.RootElement.GetProperty("sessionId").ValueKind);
                Assert.Equal(JsonValueKind.Null, afterInvalid.RootElement.GetProperty("runId").ValueKind);
            }

            const string inputJson = "{\"cmd\":\"session.input\",\"text\":\"Entrada humana sin inferencia\",\"mode\":\"plan\",\"expectedRevision\":null}";
            var command = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), inputJson);
            var validOuter = TrustedActionEnvelope(command, key, nonce, workspace, null, null);
            var accepted = Ack(RawExchange(host, validOuter));
            Assert.True(accepted.Status == "ok", accepted.Error);
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);
            var changedPayload = WireEnvelope.Command(command.MessageId,
                  "{\"cmd\":\"session.input\",\"text\":\"alterado\",\"mode\":\"plan\",\"expectedRevision\":null}");
            var changedIdResult = Ack(RawExchange(host, TrustedActionEnvelope(
                changedPayload, key, nonce, workspace, null, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, changedIdResult.Outcome?.Kind);
            Assert.Contains("command_id_conflict", changedIdResult.Error, StringComparison.Ordinal);

            // A repeated exact proof can only return the original durable receipt, even though
            // the first input created a Session and the current Host pointer is no longer null.
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var createdSession = OmniCore.Domain.SessionId.Parse(ReadCurrentSessionId(host));
            long eventCount;
            using (var read = new SqliteEventStore(journal, readOnly: true))
                eventCount = read.CountEvents(createdSession);
            var repeated = Ack(RawExchange(host, validOuter));
            Assert.Equal(accepted.FirstSeq, repeated.FirstSeq);
            using (var read = new SqliteEventStore(journal, readOnly: true))
                Assert.Equal(eventCount, read.CountEvents(createdSession));

            var runScope = ReadCurrentScope(host);
            var selectMode = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"run.mode.select\",\"runId\":\"" + runScope.RunId
                + "\",\"mode\":\"act\",\"expectedRevision\":"
                + runScope.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            var modeProof = TrustedActionEnvelope(selectMode, key, nonce, workspace, runScope.SessionId, runScope.RunId);
            var modeAck = Ack(RawExchange(host, modeProof));
            Assert.True(modeAck.Outcome?.Kind == OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                $"mode selection rejected: status={modeAck.Status}, error={modeAck.Error}, expectedRevision={runScope.Revision}");
            Assert.Equal(modeAck.FirstSeq, Ack(RawExchange(host, modeProof)).FirstSeq); // Receipt, never a second mode effect.

            var unrelatedRunId = OmniCore.Protocol.Ids.NewV7();
            var wrongRunCommand = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"run.interrupt\",\"runId\":\"" + unrelatedRunId + "\"}");
            long beforeWrongRun;
            using (var read = new SqliteEventStore(journal, readOnly: true)) beforeWrongRun = read.CountEvents(OmniCore.Domain.SessionId.Parse(runScope.SessionId));
            var wrongRunAck = Ack(RawExchange(host, TrustedActionEnvelope(wrongRunCommand, key, nonce,
                workspace, runScope.SessionId, unrelatedRunId)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, wrongRunAck.Outcome?.Kind);
            Assert.Contains("trusted_action_scope_changed", wrongRunAck.Error, StringComparison.Ordinal);
            using (var read = new SqliteEventStore(journal, readOnly: true))
                Assert.Equal(beforeWrongRun, read.CountEvents(OmniCore.Domain.SessionId.Parse(runScope.SessionId)));

            // A UI preflight can race another queued mode change. The signed input carries
            // the revision it reviewed; the Host revalidates it under writer ownership.
            var inputFromStalePlan = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"no debe registrarse en Act\",\"mode\":\"plan\",\"expectedRevision\":"
                + runScope.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            long beforeStaleInput;
            using (var read = new SqliteEventStore(journal, readOnly: true)) beforeStaleInput = read.CountEvents(OmniCore.Domain.SessionId.Parse(runScope.SessionId));
            var staleInputAck = Ack(RawExchange(host, TrustedActionEnvelope(inputFromStalePlan, key, nonce,
                workspace, runScope.SessionId, runScope.RunId)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, staleInputAck.Outcome?.Kind);
            Assert.Contains("trusted_action_scope_changed", staleInputAck.Error, StringComparison.Ordinal);
            using (var read = new SqliteEventStore(journal, readOnly: true))
                Assert.Equal(beforeStaleInput, read.CountEvents(OmniCore.Domain.SessionId.Parse(runScope.SessionId)));
            var staleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputFromStalePlan.PayloadJson)));
            var staleLookup = RawExchange(host, WireEnvelope.Command("lookup-stale-input",
                "{\"operation\":\"command.result\",\"commandId\":\"" + inputFromStalePlan.MessageId
                + "\",\"requestHash\":\"" + staleHash + "\"}"));
            using (var staleResult = JsonDocument.Parse(staleLookup.PayloadJson))
            {
                Assert.Equal("completed", staleResult.RootElement.GetProperty("state").GetString());
                Assert.Equal("Rejected", staleResult.RootElement.GetProperty("ack").GetProperty("outcome").GetString());
                Assert.Equal(JsonValueKind.Null, staleResult.RootElement.GetProperty("ack").GetProperty("firstSeq").ValueKind);
                Assert.Equal(JsonValueKind.Null, staleResult.RootElement.GetProperty("ack").GetProperty("lastSeq").ValueKind);
            }

            var cancel = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"run.cancel\",\"runId\":\"" + runScope.RunId + "\"}");
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(cancel, key, nonce, workspace,
                    runScope.SessionId, runScope.RunId))).Outcome?.Kind);
            var nextInput = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                  "{\"cmd\":\"session.input\",\"text\":\"segunda entrada\",\"mode\":\"plan\",\"expectedRevision\":null}");
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(nextInput, key, nonce, workspace,
                    runScope.SessionId, null))).Outcome?.Kind);
            var nextRunScope = ReadCurrentScope(host);

            var interrupt = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"run.interrupt\",\"runId\":\"" + nextRunScope.RunId + "\"}");
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(interrupt, key, nonce, workspace,
                    nextRunScope.SessionId, nextRunScope.RunId))).Outcome?.Kind);

            // Reusing the old proof on a fresh connection fails because its nonce is different.
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(TestContext.Current.CancellationToken);
            host.Dispose();
            host = StartRawHost(cliDll, workspace, Convert.ToHexString(key));
            var newHello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"trusted-hello-2\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var newHelloPayload = JsonDocument.Parse(newHello.PayloadJson);
            var newNonce = newHelloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;
            Assert.NotEqual(nonce, newNonce);
            var oldProof = TrustedActionEnvelope(command, key, nonce, workspace, null, null);
            var oldProofResponse = Ack(RawExchange(host, oldProof));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, oldProofResponse.Outcome?.Kind);
        }
        finally
        {
            if (host is not null)
            {
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { if (!host.HasExited) await host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
                host.Dispose();
            }
            CryptographicOperations.ZeroMemory(key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Session_security_default_set_completes_and_replays_its_stdio_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-session-security-receipt-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var dataDirectory = Path.Combine(root, "data");
        var configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(dataDirectory);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        Process? host = null;
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", dataDirectory);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", configDirectory);
            Environment.CurrentDirectory = workspace;
            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var hello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"security-default-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var helloPayload = JsonDocument.Parse(hello.PayloadJson);
            var nonce = helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;

            var initialResponse = RawExchange(host, WireEnvelope.Command("security-default-before",
                "{\"operation\":\"query\",\"name\":\"sessionSecurityDefault\"}"));
            using var initialPayload = JsonDocument.Parse(initialResponse.PayloadJson);
            using var initialDefault = JsonDocument.Parse(initialPayload.RootElement.GetProperty("json").GetString()!);
            var originalRevision = initialDefault.RootElement.GetProperty("revision").GetString()!;
            Assert.Equal("ObserveOnly", initialDefault.RootElement.GetProperty("category").GetString());

            var command = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.security.default.set\",\"category\":\"PatchOnly\",\"expectedRevision\":\""
                + originalRevision + "\"}");
            var signed = TrustedActionEnvelope(command, key, nonce, workspace, null, null);
            var accepted = Ack(RawExchange(host, signed));
            Assert.Equal("ok", accepted.Status);
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, accepted.Outcome?.Kind);

            var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var lookupResponse = RawExchange(host, WireEnvelope.Command("security-default-lookup",
                "{\"operation\":\"command.result\",\"commandId\":\"" + command.MessageId
                + "\",\"requestHash\":\"" + requestHash + "\"}"));
            using var lookup = JsonDocument.Parse(lookupResponse.PayloadJson);
            Assert.Equal("completed", lookup.RootElement.GetProperty("state").GetString());
            Assert.Equal("Accepted", lookup.RootElement.GetProperty("ack").GetProperty("outcome").GetString());

            var replayed = Ack(RawExchange(host, signed));
            Assert.Equal(accepted.Status, replayed.Status);
            Assert.Equal(accepted.Outcome?.Kind, replayed.Outcome?.Kind);
            var updatedResponse = RawExchange(host, WireEnvelope.Command("security-default-after",
                "{\"operation\":\"query\",\"name\":\"sessionSecurityDefault\"}"));
            using var updatedPayload = JsonDocument.Parse(updatedResponse.PayloadJson);
            using var updatedDefault = JsonDocument.Parse(updatedPayload.RootElement.GetProperty("json").GetString()!);
            Assert.Equal("PatchOnly", updatedDefault.RootElement.GetProperty("category").GetString());
            Assert.NotEqual(originalRevision, updatedDefault.RootElement.GetProperty("revision").GetString());
        }
        finally
        {
            if (host is not null)
            {
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
                host.Dispose();
            }
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stdio_rejects_image_before_admission_when_effective_model_is_text_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-modality-preflight-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(config);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        Process? host = null;
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllText(Path.Combine(config, "providers.yaml"), """
                providers:
                  fixture:
                    family: OpenAIResponses
                    profile: api
                    baseUrl: http://127.0.0.1:9/v1
                    auth: none
                    billingMode: Local
                """);
            File.WriteAllText(Path.Combine(config, "models.yaml"), """
                models:
                  text-only:
                    provider: fixture
                    context: 4096
                    maxOutput: 128
                    inputModalities: [text]
                """);
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: text-only\n");
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.CurrentDirectory = workspace;

            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cliDll, workspace, Convert.ToHexString(key));
            var hello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"preflight-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var helloPayload = JsonDocument.Parse(hello.PayloadJson);
            var nonce = helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;
            var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/z/kAAAAASUVORK5CYII=");
            var uploadId = Guid.NewGuid().ToString("D");
            var imageHash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            var begin = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"attachment.upload.begin\",\"uploadId\":" + JsonSerializer.Serialize(uploadId)
                + ",\"fileName\":\"fixture.png\",\"size\":" + image.Length
                + ",\"sha256\":" + JsonSerializer.Serialize(imageHash) + "}");
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(begin, key, nonce, workspace, null, null))).Outcome?.Kind);
            var chunk = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"attachment.upload.chunk\",\"uploadId\":" + JsonSerializer.Serialize(uploadId)
                + ",\"index\":0,\"data\":" + JsonSerializer.Serialize(Convert.ToBase64String(image)) + "}");
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(chunk, key, nonce, workspace, null, null))).Outcome?.Kind);
            var finish = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"attachment.upload.finish\",\"uploadId\":" + JsonSerializer.Serialize(uploadId) + "}");
            var uploaded = RawExchange(host, TrustedActionEnvelope(finish, key, nonce, workspace, null, null));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, Ack(uploaded).Outcome?.Kind);
            using var uploadedPayload = JsonDocument.Parse(uploaded.PayloadJson);
            var defaultRevision = ReadSessionSecurityDefaultRevision(host);
            var start = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"conversation.start\",\"text\":\"inspect this image\",\"mode\":\"plan\","
                + "\"expectedRevision\":null,\"expectedPolicyRevision\":null,\"expectedDefaultRevision\":"
                + JsonSerializer.Serialize(defaultRevision) + ",\"attachments\":["
                + uploadedPayload.RootElement.GetProperty("attachment").GetRawText() + "]}");
            var rejected = Ack(RawExchange(host,
                TrustedActionEnvelope(start, key, nonce, workspace, null, null)));

            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, rejected.Outcome?.Kind);
            Assert.Equal("image_input_not_supported", rejected.Error);
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            Assert.True(File.Exists(journal));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = journal,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM events";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            if (host is not null)
            {
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { if (!host.HasExited) await host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
                host.Dispose();
            }
            CryptographicOperations.ZeroMemory(key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Conversation_admission_is_separate_from_result_and_cancel_is_serialized_with_runtime()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-conversation-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var config = Path.Combine(root, "config");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(config);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        Process? host = null;
        var key = RandomNumberGenerator.GetBytes(32);
        await using var provider = new BlockingHttpProvider();
        try
        {
            File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                providers:
                  scripted:
                    family: OpenAIResponses
                    profile: api
                    baseUrl: {{provider.BaseUrl}}
                    auth: none
                    billingMode: Local
                """);
            File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  scripted-model:\n    provider: scripted\n    context: 4096\n    maxOutput: 256\n    inputModalities: [text, image]\n");
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n");
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.CurrentDirectory = workspace;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cliDll, workspace, Convert.ToHexString(key));
            var hello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"conv-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var helloPayload = JsonDocument.Parse(hello.PayloadJson);
            Assert.Contains("command.conversation.start.user", helloPayload.RootElement.GetProperty("capabilities")
                .EnumerateArray().Select(item => item.GetString()));

            // Exercise the same signed, chunked CAS upload that the Electron main process uses.
            // The body is a tiny PNG header fixture; this test validates transport/admission,
            // not image semantics or provider quality.
            var nonce = helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;
            var image = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 0, 1, 0, 0, 0, 1 };
            var uploadId = Guid.NewGuid().ToString("D");
            var imageHash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            var beginUpload = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"attachment.upload.begin\",\"uploadId\":\"" + uploadId
                + "\",\"fileName\":\"fixture.png\",\"size\":" + image.Length
                + ",\"sha256\":\"" + imageHash + "\"}");
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(beginUpload, key, nonce, workspace, null, null))).Outcome?.Kind);
            var chunkUpload = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"attachment.upload.chunk\",\"uploadId\":\"" + uploadId
                + "\",\"index\":0,\"data\":\"" + Convert.ToBase64String(image) + "\"}");
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted,
                Ack(RawExchange(host, TrustedActionEnvelope(chunkUpload, key, nonce, workspace, null, null))).Outcome?.Kind);
            var finishUpload = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"attachment.upload.finish\",\"uploadId\":\"" + uploadId + "\"}");
            var finished = RawExchange(host, TrustedActionEnvelope(finishUpload, key, nonce, workspace, null, null));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, Ack(finished).Outcome?.Kind);
            using var uploadedPayload = JsonDocument.Parse(finished.PayloadJson);
            Assert.Equal((int)OmniCore.Domain.ArtifactKind.Other,
                uploadedPayload.RootElement.GetProperty("attachment").GetProperty("kind").GetInt32());
            Assert.Equal("fixture.png", uploadedPayload.RootElement.GetProperty("attachment").GetProperty("fileName").GetString());
            var attachmentJson = uploadedPayload.RootElement.GetProperty("attachment").GetRawText();

            var defaultRevision = ReadSessionSecurityDefaultRevision(host);
            var command = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), "{\"cmd\":\"conversation.start\","
                + "\"text\":\"único envío al proveedor de prueba\",\"attachments\":[" + attachmentJson
                + "],\"mode\":\"plan\",\"expectedRevision\":null,\"expectedPolicyRevision\":null,\"expectedDefaultRevision\":"
                + JsonSerializer.Serialize(defaultRevision) + "}");
            var signed = TrustedActionEnvelope(command, key, nonce, workspace, null, null);
            var admission = Ack(RawExchange(host, signed));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, admission.Outcome?.Kind);
            Assert.NotNull(admission.FirstSeq);
            Assert.NotNull(admission.LastSeq);

            await provider.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var codecs = EventCodecs.Create();
            var scope = ReadCurrentScope(host);
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            using (var liveStore = new SqliteEventStore(journal, readOnly: true))
            {
                var liveInput = liveStore.ReadFrom(OmniCore.Domain.SessionId.Parse(scope.SessionId), 1)
                    .Select(codecs.Decode).OfType<OmniCore.Domain.UserInputReceived>().Single();
                Assert.Equal("fixture.png", Assert.Single(liveInput.Attachments!).FileName);
            }

            // Read the admitted binary back through the real stdio artifact.read route.
            // The size comes from its canonical journal reference, never from the reader.
            var validRead = RawExchange(host, WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"operation\":\"artifact.read\",\"sessionId\":" + JsonSerializer.Serialize(scope.SessionId)
                + ",\"hash\":" + JsonSerializer.Serialize(imageHash) + ",\"offset\":0,\"length\":" + image.Length + "}"));
            Assert.Equal(OmniCore.Protocol.MessageTypes.Response, validRead.MessageType);
            using (var readPayload = JsonDocument.Parse(validRead.PayloadJson))
            {
                Assert.Equal(image.Length, readPayload.RootElement.GetProperty("size").GetInt32());
                Assert.Equal(image, Convert.FromBase64String(readPayload.RootElement.GetProperty("data").GetString()!));
            }
            var foreignHashRead = RawExchange(host, WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"operation\":\"artifact.read\",\"sessionId\":" + JsonSerializer.Serialize(scope.SessionId)
                + ",\"hash\":\"" + new string('0', 64) + "\",\"offset\":0,\"length\":1}"));
            Assert.Equal(OmniCore.Protocol.MessageTypes.Error, foreignHashRead.MessageType);
            var foreignSessionRead = RawExchange(host, WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"operation\":\"artifact.read\",\"sessionId\":\"00000000-0000-7000-8000-000000000099\",\"hash\":"
                + JsonSerializer.Serialize(imageHash) + ",\"offset\":0,\"length\":1}"));
            Assert.Equal(OmniCore.Protocol.MessageTypes.Error, foreignSessionRead.MessageType);
            Assert.True(provider.RequestBody.Contains("input_image", StringComparison.Ordinal), provider.RequestBody);
            var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
            var running = ConversationResult(host, command.MessageId, requestHash);
            Assert.Equal("running", running.GetProperty("state").GetString());

            var duplicate = Ack(RawExchange(host, signed));
            Assert.Equal(admission.FirstSeq, duplicate.FirstSeq);
            Assert.Equal(admission.LastSeq, duplicate.LastSeq);
            Assert.Equal(1, provider.RequestCount);

            // The process remains responsive to queries while provider I/O is blocked.
            var state = RawExchange(host, WireEnvelope.Command("state-during-conversation",
                "{\"operation\":\"query\",\"name\":\"state\"}"));
            using (var statePayload = JsonDocument.Parse(state.PayloadJson))
                Assert.Equal("state", statePayload.RootElement.GetProperty("name").GetString());

            var cancel = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"run.cancel\",\"runId\":" + JsonSerializer.Serialize(scope.RunId) + "}");
            var cancelAck = Ack(RawExchange(host, TrustedActionEnvelope(cancel, key,
                helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!, workspace,
                scope.SessionId, scope.RunId)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, cancelAck.Outcome?.Kind);
            Assert.Equal("cancelled", ConversationResult(host, command.MessageId, requestHash).GetProperty("state").GetString());
            Assert.Equal(1, provider.RequestCount);

            var setPolicy = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"session.security.set\",\"sessionId\":"
                + JsonSerializer.Serialize(scope.SessionId)
                + ",\"category\":\"ObserveOnly\",\"expectedRevision\":1}");
            var policyAck = Ack(RawExchange(host, TrustedActionEnvelope(setPolicy, key,
                helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!, workspace,
                scope.SessionId, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, policyAck.Outcome?.Kind);
            long countBeforeStale;
            using (var beforeStale = new SqliteEventStore(journal, readOnly: true))
                countBeforeStale = beforeStale.CountEvents(OmniCore.Domain.SessionId.Parse(scope.SessionId));

            var staleConversation = WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(),
                "{\"cmd\":\"conversation.start\",\"text\":\"keep this draft; do not consume it\",\"mode\":\"plan\","
                + "\"expectedRevision\":null,\"expectedPolicyRevision\":1,\"expectedDefaultRevision\":null}");
            var staleAck = Ack(RawExchange(host, TrustedActionEnvelope(staleConversation, key,
                helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!, workspace,
                scope.SessionId, null)));
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Rejected, staleAck.Outcome?.Kind);
            Assert.Equal("session_security.stale_revision", staleAck.Error);
            Assert.Equal(1, provider.RequestCount);
            using (var afterStale = new SqliteEventStore(journal, readOnly: true))
            {
                Assert.Equal(countBeforeStale, afterStale.CountEvents(OmniCore.Domain.SessionId.Parse(scope.SessionId)));
                var afterEvents = afterStale.ReadFrom(OmniCore.Domain.SessionId.Parse(scope.SessionId), 1)
                    .Select(codecs.Decode).ToArray();
                Assert.Single(afterEvents.OfType<OmniCore.Domain.UserInputReceived>());
                Assert.Single(afterEvents.OfType<OmniCore.Domain.RunCreated>());
            }

            using var store = new SqliteEventStore(journal, readOnly: true);
            var events = store.ReadFrom(OmniCore.Domain.SessionId.Parse(scope.SessionId), 1)
                .Select(codecs.Decode).ToArray();
            var admittedInput = Assert.Single(events.OfType<OmniCore.Domain.UserInputReceived>());
            Assert.Equal("fixture.png", Assert.Single(admittedInput.Attachments!).FileName);
            Assert.Single(events.OfType<OmniCore.Domain.RunCancelled>());
        }
        finally
        {
            if (host is not null)
            {
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { if (!host.HasExited) await host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
                host.Dispose();
            }
            CryptographicOperations.ZeroMemory(key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonElement ConversationResult(Process host, string commandId, string requestHash)
    {
        var payload = "{\"operation\":\"conversation.result\",\"commandId\":"
            + JsonSerializer.Serialize(commandId) + ",\"requestHash\":" + JsonSerializer.Serialize(requestHash) + "}";
        var response = RawExchange(host, WireEnvelope.Command("conversation-result-" + Guid.NewGuid().ToString("N"), payload));
        using var document = JsonDocument.Parse(response.PayloadJson);
        return document.RootElement.Clone();
    }

    private static Process StartRawHost(string cliDll, string workspace, string? bootstrap)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (bootstrap is not null) start.Environment["OMNICORE_TRUSTED_ACTION_BOOTSTRAP"] = bootstrap;
        start.ArgumentList.Add(cliDll);
        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--stdio");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start raw stdio Host fixture.");
    }

    private static WireEnvelope RawExchange(Process host, WireEnvelope request)
    {
        WireFrame.Write(host.StandardInput.BaseStream, request.ToJson());
        var frame = WireFrame.Read(host.StandardOutput.BaseStream)
            ?? throw new EndOfStreamException("Host closed before its response.");
        var response = WireEnvelope.FromJson(frame);
        Assert.Equal(request.MessageId, response.MessageId);
        return response;
    }

    private static string ReadCurrentSessionId(Process host)
    {
        var response = RawExchange(host, WireEnvelope.Command("scope-query-" + Guid.NewGuid().ToString("N"),
            "{\"operation\":\"query\",\"name\":\"humanActionScope\"}"));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        using var scope = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        return scope.RootElement.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Session scope missing.");
    }

    private static string ReadSessionSecurityDefaultRevision(Process host)
    {
        var response = RawExchange(host, WireEnvelope.Command("security-default-query-" + Guid.NewGuid().ToString("N"),
            "{\"operation\":\"query\",\"name\":\"sessionSecurityDefault\"}"));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        using var snapshot = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        return snapshot.RootElement.GetProperty("revision").GetString()
            ?? throw new InvalidOperationException("Session security default revision missing.");
    }

    private static (bool Global, bool Project) ReadAgentMemoryConsent(Process host)
    {
        var response = RawExchange(host, WireEnvelope.Command("agent-sources-" + Guid.NewGuid().ToString("N"),
            "{\"operation\":\"query\",\"name\":\"agent.sources\"}"));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        using var inventory = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        return (inventory.RootElement.GetProperty("externalMemoryAllowed").GetBoolean(),
            inventory.RootElement.GetProperty("externalProjectMemoryAllowed").GetBoolean());
    }

    private static (string SessionId, string RunId, long Revision) ReadCurrentScope(Process host)
    {
        var response = RawExchange(host, WireEnvelope.Command("scope-query-" + Guid.NewGuid().ToString("N"),
            "{\"operation\":\"query\",\"name\":\"humanActionScope\"}"));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        using var scope = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        return (scope.RootElement.GetProperty("sessionId").GetString()!,
            scope.RootElement.GetProperty("runId").GetString()!, scope.RootElement.GetProperty("revision").GetInt64());
    }

    private static WireEnvelope TrustedActionEnvelope(WireEnvelope command, byte[] key, string nonce,
        string workspace, string? expectedSessionId, string? expectedRunId)
    {
        var canonical = Path.GetFullPath(workspace).Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows()) canonical = canonical.ToLowerInvariant();
        var workspaceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var signed = string.Join("\n", "omnicore-user-action-v1", nonce, command.MessageId, payloadHash,
            workspaceHash, expectedSessionId ?? "-", expectedRunId ?? "-");
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signed)));
        var sessionJson = expectedSessionId is null ? "null" : "\"" + expectedSessionId + "\"";
        var runJson = expectedRunId is null ? "null" : "\"" + expectedRunId + "\"";
        var payload = "{\"operation\":\"command\",\"command\":" + command.ToJson()
            + ",\"actionProof\":{\"nonce\":\"" + nonce + "\",\"expectedSessionId\":" + sessionJson
            + ",\"expectedRunId\":" + runJson + ",\"signature\":\"" + signature + "\"}}";
        return WireEnvelope.Command(OmniCore.Protocol.Ids.NewV7(), payload);
    }

    private static CommandAck Ack(WireEnvelope response)
    {
        using var document = JsonDocument.Parse(response.PayloadJson);
        var ack = document.RootElement.GetProperty("ack");
        var kind = ack.GetProperty("outcome").GetProperty("kind").GetString();
        return new CommandAck(ack.GetProperty("commandId").GetString()!, ack.GetProperty("status").GetString()!,
            ack.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null,
            kind switch
            {
                "Accepted" => RuntimeCommandOutcome.Accepted(),
                "Rejected" => RuntimeCommandOutcome.Rejected(),
                "NoOp" => RuntimeCommandOutcome.NoOp(),
                "Deferred" => RuntimeCommandOutcome.Deferred("unknown"),
                _ => null,
            },
            ack.TryGetProperty("firstSeq", out var first) && first.ValueKind == JsonValueKind.Number ? first.GetInt64() : null,
            ack.TryGetProperty("lastSeq", out var last) && last.ValueKind == JsonValueKind.Number ? last.GetInt64() : null);
    }

    [Fact]
    public async Task Reconnected_stdio_client_recovers_receipt_without_resending_and_rejects_changed_id_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-reconnect-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            var id = OmniCore.Protocol.Ids.NewV7();
            var command = WireEnvelope.Command(id, "{\"cmd\":\"session.input\",\"text\":\"persisted once, español français italiano\"}");
            CommandAck first;
            using (var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken))
                first = client.Send(command, TestContext.Current.CancellationToken);
            Assert.Equal("ok", first.Status);
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted, first.Outcome?.Kind);

            using var reconnected = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            var recovered = reconnected.LookupCommandResult(command, TestContext.Current.CancellationToken);
            var replayed = recovered.Ack;
            Assert.Equal(first.FirstSeq, replayed.FirstSeq);
            Assert.Equal(first.LastSeq, replayed.LastSeq);
            Assert.Equal(first.Outcome?.Kind, replayed.Outcome?.Kind);

            var duplicate = reconnected.Send(command, TestContext.Current.CancellationToken);
            Assert.Equal(first.FirstSeq, duplicate.FirstSeq);
            Assert.Equal(first.LastSeq, duplicate.LastSeq);

            var page = reconnected.ReadReplayPage(1, recovered.SessionId, pageSize: 2,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(page.HasGap);
            Assert.True(page.NextSequence <= 3);
            var pinnedHighWater = page.HighWater;
            var cursor = page.NextSequence;
            var pages = 1;
            while (!page.Complete)
            {
                page = reconnected.ReadReplayPage(cursor, page.SessionId, pageSize: 2,
                    highWater: pinnedHighWater, cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(page.HasGap);
                Assert.Equal(pinnedHighWater, page.HighWater);
                Assert.True(page.NextSequence > cursor);
                cursor = page.NextSequence;
                pages++;
                Assert.InRange(pages, 1, 100);
            }
            Assert.True(page.Complete);

            var modified = WireEnvelope.Command(id,
                "{\"cmd\":\"session.input\",\"text\":\"different request\"}");
            var conflict = reconnected.Send(modified, TestContext.Current.CancellationToken);
            Assert.Equal("error", conflict.Status);
            Assert.Contains("command_id_conflict", conflict.Error, StringComparison.Ordinal);
            Assert.NotNull(reconnected.Query("state", TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Wire_frame_is_bounded_and_decodes_utf8_by_bytes()
    {
        const string message = "español français italiano — 日本語";
        using var encoded = new MemoryStream();
        WireFrame.Write(encoded, "{\"text\":\"" + JsonObj.Escape(message) + "\"}");
        encoded.Position = 0;
        Assert.Contains(message, WireFrame.Read(encoded), StringComparison.Ordinal);

        using var oversized = new MemoryStream(new byte[StdioProtocol.MaximumFrameBytes + 1]);
        Assert.Throws<InvalidDataException>(() => WireFrame.Read(oversized));
        using var invalidUtf8 = new MemoryStream([0xC3, 0x28, 0x0A]);
        Assert.Throws<InvalidDataException>(() => WireFrame.Read(invalidUtf8));
        using var truncated = new MemoryStream([0x7B]);
        Assert.Throws<EndOfStreamException>(() => WireFrame.Read(truncated));
        using var empty = new MemoryStream([0x0A]);
        Assert.Throws<InvalidDataException>(() => WireFrame.Read(empty));

        using var noMultiFrame = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => WireFrame.Write(noMultiFrame, "{\"one\":1}\n{\"two\":2}"));
        Assert.Empty(noMultiFrame.ToArray());
        Assert.Throws<InvalidDataException>(() => WireFrame.Write(noMultiFrame, "{\"one\":1}\r"));
        Assert.Empty(noMultiFrame.ToArray());
        WireFrame.Write(noMultiFrame, "{\"text\":\"line1\\nline2\"}");
        Assert.Equal("{\"text\":\"line1\\nline2\"}\n", System.Text.Encoding.UTF8.GetString(noMultiFrame.ToArray()));
    }

    [Fact]
    public void Client_uses_negotiated_legacy_replay_and_rejects_unadvertised_operations()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-legacy-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
                ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
            using var client = StdioOmniClient.Start(helper, ["legacy-stdio"], root,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("events.replay", client.Capabilities);
            Assert.DoesNotContain("events.replay.page", client.Capabilities);
            Assert.Empty(client.SubscribeSince(1));
            Assert.Throws<NotSupportedException>(() => client.ReadReplayPage(1,
                cancellationToken: TestContext.Current.CancellationToken));
            Assert.Throws<NotSupportedException>(() => client.LookupCommandResult(
                WireEnvelope.Command("m9-legacy-cmd", "{\"cmd\":\"session.input\",\"text\":\"x\"}"),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Empty_replay_completes_and_utf8_budgeted_pages_retain_all_events()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-replay-budget-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            using (var emptyHost = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken))
            {
                Assert.Empty(emptyHost.SubscribeSince(1));
                var emptyPage = emptyHost.ReadReplayPage(1, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(emptyPage.Complete);
                Assert.Empty(emptyPage.Events);
                Assert.Null(emptyPage.SessionId);
            }

            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var session = OmniCore.Domain.SessionId.New();
            var codecs = EventCodecs.Create();
            using (var seed = new SqliteEventStore(journal))
            {
                var stream = new EventStream(seed, codecs, session);
                stream.Append(new OmniCore.Domain.SessionCreated(session,
                    OmniCore.Domain.WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(workspace)).ToString(),
                    workspace, OmniCore.Domain.ProfileId.New(), DateTimeOffset.UtcNow));
                var run = OmniCore.Domain.RunId.New();
                stream.Append(new OmniCore.Domain.RunCreated(run, session, "large replay", OmniCore.Domain.RunMode.Act,
                    OmniCore.Domain.ExecutionStrategy.Direct, OmniCore.Domain.FailurePolicy.BlockDependents,
                    new OmniCore.Domain.TaskBudget(null, null, null, null), OmniCore.Domain.TaskId.New(), DateTimeOffset.UtcNow));
                stream.Append(new OmniCore.Domain.RunStarted(run));
                for (var i = 0; i < 129; i++)
                    stream.Append(new OmniCore.Domain.UserInputReceived(run,
                        System.Text.Json.JsonSerializer.Serialize(new string('x', 10_000)), null));
            }

            using var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            var page = client.ReadReplayPage(1, session.ToString(), pageSize: 128,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(page.HasGap);
            Assert.Equal(132, page.HighWater);
            Assert.InRange(page.Events.Count, 1, 127); // the 128-row response exceeds the UTF-8 frame budget.
            var events = new List<WireEnvelope>(page.Events);
            var cursor = page.NextSequence;
            var pages = 1;
            while (!page.Complete)
            {
                page = client.ReadReplayPage(cursor, page.SessionId, pageSize: 128, highWater: 132,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(page.HasGap);
                Assert.Equal(132, page.HighWater);
                Assert.True(page.NextSequence > cursor);
                events.AddRange(page.Events);
                cursor = page.NextSequence;
                Assert.InRange(++pages, 1, 10);
            }
            Assert.Equal(131, events.Count);
            Assert.Equal(131, events.Select(item => item.MessageId).Distinct(StringComparer.Ordinal).Count());

        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Malformed_requests_return_canonical_errors_and_host_continues_without_dispatching_them()
    {
        using var requests = new MemoryStream();
        WireFrame.Write(requests, "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"h1\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}");
        var malformed = new[]
        {
            ("q-missing", "{\"operation\":\"query\"}"),
            ("q-type", "{\"operation\":\"query\",\"name\":3}"),
            ("s-missing", "{\"operation\":\"subscribe\"}"),
            ("s-range", "{\"operation\":\"subscribe\",\"fromSequence\":9223372036854775808}"),
            ("c-missing", "{\"operation\":\"command\"}"),
        };
        foreach (var (id, payload) in malformed)
            WireFrame.Write(requests, WireEnvelope.Command(id, payload).ToJson());
        WireFrame.Write(requests, WireEnvelope.Command("q-valid", "{\"operation\":\"query\",\"name\":\"state\"}").ToJson());
        requests.Position = 0;
        using var output = new MemoryStream();
        using var diagnostics = new StringWriter();
        var client = new RecordingClient();
        Assert.Equal(0, StdioProtocolHost.Run(client, requests, output, diagnostics,
            TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, diagnostics.ToString());
        Assert.Equal(1, client.QueryCount);
        Assert.Equal(0, client.SendCount);
        Assert.Equal(0, client.SubscribeCount);

        output.Position = 0;
        Assert.Equal(MessageTypes.Response, WireEnvelope.FromJson(WireFrame.Read(output)!).MessageType);
        foreach (var _ in malformed)
        {
            var error = WireEnvelope.FromJson(WireFrame.Read(output)!);
            Assert.Equal(MessageTypes.Error, error.MessageType);
            Assert.Contains("unsupported_request", error.PayloadJson, StringComparison.Ordinal);
        }
        var valid = WireEnvelope.FromJson(WireFrame.Read(output)!);
        Assert.Equal(MessageTypes.Response, valid.MessageType);
        Assert.Contains("state", valid.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handshake_timeout_and_cancellation_kill_the_owned_child_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-stall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "child.pid");
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows()
            ? "OmniCore.SandboxTestProcess.exe" : "OmniCore.SandboxTestProcess");
        try
        {
            var timedOut = Assert.Throws<TimeoutException>(() => StdioOmniClient.Start(executable,
                ["stdio-stall", pidFile], root, TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            Assert.Contains("negotiation", timedOut.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(pidFile));
            AssertChildExited(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));

            File.Delete(pidFile);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(2));
#pragma warning disable xUnit1051 // The linked token also carries the test cancellation token.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => StdioOmniClient.Start(
                executable, ["stdio-stall", pidFile], root,
                TimeSpan.FromSeconds(5), cancellation.Token)));
#pragma warning restore xUnit1051
            Assert.True(File.Exists(pidFile));
            AssertChildExited(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Versioned_golden_frames_are_valid_and_match_the_capability_contract()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol", "stdio", "1.0", "goldens.jsonl");
        var frames = File.ReadAllLines(path);
        Assert.Equal(12, frames.Length);
        foreach (var frame in frames)
        {
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(frame), 1, StdioProtocol.MaximumFrameBytes);
            var envelope = WireEnvelope.FromJson(frame);
            Assert.Equal("1.0", envelope.Version);
            using var parsed = System.Text.Json.JsonDocument.Parse(frame);
            Assert.Equal("1.0", parsed.RootElement.GetProperty("version").GetString());
        }
        using var negotiated = System.Text.Json.JsonDocument.Parse(frames[1]);
        var capabilities = negotiated.RootElement.GetProperty("payload").GetProperty("capabilities")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("query.state", capabilities);
        Assert.Contains("events.stream.v1", capabilities);
        Assert.DoesNotContain("query.toolCatalog", capabilities);
        Assert.DoesNotContain("query.nativeCatalog", capabilities);
        Assert.Equal(capabilities.Length, capabilities.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(StdioProtocol.NativeQueries.Count, StdioProtocol.NativeQueries.Select(query => query.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(StdioProtocol.SupportedCapabilities.Length, StdioProtocol.SupportedCapabilities.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(StdioProtocol.TrustedUserActionCapabilities.Length, StdioProtocol.TrustedUserActionCapabilities.Distinct(StringComparer.Ordinal).Count());
        Assert.All(StdioProtocol.NativeCommands, command =>
            Assert.True(StdioProtocol.IsSupportedCommand(command.WireName ?? command.Name)));
        var questionnaire = Assert.Single(StdioProtocol.NativeCommands,
            command => command.Name == "interaction.respond");
        Assert.Equal("interaction.respond", questionnaire.WireName);
        Assert.Equal("questionnaire", questionnaire.Variant);
        Assert.Equal("command.conversation.questionnaire.respond.user", questionnaire.Capability);
        var choice = Assert.Single(StdioProtocol.NativeCommands,
            command => command.Name == "interaction.respond.choice");
        Assert.Equal("interaction.respond", choice.WireName);
        Assert.Equal("choice", choice.Variant);
        Assert.Equal("command.conversation.interaction.choice.respond.user", choice.Capability);
        Assert.Equal("native-confirmation+hmac+session-run-interaction-option+policy-revision+receipt+resume",
            choice.Authority);
    }

    [Fact]
    public void Oversized_replay_is_rejected_without_emitting_an_oversized_response()
    {
        using var requests = new MemoryStream();
        WireFrame.Write(requests, "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"h1\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}");
        WireFrame.Write(requests, WireEnvelope.Command("r1", "{\"operation\":\"subscribe\",\"fromSequence\":1}").ToJson());
        requests.Position = 0;
        using var response = new MemoryStream();
        using var diagnostics = new StringWriter();
        var exitCode = StdioProtocolHost.Run(new HugeReplayClient(), requests, response, diagnostics,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        Assert.InRange(response.Length, 1, StdioProtocol.MaximumFrameBytes * 2L + 2);
        response.Position = 0;
        var hello = WireEnvelope.FromJson(WireFrame.Read(response)!);
        var replay = WireEnvelope.FromJson(WireFrame.Read(response)!);
        Assert.Equal(MessageTypes.Response, hello.MessageType);
        Assert.Equal(MessageTypes.Error, replay.MessageType);
        Assert.Contains("unsupported_request", replay.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_native_query_is_bounded_and_does_not_close_the_connection()
    {
        using var requests = new MemoryStream();
        WireFrame.Write(requests, "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"h1\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}");
        WireFrame.Write(requests, WireEnvelope.Command("q1", "{\"operation\":\"query\",\"name\":\"state\"}").ToJson());
        WireFrame.Write(requests, WireEnvelope.Command("q2", "{\"operation\":\"query\",\"name\":\"state\"}").ToJson());
        requests.Position = 0;
        using var response = new MemoryStream();
        using var diagnostics = new StringWriter();
        var exitCode = StdioProtocolHost.Run(new OversizedQueryClient(), requests, response, diagnostics,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, exitCode);
        Assert.InRange(response.Length, 1, StdioProtocol.MaximumFrameBytes * 2L + 2);
        response.Position = 0;
        _ = WireEnvelope.FromJson(WireFrame.Read(response)!);
        var oversized = WireEnvelope.FromJson(WireFrame.Read(response)!);
        var recovered = WireEnvelope.FromJson(WireFrame.Read(response)!);
        Assert.Equal(MessageTypes.Error, oversized.MessageType);
        Assert.Contains("frame_too_large", oversized.PayloadJson, StringComparison.Ordinal);
        Assert.Equal(MessageTypes.Response, recovered.MessageType);
        Assert.Contains("ready", recovered.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Public_cli_stdio_input_and_query_use_a_persistent_child_host()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-stdio-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        var oldWorkingDirectory = Environment.CurrentDirectory;
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldAgentHome = Environment.GetEnvironmentVariable("OMNICORE_AGENT_HOME");
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", Path.Combine(root, "agent-home"));

            var text = "Transport test: English, español, français, italiano, 東京";
            var input = await RunCliAsync("stdio", "input", text);
            Assert.Equal(0, input.ExitCode);
            using var ackJson = System.Text.Json.JsonDocument.Parse(input.Stdout.Split('\n')[0]);
            Assert.Equal("ok", ackJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("Accepted", ackJson.RootElement.GetProperty("outcome").GetString());
            Assert.True(ackJson.RootElement.GetProperty("firstSeq").GetInt64() > 0);
            Assert.True(ackJson.RootElement.GetProperty("lastSeq").GetInt64()
                >= ackJson.RootElement.GetProperty("firstSeq").GetInt64());
            Assert.Contains(text, input.Stdout, StringComparison.Ordinal);

            // This is a new CLI process and a new Host process. The read model and replay come
            // from the journal opened by the first child, without running a simulation.
            var query = await RunCliAsync("stdio", "query", "state");
            Assert.Equal(0, query.ExitCode);
            using var stateJson = System.Text.Json.JsonDocument.Parse(query.Stdout);
            Assert.Equal("omnicore", stateJson.RootElement.GetProperty("runtime").GetString());
            var identity = await RunCliAsync("stdio", "query", "sessionIdentity");
            Assert.Equal(0, identity.ExitCode);
            using var identityJson = System.Text.Json.JsonDocument.Parse(identity.Stdout);
            Assert.True(Guid.TryParse(identityJson.RootElement.GetProperty("sessionId").GetString(), out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", oldAgentHome);
            Environment.CurrentDirectory = oldWorkingDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Real_stdio_stream_replays_then_delivers_live_while_queries_remain_responsive_before_ack()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9h-stream-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        var oldWorkingDirectory = Environment.CurrentDirectory;
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldAgentHome = Environment.GetEnvironmentVariable("OMNICORE_AGENT_HOME");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", Path.Combine(root, "agent-home"));
            var initial = await RunCliAsync("stdio", "input", "Initial durable stream event.");
            Assert.True(initial.ExitCode == 0, $"Initial stdio input failed: {initial.Stdout}\n{initial.Stderr}");

            var identityResult = await RunCliAsync("stdio", "query", "sessionIdentity");
            Assert.Equal(0, identityResult.ExitCode);
            using var identity = JsonDocument.Parse(identityResult.Stdout);
            var sessionId = identity.RootElement.GetProperty("sessionId").GetString()!;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            using var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: cancellation.Token);
            Assert.Contains("events.stream.v1", client.Capabilities);
            var replay = client.ReadReplayPage(1, sessionId, pageSize: 32, cancellationToken: cancellation.Token);
            Assert.False(replay.HasGap);
            Assert.NotEmpty(replay.Events);
            var replayHighWater = replay.HighWater;

            var consumerEntered = new TaskCompletionSource<StdioStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseConsumer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liveEvent = new TaskCompletionSource<StdioStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var consumeTask = client.ConsumeEventStreamAsync(sessionId, 1, 1, async (item, token) =>
            {
                if (item.Sequence <= replayHighWater)
                {
                    if (consumerEntered.TrySetResult(item)) await releaseConsumer.Task.WaitAsync(token);
                }
                else liveEvent.TrySetResult(item);
            }, cancellation.Token);
            var first = await consumerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            Assert.InRange(first.Sequence, 1, replayHighWater);

            // No ACK has been sent for the first frame while the consumer is held.
            // The one stdout reader must still demultiplex a correlated query response.
            var responsiveQuery = Task.Run(() => client.Query("state", cancellation.Token), cancellation.Token);
            var state = await responsiveQuery.WaitAsync(TimeSpan.FromSeconds(3), cancellation.Token);
            Assert.NotNull(state);
            releaseConsumer.TrySetResult();

            var liveCommand = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"Event appended while the stream is live.\"}");
            var liveAck = client.Send(liveCommand, cancellation.Token);
            Assert.Equal("ok", liveAck.Status);
            var receivedLive = await liveEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            Assert.Equal(sessionId, receivedLive.SessionId);
            Assert.True(receivedLive.Sequence > replayHighWater);
            Assert.Contains("events.stream.v1", client.Capabilities);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await consumeTask);

            // Stop and bind a new stream ID on the same Host process/session.
            using var rebindCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var reboundEvent = new TaskCompletionSource<StdioStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reboundTask = client.ConsumeEventStreamAsync(sessionId, receivedLive.Sequence + 1, 4,
                (item, _) => { reboundEvent.TrySetResult(item); return ValueTask.CompletedTask; }, rebindCancellation.Token);
            var reboundCommand = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"Event received after same-Host stream rebind.\"}");
            Assert.Equal("ok", client.Send(reboundCommand, rebindCancellation.Token).Status);
            var rebound = await reboundEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), rebindCancellation.Token);
            Assert.Equal(sessionId, rebound.SessionId);
            Assert.True(rebound.Sequence > receivedLive.Sequence);
            rebindCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reboundTask);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.SetEnvironmentVariable("OMNICORE_AGENT_HOME", oldAgentHome);
            Environment.CurrentDirectory = oldWorkingDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void In_process_client_and_trusted_user_action_boundary_remain_available()
    {
        var client = OmniHost.CreateInMemoryServer();
        Assert.IsAssignableFrom<IOmniClient>(client);
        Assert.IsAssignableFrom<ITrustedUserActionClient>(client);
        Assert.Empty(client.SubscribeSince(0));
    }

    [Fact]
    public void Stdio_cli_does_not_treat_a_rejected_outcome_as_success_when_legacy_status_is_ok()
    {
        Assert.False(CliApp.IsStdioAckSuccessful(new CommandAck("c1", "ok", null,
            RuntimeCommandOutcome.Rejected())));
        Assert.True(CliApp.IsStdioAckSuccessful(new CommandAck("c2", "ok", null,
            RuntimeCommandOutcome.Accepted())));
        Assert.False(CliApp.IsStdioAckSuccessful(new CommandAck("c3", "error", null,
            RuntimeCommandOutcome.Accepted())));
    }

    [Fact]
    public async Task Stdio_cli_usage_and_host_diagnostics_are_localized_for_all_four_locales()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-locale-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        var cases = new[] { ("es", "Uso:", "inválido"), ("en", "Usage:", "Invalid"),
            ("fr", "Usage :", "invalide"), ("it", "Uso:", "non valido") };
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            foreach (var (locale, usage, diagnostic) in cases)
            {
                var help = await RunCliAsync("--locale", locale, "stdio");
                Assert.Equal(2, help.ExitCode);
                Assert.Contains(usage, help.Stdout, StringComparison.Ordinal);

                using var host = StartCli("host", "--stdio", "--locale", locale);
                await host.StandardInput.BaseStream.WriteAsync(new byte[] { 0xC3, 0x28, 0x0A },
                    TestContext.Current.CancellationToken);
                host.StandardInput.Close();
                var stdoutTask = host.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderrTask = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await WaitForExitAsync(host, TimeSpan.FromSeconds(15));
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                Assert.Equal(2, host.ExitCode);
                Assert.Equal(string.Empty, stdout);
                Assert.Contains(diagnostic, stderr, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Incompatible_protocol_version_is_rejected_by_real_host_process()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var data = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var config = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            var process = StartCli("host", "--stdio");
            using (process)
            {
                await process.StandardInput.WriteLineAsync("{\"version\":\"9.0\",\"messageType\":\"hello\",\"messageId\":\"m1\",\"payload\":{\"supportedVersions\":[\"9.0\"]}}");
                process.StandardInput.Close();
                string? response;
                try
                {
                    response = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                }
                catch { KillOwnedTestProcess(process); throw; }
                await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
                Assert.Equal(3, process.ExitCode);
                Assert.Contains("incompatible_protocol", response, StringComparison.Ordinal);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Real_host_closes_on_invalid_bounded_frame_and_eof_without_inventing_a_response()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            using (var process = StartCli("host", "--stdio", "--locale", "es"))
            {
                var oversized = new byte[StdioProtocol.MaximumFrameBytes + 1];
                await process.StandardInput.BaseStream.WriteAsync(oversized, TestContext.Current.CancellationToken);
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
                Assert.Equal(2, process.ExitCode);
                Assert.Equal(string.Empty, await stdout);
                Assert.Contains("inválido o incompleto", await stderr, StringComparison.OrdinalIgnoreCase);
            }

            using (var process = StartCli("host", "--stdio"))
            {
                process.StandardInput.Close();
                var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                await WaitForExitAsync(process, TimeSpan.FromSeconds(15));
                var stdout = await stdoutTask;
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(string.Empty, stdout);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Cancelled_request_and_disconnected_client_never_return_success()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m9-cancel-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            Environment.CurrentDirectory = workspace;
            var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            using var client = StdioOmniClient.Start("dotnet", [cliDll, "host", "--stdio"], workspace,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("human.authority", client.Capabilities);
            var forgedHumanAction = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"mode.default.set\",\"human\":true,\"mode\":\"Act\"}");
            var rejected = Assert.Throws<InvalidDataException>(() =>
                client.Send(forgedHumanAction, CancellationToken.None));
            Assert.Contains("unsupported_request", rejected.Message, StringComparison.Ordinal);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => client.Query("state", cancelled.Token));
            client.Dispose();
            Assert.Throws<ObjectDisposedException>(() => client.Query("state", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        using var process = StartCli(args);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await WaitForExitAsync(process, TimeSpan.FromSeconds(45));
        return (process.ExitCode, await stdout, await stderr);
    }

    private static Process StartCli(params string[] args)
    {
        var cliDll = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
        Assert.True(File.Exists(cliDll), "The CLI project must be built before stdio integration tests run.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(cliDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start CLI fixture process.");
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(timeout, TestContext.Current.CancellationToken);
        }
        catch
        {
            KillOwnedTestProcess(process);
            throw;
        }
    }

    private static void KillOwnedTestProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { process.WaitForExit(3000); } catch (InvalidOperationException) { }
    }

    private static void AssertChildExited(int processId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var child = Process.GetProcessById(processId);
                if (child.HasExited) return;
            }
            catch (ArgumentException) { return; }
            Thread.Sleep(25);
        }
        Assert.Fail($"Owned handshake fixture process {processId} did not exit after cancellation/timeout.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "OmniCore.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the OmniCore worktree root.");
    }

    private sealed class HugeReplayClient : IOmniClient
    {
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken) => CommandAck.Ok(command.MessageId);
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) =>
            [WireEnvelope.Event("event-1", "{\"large\":\"" + new string('x', StdioProtocol.MaximumFrameBytes) + "\"}")];
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) => null;
    }

    private sealed class OversizedQueryClient : IOmniClient
    {
        private int _queries;
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken) => CommandAck.Ok(command.MessageId);
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => [];
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _queries) == 1
                ? new SessionQueryResult(name, "{\"value\":\"" + new string('x', StdioProtocol.MaximumFrameBytes) + "\"}")
                : new SessionQueryResult(name, "{\"ready\":true}");
    }

    private sealed class RecordingClient : IOmniClient
    {
        public int QueryCount { get; private set; }
        public int SendCount { get; private set; }
        public int SubscribeCount { get; private set; }
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            SendCount++;
            return CommandAck.Ok(command.MessageId);
        }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence)
        {
            SubscribeCount++;
            return [];
        }
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
        {
            QueryCount++;
            return new SessionQueryResult(name, "{\"ready\":true}");
        }
    }

    private sealed class BlockingHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private int _requestCount;

        public BlockingHttpProvider()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }
        public string RequestBody { get; private set; } = "";
        public int RequestCount => Volatile.Read(ref _requestCount);
        public TaskCompletionSource<bool> RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = HandleAsync(client, _stop.Token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                var headerBytes = new List<byte>();
                var single = new byte[1];
                while (await stream.ReadAsync(single, cancellationToken) == 1)
                {
                    headerBytes.Add(single[0]);
                    var count = headerBytes.Count;
                    if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10
                        && headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10) break;
                }
                var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
                var contentLength = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    ?.Split(':', 2)[1].Trim();
                if (int.TryParse(contentLength, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var bodyLength) && bodyLength is >= 0 and <= 4 * 1024 * 1024)
                {
                    var body = new byte[bodyLength];
                    var offset = 0;
                    while (offset < body.Length)
                    {
                        var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
                        if (read == 0) break;
                        offset += read;
                    }
                    if (offset == body.Length) RequestBody = Encoding.UTF8.GetString(body);
                }
                Interlocked.Increment(ref _requestCount);
                RequestReceived.TrySetResult(true);
                var buffer = new byte[1024];
                try
                {
                    while (await stream.ReadAsync(buffer, cancellationToken) != 0) { }
                }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serve; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
