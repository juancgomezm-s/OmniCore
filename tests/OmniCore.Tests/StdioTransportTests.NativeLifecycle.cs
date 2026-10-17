using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_conversation_lifecycle_uses_hmac_receipts_sequence_cas_and_persists_policy()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-native-conversation-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var oldDirectory = Environment.CurrentDirectory;
        var key = RandomNumberGenerator.GetBytes(32);
        Process? host = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.CurrentDirectory = workspace;
            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var hello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"native-lifecycle-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var helloPayload = JsonDocument.Parse(hello.PayloadJson);
            var nonce = helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;
            var capabilities = helloPayload.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("command.conversation.new.user", capabilities);
            Assert.Contains("command.conversation.resume.user", capabilities);
            Assert.Contains("command.conversation.rename.user", capabilities);
            Assert.Contains("command.conversation.compact.user", capabilities);

            // The HMAC covers the exact {cmd} payload and binds this action to the connection.
            var create = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}");
            var createProof = TrustedActionEnvelope(create, key, nonce, workspace, null, null);
            var created = Ack(RawExchange(host, createProof));
            Assert.Equal("ok", created.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, created.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, create).GetProperty("state").GetString());
            var createReplay = Ack(RawExchange(host, createProof));
            Assert.Equal(created.FirstSeq, createReplay.FirstSeq);
            Assert.Single(ReadNativeConversations(host));

            var sessionId = ReadCurrentSessionId(host);
            var initialRow = FindNativeConversation(host, sessionId);
            Assert.True(initialRow.GetProperty("current").GetBoolean());
            Assert.True(initialRow.GetProperty("journalSequence").GetInt64() > 0);
            Assert.Equal("", initialRow.GetProperty("title").GetString());
            var initialPolicy = ReadNativeSessionSecurity(host);
            Assert.Equal(1, initialPolicy.GetProperty("revision").GetInt64());
            Assert.Equal("Native", initialPolicy.GetProperty("provenance").GetString());
            Assert.Equal("UserDefault", initialPolicy.GetProperty("source").GetString());

            // A human selection advances durable policy independently of the model label.
            var setPolicy = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.security.set\",\"sessionId\":" + JsonSerializer.Serialize(sessionId)
                + ",\"category\":\"PatchOnly\",\"expectedRevision\":1}");
            var policyAck = Ack(RawExchange(host, TrustedActionEnvelope(setPolicy, key, nonce,
                workspace, sessionId, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, policyAck.Outcome?.Kind);
            var chosenPolicy = ReadNativeSessionSecurity(host);
            Assert.Equal(2, chosenPolicy.GetProperty("revision").GetInt64());
            Assert.Equal("PatchOnly", chosenPolicy.GetProperty("category").GetString());
            Assert.Equal("UserSelection", chosenPolicy.GetProperty("source").GetString());

            // Rename is an append guarded by the exact current journal sequence.
            var sequenceBeforeRename = FindNativeConversation(host, sessionId)
                .GetProperty("journalSequence").GetInt64();
            var rename = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"conversation.rename\",\"sessionId\":" + JsonSerializer.Serialize(sessionId)
                + ",\"title\":\"Proyecto M9\",\"expectedSequence\":"
                + sequenceBeforeRename.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            var renameProof = TrustedActionEnvelope(rename, key, nonce, workspace, sessionId, null);
            var renamed = Ack(RawExchange(host, renameProof));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, renamed.Outcome?.Kind);
            Assert.NotNull(renamed.FirstSeq);
            Assert.Equal(renamed.FirstSeq, renamed.LastSeq);
            var renamedRow = FindNativeConversation(host, sessionId);
            Assert.Equal("Proyecto M9", renamedRow.GetProperty("title").GetString());
            Assert.Equal(sequenceBeforeRename + 1, renamedRow.GetProperty("journalSequence").GetInt64());

            var renameReceipt = LookupNativeReceipt(host, rename);
            Assert.Equal("completed", renameReceipt.GetProperty("state").GetString());
            var replay = Ack(RawExchange(host, renameProof));
            Assert.Equal(renamed.FirstSeq, replay.FirstSeq);
            Assert.Equal(renamed.LastSeq, replay.LastSeq);
            Assert.Equal(renamedRow.GetProperty("journalSequence").GetInt64(),
                FindNativeConversation(host, sessionId).GetProperty("journalSequence").GetInt64());

            // An older sequence cannot replace the current title and creates no event.
            var staleRename = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"conversation.rename\",\"sessionId\":" + JsonSerializer.Serialize(sessionId)
                + ",\"title\":\"Stale title\",\"expectedSequence\":"
                + sequenceBeforeRename.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            var staleAck = Ack(RawExchange(host,
                TrustedActionEnvelope(staleRename, key, nonce, workspace, sessionId, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, staleAck.Outcome?.Kind);
            Assert.Equal("Proyecto M9", FindNativeConversation(host, sessionId).GetProperty("title").GetString());
            Assert.Equal(renamedRow.GetProperty("journalSequence").GetInt64(),
                FindNativeConversation(host, sessionId).GetProperty("journalSequence").GetInt64());
            Assert.Equal("completed", LookupNativeReceipt(host, staleRename).GetProperty("state").GetString());

            // A blank Session can compact as a no-op; the result is still durably replayable.
            var compact = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.compact\"}");
            var compactProof = TrustedActionEnvelope(compact, key, nonce, workspace, sessionId, null);
            var compactAck = Ack(RawExchange(host, compactProof));
            Assert.Equal("ok", compactAck.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.NoOp, compactAck.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, compact).GetProperty("state").GetString());
            Assert.Equal(compactAck.FirstSeq, Ack(RawExchange(host, compactProof)).FirstSeq);

            await StopNativeRawHost(host);
            host = null;
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var reopenedHello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"native-lifecycle-reopen\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var reopenedPayload = JsonDocument.Parse(reopenedHello.PayloadJson);
            nonce = reopenedPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;

            var reopenedRow = FindNativeConversation(host, sessionId);
            Assert.Equal("Proyecto M9", reopenedRow.GetProperty("title").GetString());
            Assert.True(reopenedRow.GetProperty("current").GetBoolean());
            var reopenedPolicy = ReadNativeSessionSecurity(host);
            Assert.Equal(2, reopenedPolicy.GetProperty("revision").GetInt64());
            Assert.Equal("PatchOnly", reopenedPolicy.GetProperty("category").GetString());
            Assert.Equal("UserSelection", reopenedPolicy.GetProperty("source").GetString());

            // Open/resume uses exactly {cmd, sessionId}; the journal policy stays unchanged.
            var resume = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"conversation.resume\",\"sessionId\":" + JsonSerializer.Serialize(sessionId) + "}");
            var resumeProof = TrustedActionEnvelope(resume, key, nonce, workspace, sessionId, null);
            var resumed = Ack(RawExchange(host, resumeProof));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, resumed.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, resume).GetProperty("state").GetString());
            Assert.Equal(resumed.FirstSeq, Ack(RawExchange(host, resumeProof)).FirstSeq);
            Assert.Equal(sessionId, ReadCurrentSessionId(host));
            Assert.Equal(2, ReadNativeSessionSecurity(host).GetProperty("revision").GetInt64());
        }
        finally
        {
            if (host is not null)
            {
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
                host.Dispose();
            }
            CryptographicOperations.ZeroMemory(key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            Environment.CurrentDirectory = oldDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonElement QueryNativeJson(Process host, string name)
    {
        var response = RawExchange(host, WireEnvelope.Command(Ids.NewV7(),
            "{\"operation\":\"query\",\"name\":" + JsonSerializer.Serialize(name) + "}"));
        using var payload = JsonDocument.Parse(response.PayloadJson);
        using var result = JsonDocument.Parse(payload.RootElement.GetProperty("json").GetString()!);
        return result.RootElement.Clone();
    }

    private static JsonElement ReadNativeSessionSecurity(Process host) => QueryNativeJson(host, "sessionSecurity");

    private static JsonElement[] ReadNativeConversations(Process host) => QueryNativeJson(host, "conversations")
        .GetProperty("conversations").EnumerateArray().Select(row => row.Clone()).ToArray();

    private static JsonElement FindNativeConversation(Process host, string sessionId) =>
        Assert.Single(ReadNativeConversations(host), row => row.GetProperty("id").GetString() == sessionId);

    private static JsonElement LookupNativeReceipt(Process host, WireEnvelope command)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var response = RawExchange(host, WireEnvelope.Command(Ids.NewV7(),
            "{\"operation\":\"command.result\",\"commandId\":" + JsonSerializer.Serialize(command.MessageId)
            + ",\"requestHash\":" + JsonSerializer.Serialize(hash) + "}"));
        using var result = JsonDocument.Parse(response.PayloadJson);
        return result.RootElement.Clone();
    }

    private static async Task StopNativeRawHost(Process host)
    {
        try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { await host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
        host.Dispose();
    }
}
