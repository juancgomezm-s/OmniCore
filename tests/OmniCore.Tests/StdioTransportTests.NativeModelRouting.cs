using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public async Task Native_model_selection_is_session_cas_scoped_and_catalog_does_not_expose_route_configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-native-model-selection-" + Guid.NewGuid().ToString("N"));
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
        await using var provider = new CliEndToEndTests.ScriptedHttpProvider();
        var providerRequests = new ConcurrentQueue<string>();
        provider.RespondWith((_, body) =>
        {
            providerRequests.Enqueue(body);
            return CliEndToEndTests.ResponsesTextResponse("Model-selection fixture completed this Turn.");
        });
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.CurrentDirectory = workspace;
            var providersYaml = $$"""
                providers:
                  local-fixture:
                    family: OpenAIResponses
                    profile: api
                    baseUrl: {{provider.BaseUrl}}
                    auth: none
                    billingMode: Local
                  metered-fixture:
                    family: OpenAIResponses
                    profile: api
                    baseUrl: {{provider.BaseUrl}}
                    auth: none
                    billingMode: MeteredCurrency
                """.Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(config, "providers.yaml"), providersYaml, new UTF8Encoding(false));
            var modelsPath = Path.Combine(config, "models.yaml");
            var modelsYaml = """
                models:
                  local-a: { provider: local-fixture, context: 32768, recommendedUsableContext: 24000, maxOutput: 512, parametersBillions: 100, inputModalities: [text, image] }
                  local-b: { provider: local-fixture, context: 32768, recommendedUsableContext: 24000, maxOutput: 512, parametersBillions: 100 }
                  metered-c: { provider: metered-fixture, context: 32768, recommendedUsableContext: 24000, maxOutput: 512, parametersBillions: 100, inputModalities: [text, image] }
                """;
            File.WriteAllText(modelsPath, modelsYaml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "defaultModel: local-b\n", new UTF8Encoding(false));

            var cli = Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll");
            host = StartRawHost(cli, workspace, Convert.ToHexString(key));
            var hello = RawExchange(host, WireEnvelope.FromJson(
                "{\"version\":\"1.0\",\"messageType\":\"hello\",\"messageId\":\"native-model-selection-hello\",\"payload\":{\"supportedVersions\":[\"1.0\"]}}"));
            using var helloPayload = JsonDocument.Parse(hello.PayloadJson);
            var nonce = helloPayload.RootElement.GetProperty("trustedActionNonce").GetString()!;
            var capabilities = helloPayload.RootElement.GetProperty("capabilities").EnumerateArray()
                .Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("query.modelsCatalog", capabilities);
            Assert.Contains("query.sessionModelSelection", capabilities);
            Assert.Contains("command.session.model.select.user", capabilities);
            Assert.Contains("command.session.model.route.respond.user", capabilities);

            var catalog = QueryNativeJson(host, "models.catalog");
            Assert.Equal(1, catalog.GetProperty("schemaVersion").GetInt32());
            var catalogRevision = catalog.GetProperty("catalogRevision").GetString()!;
            Assert.Matches("^[0-9A-Fa-f]{64}$", catalogRevision);
            var models = catalog.GetProperty("models").EnumerateArray().ToArray();
            Assert.Equal(new[] { "local-a", "local-b", "metered-c" }, models
                .Select(item => item.GetProperty("modelId").GetString()).Order(StringComparer.Ordinal));
            foreach (var model in models)
            {
                Assert.Contains("text", model.GetProperty("inputModalities").EnumerateArray()
                    .Select(item => item.GetString()));
                Assert.Contains("text", model.GetProperty("outputModalities").EnumerateArray()
                    .Select(item => item.GetString()));
            }
            var publicCatalog = catalog.GetRawText();
            Assert.DoesNotContain(provider.BaseUrl, publicCatalog, StringComparison.Ordinal);
            Assert.DoesNotContain("auth", publicCatalog, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", publicCatalog, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("endpoint", publicCatalog, StringComparison.OrdinalIgnoreCase);

            var firstSession = CreateNativeModelSession(host, key, nonce, workspace);
            var journalPath = Path.Combine(OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace), "journal.db");
            var securityBeforeFirstSelection = ReadSessionSecurityPolicyFromJournal(journalPath, firstSession);
            var firstSnapshot = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(firstSession, firstSnapshot.GetProperty("sessionId").GetString());
            Assert.Equal(JsonValueKind.Null, firstSnapshot.GetProperty("selection").ValueKind);
            Assert.Null(NullableJsonString(firstSnapshot.GetProperty("activeRunId")));
            Assert.Equal(0, firstSnapshot.GetProperty("selectionRevision").GetInt64());
            Assert.Equal(JsonValueKind.Null, firstSnapshot.GetProperty("pendingConsent").ValueKind);
            Assert.Equal(catalogRevision, firstSnapshot.GetProperty("catalogRevision").GetString());

            // A metadata-only catalog change invalidates a reviewed catalog token even
            // though the selected model and route IDs remain the same.
            var sequenceBeforeCatalogChange = FindNativeConversation(host, firstSession)
                .GetProperty("journalSequence").GetInt64();
            modelsYaml = modelsYaml.Replace("local-b: { provider: local-fixture, context: 32768",
                "local-b: { provider: local-fixture, context: 32769", StringComparison.Ordinal);
            File.WriteAllText(modelsPath, modelsYaml, new UTF8Encoding(false));
            var changedCatalog = QueryNativeJson(host, "models.catalog");
            var changedCatalogRevision = changedCatalog.GetProperty("catalogRevision").GetString()!;
            Assert.NotEqual(catalogRevision, changedCatalogRevision);
            var staleCatalogSelection = SelectNativeModel(host, key, nonce, workspace, firstSession,
                firstSnapshot, catalogRevision, "local-b");
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, staleCatalogSelection.Ack.Outcome?.Kind);
            Assert.Equal("Rejected", LookupNativeReceipt(host, staleCatalogSelection.Command)
                .GetProperty("ack").GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, QueryNativeJson(host, "session.model.selection")
                .GetProperty("selection").ValueKind);
            Assert.Equal(sequenceBeforeCatalogChange, FindNativeConversation(host, firstSession)
                .GetProperty("journalSequence").GetInt64());

            var localSelection = SelectNativeModel(host, key, nonce, workspace, firstSession,
                firstSnapshot, changedCatalogRevision, "local-b");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, localSelection.Ack.Outcome?.Kind);
            Assert.Equal("completed", LookupNativeReceipt(host, localSelection.Command).GetProperty("state").GetString());
            var selectedFirst = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(firstSession, selectedFirst.GetProperty("sessionId").GetString());
            Assert.Equal(1, selectedFirst.GetProperty("selectionRevision").GetInt64());
            AssertSelectionMatchesCatalog(selectedFirst.GetProperty("selection"),
                Assert.Single(models, item => item.GetProperty("modelId").GetString() == "local-b"));

            // A stale CAS cannot overwrite the first Session's selected model.
            var stale = SelectNativeModel(host, key, nonce, workspace, firstSession,
                firstSnapshot, changedCatalogRevision, "local-a");
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, stale.Ack.Outcome?.Kind);
            Assert.Equal(1, QueryNativeJson(host, "session.model.selection").GetProperty("selectionRevision").GetInt64());
            Assert.Equal("local-b", QueryNativeJson(host, "session.model.selection")
                .GetProperty("selection").GetProperty("modelId").GetString());

            // A route outside the Session's initial local-provider grant needs a durable
            // human decision before the selected binding or any provider call can change.
            var beforeDeniedRoute = QueryNativeJson(host, "session.model.selection");
            var deniedSelection = SelectNativeModel(host, key, nonce, workspace, firstSession,
                beforeDeniedRoute, changedCatalogRevision, "metered-c");
            Assert.Equal(RuntimeCommandOutcomeKind.Deferred, deniedSelection.Ack.Outcome?.Kind);
            var deferredReceipt = LookupNativeReceipt(host, deniedSelection.Command);
            Assert.Equal("completed", deferredReceipt.GetProperty("state").GetString());
            Assert.Equal("Deferred", deferredReceipt.GetProperty("ack").GetProperty("outcome").GetString());
            Assert.Equal("ModelRouteConsent", deferredReceipt.GetProperty("ack").GetProperty("reason").GetString());
            var consent = QueryNativeJson(host, "session.model.selection").GetProperty("pendingConsent");
            Assert.Equal("metered-c", consent.GetProperty("modelId").GetString());
            Assert.Equal(new[] { "allow_route", "deny" }, consent.GetProperty("optionIds").EnumerateArray()
                .Select(item => item.GetString()).Order(StringComparer.Ordinal));
            var deniedRoute = RespondToNativeRoute(host, key, nonce, workspace, firstSession,
                consent, beforeDeniedRoute, changedCatalogRevision, "deny");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, deniedRoute.Ack.Outcome?.Kind);
            var afterDeny = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(JsonValueKind.Null, afterDeny.GetProperty("pendingConsent").ValueKind);
            Assert.Equal("local-b", afterDeny.GetProperty("selection").GetProperty("modelId").GetString());
            Assert.Equal(1, afterDeny.GetProperty("selectionRevision").GetInt64());
            Assert.Equal(1, afterDeny.GetProperty("routingPolicyRevision").GetInt64());

            var beforeAllowedRoute = QueryNativeJson(host, "session.model.selection");
            var allowedSelection = SelectNativeModel(host, key, nonce, workspace, firstSession,
                beforeAllowedRoute, changedCatalogRevision, "metered-c");
            Assert.Equal(RuntimeCommandOutcomeKind.Deferred, allowedSelection.Ack.Outcome?.Kind);
            var allowedReceipt = LookupNativeReceipt(host, allowedSelection.Command);
            Assert.Equal("completed", allowedReceipt.GetProperty("state").GetString());
            Assert.Equal("Deferred", allowedReceipt.GetProperty("ack").GetProperty("outcome").GetString());
            Assert.Equal("ModelRouteConsent", allowedReceipt.GetProperty("ack").GetProperty("reason").GetString());
            var allowedConsent = QueryNativeJson(host, "session.model.selection").GetProperty("pendingConsent");
            var allowedRoute = RespondToNativeRoute(host, key, nonce, workspace, firstSession,
                allowedConsent, beforeAllowedRoute, changedCatalogRevision, "allow_route");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, allowedRoute.Ack.Outcome?.Kind);
            var afterAllow = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(JsonValueKind.Null, afterAllow.GetProperty("pendingConsent").ValueKind);
            Assert.Equal(2, afterAllow.GetProperty("selectionRevision").GetInt64());
            Assert.Equal(2, afterAllow.GetProperty("routingPolicyRevision").GetInt64());
            Assert.Equal("metered-c", afterAllow.GetProperty("selection").GetProperty("modelId").GetString());
            AssertSessionSecurityPolicyUnchanged(securityBeforeFirstSelection,
                ReadSessionSecurityPolicyFromJournal(journalPath, firstSession));
            Assert.Equal(0, provider.RequestCount); // Catalog and route selection never probe a provider.

            var secondSession = CreateNativeModelSession(host, key, nonce, workspace);
            var secondSnapshot = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(secondSession, secondSnapshot.GetProperty("sessionId").GetString());
            Assert.Equal(0, secondSnapshot.GetProperty("selectionRevision").GetInt64());
            Assert.Equal(JsonValueKind.Null, secondSnapshot.GetProperty("selection").ValueKind);

            // Between Turns in one active Run, the trusted selection is bound to that exact Run
            // and controls the actual next provider request. Changing Session B cannot alter A.
            var firstInput = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "conversation.start",
                text = "Use the Session default model",
                mode = "plan",
                expectedRevision = (long?)null,
                expectedPolicyRevision = secondSnapshot.GetProperty("sessionPolicyRevision").GetInt64(),
                expectedDefaultRevision = (string?)null,
            }));
            var firstInputAck = Ack(RawExchange(host,
                TrustedActionEnvelope(firstInput, key, nonce, workspace, secondSession, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, firstInputAck.Outcome?.Kind);
            var firstSettled = await WaitForNativeConversationSettled(host, firstInput, TestContext.Current.CancellationToken);
            Assert.Equal("completed", firstSettled.GetProperty("state").GetString());
            Assert.Equal("turn_completed", firstSettled.GetProperty("code").GetString());
            await provider.WaitForRequestAsync(1, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var runScope = ReadCurrentScope(host);
            Assert.Equal(secondSession, runScope.SessionId);
            var secondSessionJournal = journalPath;
            var run = RunId.Parse(runScope.RunId);
            await WaitForNativeModelTurnCompletion(secondSessionJournal,
                SessionId.Parse(secondSession), run, 1, TestContext.Current.CancellationToken);
            var selectionDuringRun = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(runScope.RunId, selectionDuringRun.GetProperty("activeRunId").GetString());
            var securityBeforeSameRunSelection = ReadSessionSecurityPolicyFromJournal(secondSessionJournal, secondSession);
            var sameRunSelection = SelectNativeModel(host, key, nonce, workspace, secondSession,
                selectionDuringRun, changedCatalogRevision, "local-a");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, sameRunSelection.Ack.Outcome?.Kind);
            AssertSessionSecurityPolicyUnchanged(securityBeforeSameRunSelection,
                ReadSessionSecurityPolicyFromJournal(secondSessionJournal, secondSession));
            Assert.Equal(1, provider.RequestCount);
            Assert.Equal("local-a", QueryNativeJson(host, "session.model.selection")
                .GetProperty("selection").GetProperty("modelId").GetString());

            var nextScope = ReadCurrentScope(host);
            Assert.Equal(secondSession, nextScope.SessionId);
            Assert.Equal(runScope.RunId, nextScope.RunId);
            var nextInput = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "conversation.start",
                text = "Use the newly selected model",
                mode = "plan",
                expectedRevision = nextScope.Revision,
                expectedPolicyRevision = selectionDuringRun.GetProperty("sessionPolicyRevision").GetInt64(),
                expectedDefaultRevision = (string?)null,
            }));
            var nextInputAck = Ack(RawExchange(host,
                TrustedActionEnvelope(nextInput, key, nonce, workspace, secondSession, runScope.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, nextInputAck.Outcome?.Kind);
            var nextSettled = await WaitForNativeConversationSettled(host, nextInput, TestContext.Current.CancellationToken);
            Assert.Equal("completed", nextSettled.GetProperty("state").GetString());
            Assert.Equal("turn_completed", nextSettled.GetProperty("code").GetString());
            await provider.WaitForRequestAsync(2, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            var sameRunAfterNextTurn = ReadCurrentScope(host);
            Assert.Equal(secondSession, sameRunAfterNextTurn.SessionId);
            Assert.Equal(runScope.RunId, sameRunAfterNextTurn.RunId);
            await WaitForNativeModelTurnCompletion(secondSessionJournal,
                SessionId.Parse(secondSession), run, 2, TestContext.Current.CancellationToken);
            Assert.Equal(2, provider.RequestCount);
            var requestBodies = providerRequests.ToArray();
            Assert.Equal(2, requestBodies.Length);
            Assert.Equal("local-b", ReadProviderModel(requestBodies[0]));
            Assert.Equal("local-a", ReadProviderModel(requestBodies[1]));
            Assert.Equal("metered-c", ReadSessionModelSelectionFromJournal(secondSessionJournal, firstSession).ModelId);
            Assert.Equal("local-a", ReadSessionModelSelectionFromJournal(secondSessionJournal, secondSession).ModelId);

            // A valid HMAC for Session B cannot authorize a command whose payload targets Session A.
            var currentB = QueryNativeJson(host, "session.model.selection");
            var crossSessionCommand = MakeModelSelectCommand(firstSession, "local-b",
                currentB.GetProperty("selectionRevision").GetInt64(),
                currentB.GetProperty("routingPolicyRevision").GetInt64(),
                currentB.GetProperty("sessionPolicyRevision").GetInt64(), runScope.RunId, changedCatalogRevision);
            var crossSessionAck = Ack(RawExchange(host, TrustedActionEnvelope(crossSessionCommand, key,
                nonce, workspace, secondSession, runScope.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, crossSessionAck.Outcome?.Kind);
            Assert.Equal("local-a", QueryNativeJson(host, "session.model.selection")
                .GetProperty("selection").GetProperty("modelId").GetString());

            var resumeFirst = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.resume\",\"sessionId\":"
                + JsonSerializer.Serialize(firstSession) + "}");
            var resumed = Ack(RawExchange(host,
                TrustedActionEnvelope(resumeFirst, key, nonce, workspace, secondSession, runScope.RunId)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, resumed.Outcome?.Kind);
            var reopenedFirst = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(firstSession, reopenedFirst.GetProperty("sessionId").GetString());
            Assert.Equal("metered-c", reopenedFirst.GetProperty("selection").GetProperty("modelId").GetString());
            Assert.Equal(2, reopenedFirst.GetProperty("selectionRevision").GetInt64());

            // With no Run yet, Session-scoped model binding is the authority for attachment
            // preflight. The default model is text-only; explicit vision selection permits the
            // same retained image draft without sending it until the user submits.
            var attachmentSession = CreateNativeModelSession(host, key, nonce, workspace);
            var attachmentSnapshot = QueryNativeJson(host, "session.model.selection");
            var providerRequestsBeforeImagePreflight = provider.RequestCount;
            var imageAttachment = UploadNativeModelImage(host, key, nonce, workspace);
            var beforeRejectedImageSequence = FindNativeConversation(host, attachmentSession)
                .GetProperty("journalSequence").GetInt64();
            var rejectedImageInput = MakeNativeImageConversation(attachmentSnapshot,
                imageAttachment, "Check this image using the default model");
            var rejectedImageAck = Ack(RawExchange(host, TrustedActionEnvelope(rejectedImageInput,
                key, nonce, workspace, attachmentSession, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, rejectedImageAck.Outcome?.Kind);
            Assert.Equal("image_input_not_supported", rejectedImageAck.Error);
            Assert.Equal(providerRequestsBeforeImagePreflight, provider.RequestCount);
            Assert.Equal(beforeRejectedImageSequence, FindNativeConversation(host, attachmentSession)
                .GetProperty("journalSequence").GetInt64());
            Assert.Equal(JsonValueKind.Null, QueryNativeJson(host, "session.model.selection")
                .GetProperty("activeRunId").ValueKind);
            var noRunJournalSequence = FindNativeConversation(host, attachmentSession)
                .GetProperty("journalSequence").GetInt64();

            var attachmentModelSelection = SelectNativeModel(host, key, nonce, workspace,
                attachmentSession, attachmentSnapshot, changedCatalogRevision, "local-a");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, attachmentModelSelection.Ack.Outcome?.Kind);
            Assert.Equal(providerRequestsBeforeImagePreflight, provider.RequestCount);
            var imageBoundSnapshot = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(JsonValueKind.Null, imageBoundSnapshot.GetProperty("activeRunId").ValueKind);
            Assert.Equal("local-a", imageBoundSnapshot.GetProperty("selection").GetProperty("modelId").GetString());
            Assert.Equal(noRunJournalSequence + 1, FindNativeConversation(host, attachmentSession)
                .GetProperty("journalSequence").GetInt64());
            var acceptedImageInput = MakeNativeImageConversation(imageBoundSnapshot,
                imageAttachment, "Check this image using the selected vision model");
            var acceptedImageAck = Ack(RawExchange(host, TrustedActionEnvelope(acceptedImageInput,
                key, nonce, workspace, attachmentSession, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, acceptedImageAck.Outcome?.Kind);
            var acceptedImageSettled = await WaitForNativeConversationSettled(host, acceptedImageInput,
                TestContext.Current.CancellationToken);
            Assert.Equal("completed", acceptedImageSettled.GetProperty("state").GetString());
            var providerRequestsAfterImagePreflight = providerRequestsBeforeImagePreflight + 1;
            await provider.WaitForRequestAsync(providerRequestsAfterImagePreflight, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal("local-a", ReadProviderModel(providerRequests.ToArray()[providerRequestsAfterImagePreflight - 1]));
            Assert.Equal(providerRequestsAfterImagePreflight, provider.RequestCount);

            // A selected route whose configured provider identity changes fails closed before
            // another HTTP request and leaves the durable Session security ceiling untouched.
            var resumeFirstAgain = WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"conversation.resume\",\"sessionId\":" + JsonSerializer.Serialize(firstSession) + "}");
            var scopeBeforeSecondResume = QueryNativeJson(host, "humanActionScope");
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, Ack(RawExchange(host,
                TrustedActionEnvelope(resumeFirstAgain, key, nonce, workspace,
                    NullableJsonString(scopeBeforeSecondResume.GetProperty("sessionId")),
                    NullableJsonString(scopeBeforeSecondResume.GetProperty("runId"))))).Outcome?.Kind);
            var beforeRouteMutation = QueryNativeJson(host, "session.model.selection");
            Assert.Equal(JsonValueKind.Null, beforeRouteMutation.GetProperty("activeRunId").ValueKind);
            var routeMutationJournalSequence = FindNativeConversation(host, firstSession)
                .GetProperty("journalSequence").GetInt64();
            var securityBeforeRouteMutation = ReadSessionSecurityPolicyFromJournal(secondSessionJournal, firstSession);
            var meteredProviderBlock = "metered-fixture:\n    family: OpenAIResponses\n    profile: api\n    baseUrl: " + provider.BaseUrl;
            Assert.Contains(meteredProviderBlock, providersYaml, StringComparison.Ordinal);
            var changedProvidersYaml = providersYaml.Replace(meteredProviderBlock,
                meteredProviderBlock.Replace(provider.BaseUrl,
                    provider.BaseUrl.TrimEnd('/') + "/changed-route/", StringComparison.Ordinal), StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(config, "providers.yaml"), changedProvidersYaml, new UTF8Encoding(false));
            var routeChangedStart = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
            {
                cmd = "conversation.start",
                text = "This must not reach a changed provider route",
                mode = "plan",
                expectedRevision = (long?)null,
                expectedPolicyRevision = beforeRouteMutation.GetProperty("sessionPolicyRevision").GetInt64(),
                expectedDefaultRevision = (string?)null,
            }));
            var routeChangedAck = Ack(RawExchange(host,
                TrustedActionEnvelope(routeChangedStart, key, nonce, workspace, firstSession, null)));
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, routeChangedAck.Outcome?.Kind);
            Assert.Equal("session_model_route_unavailable", routeChangedAck.Error);
            Assert.Equal(providerRequestsAfterImagePreflight, provider.RequestCount);
            Assert.Equal(routeMutationJournalSequence, FindNativeConversation(host, firstSession)
                .GetProperty("journalSequence").GetInt64());
            var securityAfterRouteMutation = ReadSessionSecurityPolicyFromJournal(secondSessionJournal, firstSession);
            AssertSessionSecurityPolicyUnchanged(securityBeforeRouteMutation, securityAfterRouteMutation);
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
    private static string CreateNativeModelSession(Process host, byte[] key, string nonce, string workspace)
    {
        var command = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}");
        var scope = QueryNativeJson(host, "humanActionScope");
        var ack = Ack(RawExchange(host, TrustedActionEnvelope(command, key, nonce, workspace,
            NullableJsonString(scope.GetProperty("sessionId")), NullableJsonString(scope.GetProperty("runId")))));
        Assert.True(ack.Outcome?.Kind == RuntimeCommandOutcomeKind.Accepted,
            $"conversation.new was rejected: error={ack.Error ?? "<none>"}; reason={ack.Outcome?.Reason ?? "<none>"}");
        Assert.Equal("completed", LookupNativeReceipt(host, command).GetProperty("state").GetString());
        var session = ReadCurrentSessionId(host);
        var snapshot = QueryNativeJson(host, "session.model.selection");
        Assert.Equal(session, snapshot.GetProperty("sessionId").GetString());
        Assert.Equal(1, snapshot.GetProperty("routingPolicyRevision").GetInt64());
        Assert.True(snapshot.GetProperty("sessionPolicyRevision").GetInt64() >= 1);
        Assert.Null(NullableJsonString(snapshot.GetProperty("activeRunId")));
        return session;
    }

    private static (WireEnvelope Command, CommandAck Ack) SelectNativeModel(Process host, byte[] key,
        string nonce, string workspace, string sessionId, JsonElement snapshot, string catalogRevision, string modelId)
    {
        var command = MakeModelSelectCommand(sessionId, modelId,
            snapshot.GetProperty("selectionRevision").GetInt64(),
            snapshot.GetProperty("routingPolicyRevision").GetInt64(),
            snapshot.GetProperty("sessionPolicyRevision").GetInt64(),
            NullableJsonString(snapshot.GetProperty("activeRunId")), catalogRevision);
        var ack = Ack(RawExchange(host, TrustedActionEnvelope(command, key, nonce, workspace,
            sessionId, NullableJsonString(snapshot.GetProperty("activeRunId")))));
        return (command, ack);
    }

    private static (WireEnvelope Command, CommandAck Ack) RespondToNativeRoute(Process host, byte[] key,
        string nonce, string workspace, string sessionId, JsonElement consent, JsonElement snapshot,
        string catalogRevision, string optionId)
    {
        var command = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "session.model.route.respond",
            sessionId,
            interactionId = consent.GetProperty("interactionId").GetString(),
            optionId,
            expectedSelectionRevision = snapshot.GetProperty("selectionRevision").GetInt64(),
            expectedRoutingPolicyRevision = snapshot.GetProperty("routingPolicyRevision").GetInt64(),
            expectedSessionPolicyRevision = snapshot.GetProperty("sessionPolicyRevision").GetInt64(),
            expectedRunId = NullableJsonString(snapshot.GetProperty("activeRunId")),
            catalogRevision,
        }));
        var ack = Ack(RawExchange(host,
            TrustedActionEnvelope(command, key, nonce, workspace, sessionId,
                NullableJsonString(snapshot.GetProperty("activeRunId")))));
        Assert.Equal("completed", LookupNativeReceipt(host, command).GetProperty("state").GetString());
        return (command, ack);
    }

    private static WireEnvelope MakeModelSelectCommand(string sessionId, string modelId,
        long selectionRevision, long routingRevision, long sessionPolicyRevision, string? runId, string catalogRevision) =>
        WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "session.model.select",
            sessionId,
            modelId,
            expectedSelectionRevision = selectionRevision,
            expectedRoutingPolicyRevision = routingRevision,
            expectedSessionPolicyRevision = sessionPolicyRevision,
            expectedRunId = runId,
            catalogRevision,
        }));

    private static string? NullableJsonString(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static string ReadProviderModel(string requestBody)
    {
        using var request = JsonDocument.Parse(requestBody);
        return request.RootElement.GetProperty("model").GetString()!;
    }

    private static string UploadNativeModelImage(Process host, byte[] key, string nonce, string workspace)
    {
        var currentScope = QueryNativeJson(host, "humanActionScope");
        var expectedSession = NullableJsonString(currentScope.GetProperty("sessionId"));
        var expectedRun = NullableJsonString(currentScope.GetProperty("runId"));
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/z/kAAAAASUVORK5CYII=");
        var uploadId = Guid.NewGuid().ToString("D");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var begin = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "attachment.upload.begin", uploadId, fileName = "selection-fixture.png", size = bytes.Length, sha256 = hash,
        }));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, Ack(RawExchange(host,
            TrustedActionEnvelope(begin, key, nonce, workspace, expectedSession, expectedRun))).Outcome?.Kind);
        var chunk = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "attachment.upload.chunk", uploadId, index = 0, data = Convert.ToBase64String(bytes),
        }));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, Ack(RawExchange(host,
            TrustedActionEnvelope(chunk, key, nonce, workspace, expectedSession, expectedRun))).Outcome?.Kind);
        var finish = WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new { cmd = "attachment.upload.finish", uploadId }));
        var uploaded = RawExchange(host, TrustedActionEnvelope(finish, key, nonce, workspace, expectedSession, expectedRun));
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, Ack(uploaded).Outcome?.Kind);
        using var payload = JsonDocument.Parse(uploaded.PayloadJson);
        return payload.RootElement.GetProperty("attachment").GetRawText();
    }

    private static WireEnvelope MakeNativeImageConversation(JsonElement snapshot, string attachmentJson, string text)
    {
        using var attachment = JsonDocument.Parse(attachmentJson);
        return WireEnvelope.Command(Ids.NewV7(), JsonSerializer.Serialize(new
        {
            cmd = "conversation.start",
            text,
            mode = "plan",
            expectedRevision = (long?)null,
            expectedPolicyRevision = snapshot.GetProperty("sessionPolicyRevision").GetInt64(),
            expectedDefaultRevision = (string?)null,
            attachments = new[] { attachment.RootElement.Clone() },
        }));
    }

    private static SessionSecurityPolicySet ReadSessionSecurityPolicyFromJournal(string journal, string sessionId)
    {
        using var store = new SqliteEventStore(journal, readOnly: true);
        var codecs = EventCodecs.Create();
        return store.ReadFrom(SessionId.Parse(sessionId), 1).Select(codecs.Decode).OfType<SessionSecurityPolicySet>()
            .LastOrDefault() ?? throw new InvalidOperationException("Session security policy is absent from its journal.");
    }

    private static void AssertSessionSecurityPolicyUnchanged(SessionSecurityPolicySet expected,
        SessionSecurityPolicySet actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static SessionModelSelection ReadSessionModelSelectionFromJournal(string journal, string sessionId)
    {
        using var store = new SqliteEventStore(journal, readOnly: true);
        var codecs = EventCodecs.Create();
        return SessionModelSelectionProjection.Read(store.ReadFrom(SessionId.Parse(sessionId), 1), codecs,
            SessionId.Parse(sessionId)) ?? throw new InvalidOperationException("Session model selection is absent from its journal.");
    }

    private static async Task<TurnId> WaitForNativeModelTurnCompletion(string journal, SessionId session,
        RunId run, int turnOrdinal, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var store = new SqliteEventStore(journal, readOnly: true))
            {
                var codecs = EventCodecs.Create();
                var events = store.ReadFrom(session, 1).Where(item => item.RunId == run)
                    .Select(item => (Envelope: item, Payload: codecs.Decode(item))).ToArray();
                var started = events.Where(item => item.Payload is TurnStarted).ToArray();
                if (started.Length >= turnOrdinal)
                {
                    var turn = started[turnOrdinal - 1].Payload is TurnStarted startedPayload
                        ? startedPayload.TurnId : throw new InvalidOperationException("TurnStarted payload mismatch.");
                    if (events.Any(item => item.Envelope.TurnId == turn && item.Payload is TurnCompleted)) return turn;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
        throw new TimeoutException($"Native model-selection fixture did not complete Turn {turnOrdinal}.");
    }

    private static async Task<JsonElement> WaitForNativeConversationSettled(Process host, WireEnvelope command,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.PayloadJson)));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ConversationResult(host, command.MessageId, hash);
            var state = result.GetProperty("state").GetString();
            if (state is "completed" or "deferred") return result;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
        throw new TimeoutException("The native model-selection conversation did not settle.");
    }

    private static void AssertSelectionMatchesCatalog(JsonElement selection, JsonElement model)
    {
        Assert.Equal(model.GetProperty("modelId").GetString(), selection.GetProperty("modelId").GetString());
        Assert.Equal(model.GetProperty("providerId").GetString(), selection.GetProperty("providerId").GetString());
        Assert.Equal(model.GetProperty("routeId").GetString(), selection.GetProperty("routeId").GetString());
        Assert.Equal(model.GetProperty("billingMode").GetString(), selection.GetProperty("billingMode").GetString());
        Assert.Matches("^[0-9A-Fa-f]{64}$", selection.GetProperty("routeIdentityHash").GetString()!);
    }

}
