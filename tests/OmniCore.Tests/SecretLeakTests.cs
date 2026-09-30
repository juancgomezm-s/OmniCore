using System.Text.Json;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class SecretLeakTests
{
    private const string Raw = "sk-super-secret-value";

    private sealed record Holder(string Name, Secret Key);

    [Fact]
    public void ToString_is_redacted()
    {
        var text = Secret.Of(Raw).ToString();
        Assert.Equal("***", text);
        Assert.DoesNotContain(Raw, text);
    }

    [Fact]
    public void Serializing_a_secret_throws()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(Secret.Of(Raw)));
    }

    [Fact]
    public void Serializing_an_object_with_a_secret_property_throws()
    {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new Holder("x", Secret.Of(Raw))));
    }

    [Fact]
    public void Interpolation_does_not_contain_the_value()
    {
        var secret = Secret.Of(Raw);
        Assert.DoesNotContain(Raw, $"{secret}");
    }

    [Fact]
    public void Secret_provider_and_credential_store_register_resolved_values()
    {
        var redactor = SecretRedactor.Shared;
        const string providerValue = "provider-resolved-secret-98765";
        const string credentialValue = "credential-loaded-secret-54321";
        var provider = new SimpleSecretProvider("OMNI_").With("provider-key", providerValue);
        var resolved = provider.GetSecret("provider-key", CancellationToken.None);
        Assert.DoesNotContain(providerValue, redactor.Redact("tool output: " + providerValue));
        Assert.Equal("***", resolved.ToString());

        var path = Path.Combine(Path.GetTempPath(), "omnicore-credential-redaction-" + Guid.NewGuid().ToString("N"),
            "credentials.ini");
        try
        {
            var credentials = new FileCredentialStore(path);
            credentials.Save("api", credentialValue, CancellationToken.None);
            Assert.Equal(credentialValue, credentials.Load("api", CancellationToken.None));
            Assert.DoesNotContain(credentialValue, redactor.Redact("provider error: " + credentialValue));
        }
        finally
        {
            try { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Known_secret_is_redacted_before_journal_artifact_context_and_log_sinks()
    {
        var redactor = SecretRedactor.Shared;
        var known = "echoed-secret-value-7642";
        _ = Secret.Of(known); // Resolución: el valor y sus encodings quedan registrados.

        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var stream = new EventStream(store, codecs, session);
        stream.Append(new ToolCallRequested(ToolCallId.New(), "fake-tool", "fake.echo",
            "{\"value\":\"" + known + "\"}"));
        Assert.DoesNotContain(known, store.ReadFrom(session, 1).Single().PayloadJson);
        Assert.Contains(SecretRedactor.Marker, store.ReadFrom(session, 1).Single().PayloadJson);

        var artifactPath = Path.Combine(Path.GetTempPath(), "omnicore-redaction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new FileArtifactStore(artifactPath, redactor);
            var artifact = artifacts.PutText(known, "text/plain", ArtifactKind.ContextSnapshot, Sensitivity.Normal);
            var persisted = artifacts.GetText(artifact.Hash)!;
            Assert.True(artifact.Redacted);
            Assert.DoesNotContain(known, persisted);
            Assert.Equal(Sha256.Hex(Encoding.UTF8.GetBytes(persisted)), artifact.Hash.Value);
            Assert.Equal(SecretRedactor.Marker, persisted);

            var contextText = new RedactionPolicy().Redact("tool result: " + known);
            Assert.DoesNotContain(known, contextText);
            Assert.DoesNotContain(known, OmniCliRuntime.RedactSensitive("log: " + known));
        }
        finally
        {
            try { if (Directory.Exists(artifactPath)) Directory.Delete(artifactPath, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Fake_tool_echo_is_redacted_before_the_next_context_and_final_result()
    {
        var redactor = SecretRedactor.Shared;
        var known = "tool-echo-secret-13579";
        _ = Secret.Of(known);
        var server = OmniHost.CreateInMemoryServer();
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"explore.start\",\"objective\":\"inspect\"}"),
            CancellationToken.None).Status);

        var catalog = new FakeCatalog().Add(new EchoSecretTool(known));
        var executor = new ScriptedToolExecutor(catalog,
            ScriptedPermissionPolicy.WithTool("fake.echo", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Plan));
        var requests = new List<ModelRequest>();
        var callId = ToolCallId.New();
        var responseCount = 0;
        var turn = new ExplorerTurn((request, _) =>
        {
            requests.Add(request);
            responseCount++;
            return responseCount == 1
                ? new ModelResponse(new ContentBlock[] { new ToolCallBlock(callId, "echo-1", "fake.echo", "{}") },
                    StopReason.ToolUse, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "", null))
                : new ModelResponse(new ContentBlock[] { new TextBlock("provider echoed " + known) },
                    StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "", null));
        }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("secret-redaction", "h", "t", "c", "o", "M2"),
            new ModelSelection(new ModelIdValue("secret-redaction"), 4096, ToolMode.Direct, null),
            server.AcquireStore(), server.AcquireCodecs(),
            new FileArtifactStore(Path.Combine(Path.GetTempPath(), "omnicore-echo-" + Guid.NewGuid().ToString("N")), redactor),
            new InMemoryAuditSink(), new RedactionPolicy());

        var result = turn.Ask("question", "instruction", server.LastSessionId()!, server.LastRunId()!,
            server.LastLaneId()!, "", CancellationToken.None);
        Assert.Equal(2, requests.Count);
        Assert.DoesNotContain(known, string.Join(" ", requests[1].Messages.SelectMany(message => message.Content)
            .OfType<TextBlock>().Select(block => block.Text)));
        Assert.DoesNotContain(known, result.FinalText ?? "");
        Assert.All(result.ToolCalls, trace => Assert.DoesNotContain(known, trace.Summary ?? ""));
        Assert.DoesNotContain(known, string.Join("\n", server.AcquireStore().ReadFrom(server.LastSessionId()!, 1)
            .Select(evt => evt.PayloadJson)));
    }

    [Fact]
    public void Known_secret_redacts_base64_and_url_encodings_including_short_values()
    {
        var redactor = new SecretRedactor();
        const string known = "known/value with spaces-123";
        redactor.RegisterSecret(known);

        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(known)),
            redactor.Redact(Convert.ToBase64String(Encoding.UTF8.GetBytes(known))));
        Assert.DoesNotContain(Uri.EscapeDataString(known), redactor.Redact(Uri.EscapeDataString(known)));

        redactor.RegisterSecret("brief");
        Assert.Equal(SecretRedactor.Marker, redactor.Redact("brief"));
        Assert.Equal(4, SecretRedactor.MinimumSecretLength);
    }

    [Theory]
    [InlineData("aB3!")]
    [InlineData("cD5@x")]
    [InlineData("eF6#yZ")]
    [InlineData("gH7$wX9")]
    public void Four_to_seven_character_secrets_are_redacted_by_journal_artifact_context_and_log_sinks(
        string secret)
    {
        var redactor = SecretRedactor.Shared;
        SecretRedactorRegistry.Register(secret);

        var store = new InMemoryEventStore();
        var session = SessionId.New();
        new EventStream(store, EventCodecs.Create(), session).Append(new ToolCallRequested(
            ToolCallId.New(), "short-secret", "fake.echo", "{\"value\":\"" + secret + "\"}"));
        Assert.DoesNotContain(secret, store.ReadFrom(session, 1).Single().PayloadJson);

        var artifactRoot = Path.Combine(Path.GetTempPath(), "omnicore-short-redaction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new FileArtifactStore(artifactRoot, redactor);
            var artifact = artifacts.PutText(secret, "text/plain", ArtifactKind.ContextSnapshot, Sensitivity.Normal);
            Assert.True(artifact.Redacted);
            Assert.Equal(SecretRedactor.Marker, artifacts.GetText(artifact.Hash));
            Assert.DoesNotContain(secret, new RedactionPolicy().Redact("context=" + secret));
            Assert.DoesNotContain(secret, OmniCliRuntime.RedactSensitive("log=" + secret));
        }
        finally
        {
            try { if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Three_character_credential_is_rejected_with_typed_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-short-credential-" + Guid.NewGuid().ToString("N"), "credentials.ini");
        try
        {
            var credentials = new FileCredentialStore(path);
            var error = Assert.Throws<SecretValueTooShortException>(() => credentials.Save("api", "abc", CancellationToken.None));
            Assert.Equal(4, error.MinimumLength);
            Assert.Equal("secrets.tooShort", error.UserMessage.Key);
            Assert.False(File.Exists(path));
            var envError = Assert.Throws<SecretValueTooShortException>(() =>
                OmniHost.ResolveApiKey(credentials, "api", "xyz", CancellationToken.None));
            Assert.Equal(4, envError.MinimumLength);
            Assert.Throws<SecretValueTooShortException>(() => Secret.Of("xyz"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { if (Directory.Exists(Path.GetDirectoryName(path)!)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
            catch (IOException) { }
        }
    }

    private sealed class EchoSecretTool : ITool
    {
        private readonly string _secret;

        public EchoSecretTool(string secret)
        {
            _secret = secret;
            Descriptor = new ToolDescriptor(new ToolId("fake.echo"), "fake echo tool",
                new InputSchema("{}"), Array.Empty<string>(), true, false, ToolRisk.Low,
                ComponentSource.Core(), ToolProtection.None, EffectClass.None);
        }

        public ToolDescriptor Descriptor { get; }

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context) =>
            new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.None, ResourceClaims.Empty(), ToolRisk.Low, null));

        public System.Threading.Tasks.Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent,
            ToolExecutionContext context, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(ToolResult.Ok(
                "echoed " + _secret, _secret, _secret.Length, false, EffectOutcome.None));
    }

    [Fact]
    public void Every_event_payload_is_redacted_by_the_journal_codec()
    {
        _ = SecretRedactor.Shared;
        var secret = "journal-canary-" + Guid.NewGuid().ToString("N");
        SecretRedactorRegistry.Register(secret);
        var input = new UserInputReceived(RunId.New(), JsonSerializer.Serialize("mi clave es " + secret), null, null);

        var encoded = EventCodecs.Create().CodecFor(input.Type()).Encode(input);

        Assert.DoesNotContain(secret, encoded, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Marker, encoded, StringComparison.Ordinal);
    }
}
