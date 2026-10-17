using System.Collections.Concurrent;
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
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Native_questionnaire_response_resumes_the_same_turn_and_replays_its_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-stdio-questionnaire-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var config = Path.Combine(root, "config");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(config);
        var priorData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var priorConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var priorDirectory = Environment.CurrentDirectory;
        var key = RandomNumberGenerator.GetBytes(32);
        Process? host = null;
        await using var provider = new CliEndToEndTests.ScriptedHttpProvider();
        var requests = new ConcurrentQueue<string>();
        var toolCallId = "fixture-ask-" + Guid.NewGuid().ToString("N");
        const string questionArguments = "{\"title\":\"Review preference\",\"description\":null,\"questions\":[{\"id\":\"style\",\"prompt\":\"Choose a review style\",\"kind\":\"SingleChoice\",\"options\":[{\"id\":\"inline\",\"label\":\"Inline comments\"}]}]}";
        provider.RespondWith((index, body) =>
        {
            requests.Enqueue(body);
            return index == 0
                ? CliEndToEndTests.ResponsesToolCallResponse(toolCallId, "user.ask", questionArguments)
                : CliEndToEndTests.ResponsesTextResponse("Questionnaire answer acknowledged.");
        });

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
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  scripted-model:\n    provider: scripted\n    context: 8192\n    maxOutput: 512\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.CurrentDirectory = workspace;

            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var hello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"questionnaire-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var helloJson = JsonDocument.Parse(hello.PayloadJson);
            var nonce = helloJson.RootElement.GetProperty("trustedActionNonce").GetString()!;
            var defaultRevision = ReadSessionSecurityDefaultRevision(host);
            var start = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"conversation.start\",\"text\":\"Ask me one review preference\",\"mode\":\"plan\","
                + "\"expectedRevision\":null,\"expectedPolicyRevision\":null,\"expectedDefaultRevision\":"
                + JsonSerializer.Serialize(defaultRevision) + "}");
            var startAck = Ack(RawExchange(host, TrustedActionEnvelope(start, key, nonce, workspace, null, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, startAck.Outcome?.Kind);
            await provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var startHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(start.PayloadJson)));
            var deferred = await WaitForDeferredConversation(host, start.MessageId, startHash,
                TestContext.Current.CancellationToken);
            Assert.Equal("input_required", deferred.GetProperty("code").GetString());

            var scope = ReadCurrentScope(host);
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var session = SessionId.Parse(scope.SessionId);
            var run = RunId.Parse(scope.RunId);
            var (interaction, resumedTurn) = await WaitForQuestionAsync(journal, session, run, toolCallId,
                TestContext.Current.CancellationToken);
            var policyRevision = ReadNativeSessionSecurity(host).GetProperty("revision").GetInt64();
            const string answerMarker = "inline";
            var answer = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"responseType\":\"questionnaire\",\"interactionId\":"
                + JsonSerializer.Serialize(interaction.ToString()) + ",\"expectedRunId\":"
                + JsonSerializer.Serialize(scope.RunId) + ",\"expectedPolicyRevision\":"
                + policyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"answers\":[{\"questionId\":\"style\",\"selectedOptionIds\":[\"" + answerMarker
                + "\"],\"text\":null,\"otherText\":null}],\"cancelled\":false}");
            var proof = TrustedActionEnvelope(answer, key, nonce, workspace, scope.SessionId, scope.RunId);
            var responseAck = Ack(RawExchange(host, proof));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, responseAck.Outcome?.Kind);
            await provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            var duplicateAck = Ack(RawExchange(host, proof));
            Assert.Equal(responseAck.Status, duplicateAck.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, duplicateAck.Outcome?.Kind);
            Assert.Equal(responseAck.FirstSeq, duplicateAck.FirstSeq);
            Assert.Equal(responseAck.LastSeq, duplicateAck.LastSeq);
            Assert.Equal(2, provider.RequestCount);

            await WaitForTurnCompletion(journal, session, run, resumedTurn,
                TestContext.Current.CancellationToken);
            var bodies = requests.ToArray();
            Assert.Equal(2, bodies.Length);
            Assert.Contains("Ask me one review preference", bodies[0], StringComparison.Ordinal);
            Assert.Contains(answerMarker, bodies[1], StringComparison.Ordinal);
            Assert.Contains("function_call_output", bodies[1], StringComparison.Ordinal);
            using (var store = new SqliteEventStore(journal, readOnly: true))
            {
                var codecs = EventCodecs.Create();
                var journalEvents = store.ReadFrom(session, 1);
                var events = journalEvents.Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope))).ToArray();
                Assert.Single(events, item => item.Envelope.RunId == run && item.Payload is RunCreated);
                Assert.Single(events, item => item.Envelope.RunId == run && item.Envelope.TurnId == resumedTurn
                    && item.Payload is TurnStarted);
                Assert.Single(events, item => item.Envelope.RunId == run && item.Payload is InteractionResolved resolved
                    && resolved.InteractionId == interaction);
                Assert.Equal(2, events.Count(item => item.Envelope.RunId == run && item.Payload is UserInputReceived));
                Assert.Contains(events, item => item.Envelope.RunId == run && item.Envelope.TurnId == resumedTurn
                    && item.Payload is TurnCompleted);
                Assert.Single(events, item => item.Envelope.RunId == run && item.Envelope.TurnId == resumedTurn
                    && item.Payload is ToolCallRequested requested && requested.ToolName == "user.ask");
                Assert.All(events.Where(item => item.Envelope.RunId == run && item.Payload is ModelStepStarted),
                    item => Assert.Equal(resumedTurn, item.Envelope.TurnId));
                CanonicalStateTracker.Replay(codecs, journalEvents);
            }
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
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", priorConfig);
            Environment.CurrentDirectory = priorDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Native_questionnaire_response_survives_host_process_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-stdio-questionnaire-restart-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var config = Path.Combine(root, "config");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(config);
        var priorData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var priorConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var priorDirectory = Environment.CurrentDirectory;
        var key = RandomNumberGenerator.GetBytes(32);
        Process? host = null;
        await using var provider = new CliEndToEndTests.ScriptedHttpProvider();
        var toolCallId = "restart-ask-" + Guid.NewGuid().ToString("N");
        const string questionArguments = "{\"title\":\"Recovery preference\",\"questions\":[{\"id\":\"style\",\"prompt\":\"Choose a recovery style\",\"kind\":\"SingleChoice\",\"options\":[{\"id\":\"safe\",\"label\":\"Safe\"}]}]}";
        provider.RespondWith((index, _) => index == 0
            ? CliEndToEndTests.ResponsesToolCallResponse(toolCallId, "user.ask", questionArguments)
            : CliEndToEndTests.ResponsesTextResponse("Recovered questionnaire answer."));

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
                """, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  scripted-model:\n    provider: scripted\n    context: 8192\n    maxOutput: 512\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: scripted-model\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.CurrentDirectory = workspace;

            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var firstHello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"questionnaire-restart-hello-1\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var firstHelloJson = JsonDocument.Parse(firstHello.PayloadJson);
            var firstNonce = firstHelloJson.RootElement.GetProperty("trustedActionNonce").GetString()!;
            var defaultRevision = ReadSessionSecurityDefaultRevision(host);
            var start = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"conversation.start\",\"text\":\"Ask a recovery preference\",\"mode\":\"plan\","
                + "\"expectedRevision\":null,\"expectedPolicyRevision\":null,\"expectedDefaultRevision\":"
                + JsonSerializer.Serialize(defaultRevision) + "}");
            var startAck = Ack(RawExchange(host, TrustedActionEnvelope(start, key, firstNonce, workspace, null, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, startAck.Outcome?.Kind);
            await provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var startHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(start.PayloadJson)));
            var deferred = await WaitForDeferredConversation(host, start.MessageId, startHash,
                TestContext.Current.CancellationToken);
            Assert.Equal("input_required", deferred.GetProperty("code").GetString());

            var beforeRestart = ReadCurrentScope(host);
            var journal = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var session = SessionId.Parse(beforeRestart.SessionId);
            var run = RunId.Parse(beforeRestart.RunId);
            var (interaction, resumedTurn) = await WaitForQuestionAsync(journal, session, run, toolCallId,
                TestContext.Current.CancellationToken);

            // Abruptly stop the owning Host while its real interaction remains unresolved.
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(TestContext.Current.CancellationToken);
            host.Dispose();
            host = null;

            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var secondHello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"questionnaire-restart-hello-2\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var secondHelloJson = JsonDocument.Parse(secondHello.PayloadJson);
            var secondNonce = secondHelloJson.RootElement.GetProperty("trustedActionNonce").GetString()!;
            var afterRestart = ReadCurrentScope(host);
            Assert.Equal(beforeRestart.SessionId, afterRestart.SessionId);
            Assert.Equal(beforeRestart.RunId, afterRestart.RunId);
            Assert.Equal(1, provider.RequestCount);

            var policyRevision = ReadNativeSessionSecurity(host).GetProperty("revision").GetInt64();
            var answer = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"interaction.respond\",\"responseType\":\"questionnaire\",\"interactionId\":"
                + JsonSerializer.Serialize(interaction.ToString()) + ",\"expectedRunId\":"
                + JsonSerializer.Serialize(afterRestart.RunId) + ",\"expectedPolicyRevision\":"
                + policyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"answers\":[{\"questionId\":\"style\",\"selectedOptionIds\":[\"safe\"],\"text\":null,\"otherText\":null}],\"cancelled\":false}");
            var answerAck = Ack(RawExchange(host,
                TrustedActionEnvelope(answer, key, secondNonce, workspace, afterRestart.SessionId, afterRestart.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, answerAck.Outcome?.Kind);
            await provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await WaitForTurnCompletion(journal, session, run, resumedTurn, TestContext.Current.CancellationToken);
            Assert.Equal(2, provider.RequestCount);

            using var store = new SqliteEventStore(journal, readOnly: true);
            var codecs = EventCodecs.Create();
            var journalEvents = store.ReadFrom(session, 1);
            var events = journalEvents.Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope))).ToArray();
            Assert.Single(events, item => item.Payload is RunCreated created && created.RunId == run);
            Assert.Single(events, item => item.Envelope.RunId == run && item.Envelope.TurnId == resumedTurn
                && item.Payload is TurnStarted);
            Assert.Single(events, item => item.Envelope.RunId == run && item.Payload is InteractionResolved resolved
                && resolved.InteractionId == interaction);
            Assert.Contains(events, item => item.Envelope.RunId == run && item.Envelope.TurnId == resumedTurn
                && item.Payload is TurnCompleted);
            Assert.Single(events, item => item.Envelope.RunId == run && item.Payload is ToolCallRequested requested
                && requested.ToolName == "user.ask");
            CanonicalStateTracker.Replay(codecs, journalEvents);
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
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", priorConfig);
            Environment.CurrentDirectory = priorDirectory;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<(InteractionId Interaction, TurnId Turn)> WaitForQuestionAsync(string journal, SessionId session,
        RunId run, string toolCallId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var store = new SqliteEventStore(journal, readOnly: true))
            {
                var codecs = EventCodecs.Create();
                var events = store.ReadFrom(session, 1);
                var requested = events.Where(item => item.RunId == run)
                    .Select(envelope => (Envelope: envelope, Payload: codecs.Decode(envelope)))
                    .FirstOrDefault(item => item.Payload is InteractionRequested request
                        && request.Kind == InteractionKind.Question
                        && request.ToolCallJson?.Contains(toolCallId, StringComparison.Ordinal) == true);
                if (requested.Payload is InteractionRequested interaction && requested.Envelope.TurnId is { } turnId)
                    return (interaction.InteractionId, turnId);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
        throw new TimeoutException("The real stdio Host did not persist the user.ask interaction.");
    }

    private static async Task WaitForTurnCompletion(string journal, SessionId session, RunId run, TurnId turn,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var store = new SqliteEventStore(journal, readOnly: true))
            {
                var codecs = EventCodecs.Create();
                if (store.ReadFrom(session, 1).Any(envelope => envelope.RunId == run && envelope.TurnId == turn
                    && codecs.Decode(envelope) is TurnCompleted)) return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
        throw new TimeoutException("The questionnaire continuation did not complete its original Turn.");
    }

    private static async Task<JsonElement> WaitForDeferredConversation(Process host, string commandId,
        string requestHash, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ConversationResult(host, commandId, requestHash);
            if (result.GetProperty("state").GetString() == "deferred") return result;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
        throw new TimeoutException("The start receipt did not reach the deferred input_required state.");
    }
}
