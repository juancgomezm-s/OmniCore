using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class MediaReadToolTests
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p1sAAAAASUVORK5CYII=");

    [Fact]
    public void Authorized_path_is_bounded_decoded_and_published_as_a_native_image_snapshot()
    {
        var root = Temp();
        try
        {
            var file = Path.Combine(root, "pixel.png");
            File.WriteAllBytes(file, ValidPng);
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var tool = new MediaReadTool(_ => false, _ => null, new PathBoundaryValidator(),
                (media, bytes) =>
                {
                    var decoded = GeneratedImageValidator.Decode(media, bytes);
                    return (decoded.Width, decoded.Height);
                });
            var executor = Executor(root, artifacts, tool);

            var outcome = executor.ExecuteToolWithoutJournal(Call("{\"path\":\"pixel.png\"}"), false,
                CancellationToken.None);

            Assert.True(outcome.Succeeded, outcome.Summary);
            Assert.Equal((long)ValidPng.Length, outcome.Artifact!.Size);
            Assert.Equal("image/png", outcome.Artifact.MediaType);
            Assert.Equal("pixel.png", outcome.Artifact.FileName);
            Assert.Equal(ValidPng, artifacts.GetBytes(outcome.Artifact.Hash, outcome.Artifact.Size));
            Assert.Contains("1x1", outcome.Summary);
            Assert.Contains(outcome.Artifact.Hash.Value, outcome.Summary);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Corrupt_image_is_rejected_before_any_CAS_blob_is_published()
    {
        var root = Temp();
        try
        {
            var file = Path.Combine(root, "broken.png");
            File.WriteAllBytes(file, [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
                0, 0, 0, 1, 0, 0, 0, 1]);
            var cas = Path.Combine(root, "cas");
            var artifacts = new FileArtifactStore(cas);
            var tool = new MediaReadTool(_ => false, _ => null, new PathBoundaryValidator(),
                (media, bytes) =>
                {
                    var decoded = GeneratedImageValidator.Decode(media, bytes);
                    return (decoded.Width, decoded.Height);
                });
            var executor = Executor(root, artifacts, tool);

            var outcome = executor.ExecuteToolWithoutJournal(Call("{\"path\":\"broken.png\"}"), false,
                CancellationToken.None);

            Assert.False(outcome.Succeeded);
            Assert.Empty(Directory.Exists(Path.Combine(cas, "blobs"))
                ? Directory.GetFiles(Path.Combine(cas, "blobs"), "*", SearchOption.AllDirectories)
                : []);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Path_reads_deny_outside_secret_and_reparse_targets_before_CAS()
    {
        var root = Temp();
        var outside = Temp();
        try
        {
            var outsideImage = Path.Combine(outside, "outside.png");
            File.WriteAllBytes(outsideImage, ValidPng);
            File.WriteAllText(Path.Combine(root, ".env"), "synthetic fixture secret");
            var cas = Path.Combine(root, "cas");
            var artifacts = new FileArtifactStore(cas);
            var tool = new MediaReadTool(_ => false, _ => null, new PathBoundaryValidator(), CompleteDecode);
            var executor = Executor(root, artifacts, tool);

            var outsideResult = executor.ExecuteToolWithoutJournal(Call(
                "{\"path\":" + System.Text.Json.JsonSerializer.Serialize(outsideImage) + "}"), false,
                CancellationToken.None);
            var secretResult = executor.ExecuteToolWithoutJournal(Call("{\"path\":\".env\"}"), false,
                CancellationToken.None);

            Assert.False(outsideResult.Succeeded);
            Assert.False(secretResult.Succeeded);

            Assert.Empty(Directory.Exists(Path.Combine(cas, "blobs"))
                ? Directory.GetFiles(Path.Combine(cas, "blobs"), "*", SearchOption.AllDirectories)
                : []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Path_read_rejects_a_reparse_link_before_CAS_when_the_platform_can_create_one()
    {
        var root = Temp();
        var outside = Temp();
        try
        {
            var outsideImage = Path.Combine(outside, "outside.png");
            File.WriteAllBytes(outsideImage, ValidPng);
            var cas = Path.Combine(root, "cas");
            var artifacts = new FileArtifactStore(cas);
            var executor = Executor(root, artifacts,
                new MediaReadTool(_ => false, _ => null, new PathBoundaryValidator(), CompleteDecode));
            try { File.CreateSymbolicLink(Path.Combine(root, "linked.png"), outsideImage); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                or PlatformNotSupportedException)
            {
                Assert.Skip($"El host no permite crear symlinks en este entorno: {exception.GetType().Name}.");
            }

            var result = executor.ExecuteToolWithoutJournal(Call("{\"path\":\"linked.png\"}"), false,
                CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Empty(Directory.Exists(Path.Combine(cas, "blobs"))
                ? Directory.GetFiles(Path.Combine(cas, "blobs"), "*", SearchOption.AllDirectories)
                : []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Hash_read_requires_session_reference_and_matching_detected_MIME()
    {
        var root = Temp();
        try
        {
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var reference = artifacts.PutBytes(ValidPng, "image/png", ArtifactKind.Other, Sensitivity.Sensitive);
            var denied = Executor(root, artifacts, new MediaReadTool(_ => false, _ => reference,
                new PathBoundaryValidator(), CompleteDecode));
            var deniedOutcome = denied.ExecuteToolWithoutJournal(Call("{\"hash\":\"" + reference.Hash.Value + "\"}"),
                false, CancellationToken.None);
            Assert.False(deniedOutcome.Succeeded);

            var wrongMime = reference with { MediaType = "image/jpeg" };
            var allowed = Executor(root, artifacts, new MediaReadTool(hash => hash == reference.Hash.Value,
                _ => wrongMime, new PathBoundaryValidator(), CompleteDecode));
            var mismatch = allowed.ExecuteToolWithoutJournal(Call("{\"hash\":\"" + reference.Hash.Value + "\"}"),
                false, CancellationToken.None);
            Assert.False(mismatch.Succeeded);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Hash_resolver_reuses_generated_image_references_only_from_the_current_session()
    {
        var root = Temp();
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var otherSession = SessionId.New();
            var run = TestRun.Open(store, session);
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var reference = artifacts.PutBytes(ValidPng, "image/png", ArtifactKind.Other, Sensitivity.Sensitive);
            var payload = new AssistantImagesRecorded(run.RunId, run.RootLane, TurnId.New(),
                [new AssistantImageOutput(reference, 1, 1, 0, "fixture", "fixture-model")]);
            new EventStream(store, codecs, session).Append(payload);

            var resolvedAfterReplay = SessionMediaArtifactResolver.Resolve(store, () => session, codecs,
                reference.Hash.Value);

            Assert.Equal(reference, resolvedAfterReplay);
            Assert.Null(SessionMediaArtifactResolver.Resolve(store, () => otherSession, codecs, reference.Hash.Value));
            Assert.Null(SessionMediaArtifactResolver.Resolve(store, () => session, codecs, new string('a', 64)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Tool_availability_requires_effective_image_input_and_native_call_format()
    {
        var provider = new OmniCore.Models.ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1", AuthConfig.None(), false, false, supportsNativeToolCalls: true);
        var imageModel = new OmniCore.Models.ModelDefinition("fixture-model", "fixture", 8192, 6144, 512,
            inputModalities: ["text", "image"]);
        var textModel = new OmniCore.Models.ModelDefinition("text-model", "fixture", 8192, 6144, 512,
            inputModalities: ["text"]);
        var resolver = new ModelProfileResolver();
        var imageProfile = resolver.Resolve(imageModel, provider);
        var textProfile = resolver.Resolve(textModel, provider);
        var imageHarness = new HarnessPolicyResolver().Resolve(imageProfile);
        var textHarness = new HarnessPolicyResolver().Resolve(textProfile);
        var noNativeProvider = new OmniCore.Models.ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
            "http://127.0.0.1", AuthConfig.None(), false, false, supportsNativeToolCalls: false);
        var noNativeProfile = resolver.Resolve(imageModel, noNativeProvider);
        var noNativeHarness = new HarnessPolicyResolver().Resolve(noNativeProfile);

        Assert.True(MediaReadAvailability.CanExpose(provider, imageProfile, imageHarness));
        Assert.False(MediaReadAvailability.CanExpose(provider, textProfile, textHarness));
        Assert.False(MediaReadAvailability.CanExpose(noNativeProvider, noNativeProfile, noNativeHarness));
    }

    [Fact]
    public void Explorer_turn_with_production_capability_boundary_sends_native_snapshot_on_next_model_step()
    {
        var root = Temp();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "pixel.png"), ValidPng);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var mediaTool = new MediaReadTool(_ => false, _ => null, new PathBoundaryValidator(), CompleteDecode);
            var tools = HostTools.Explorer(mediaReadTool: mediaTool);
            var policy = ModelPolicyPresets.ObserveOnly();
            var effectivePolicy = new EffectiveModelPolicy(ModelPolicyKey.For("fixture", "vision-model"), 1,
                policy.Category, policy.ToolPolicy, policy.MutationPolicy, false);
            var boundary = new ModelCapabilityBoundary(effectivePolicy);
            var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), root, boundary, null,
                artifacts: artifacts);
            var provider = new ProviderDescriptor("fixture", ProviderFamily.OpenAiChatCompatible,
                "http://127.0.0.1", AuthConfig.None(), true, true, true);
            var definition = new ModelDefinition("vision-model", "fixture", 32_768, 32_768, 512,
                inputModalities: ["text", "image"]);
            var profile = new ModelProfileResolver().Resolve(definition, provider);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var requests = new List<ModelRequest>();
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                if (requests.Count == 1)
                {
                    Assert.Contains(request.Tools, item => item.Name == "media.read");
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "fixture-media-read",
                        "media.read", "{\"path\":\"pixel.png\"}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                }
                var image = Assert.Single(request.Messages.SelectMany(message => message.Content)
                    .OfType<ToolResultBlock>().SelectMany(result => result.Content).OfType<ImageBlock>());
                Assert.Equal("image/png", image.MediaType);
                Assert.Equal(ValidPng, image.Bytes);
                return new ModelResponse([new TextBlock("fixture received typed image")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, executor, tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("vision-model", "h", "t", "c", "o", "media-read-test"),
                new ModelSelection(new ModelIdValue("vision-model"), 32_768, ToolMode.Direct, null,
                    inputModalities: ["text", "image"]),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), harness, boundary);

            var result = turn.Ask("Inspect pixel.png", "system", session, run.RunId, run.RootLane, root,
                CancellationToken.None);

            Assert.True(result.StopReason == StopReason.EndTurn,
                $"stop={result.StopReason}; failure={result.FailureDiagnostic}; requests={requests.Count}; "
                + $"toolCalls={string.Join(';', result.ToolCalls.Select(call => call.ToolName + ':' + call.Succeeded + ':' + call.Summary))}");
            Assert.Equal(2, requests.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (int Width, int Height) CompleteDecode(string media, byte[] bytes)
    {
        var decoded = GeneratedImageValidator.Decode(media, bytes);
        return (decoded.Width, decoded.Height);
    }

    private static ScriptedToolExecutor Executor(string workspace, IArtifactStore artifacts, MediaReadTool tool)
    {
        var catalog = HostTools.Explorer(mediaReadTool: tool).Catalog();
        return new ScriptedToolExecutor(catalog,
            ScriptedPermissionPolicy.WithTool("media.read", PermissionDecision.Allow), workspace,
            boundary: null, audit: null, interactionResponder: null, isInteractive: false, artifacts: artifacts);
    }

    private static ValidatedToolCall Call(string args) => new(ToolCallId.New(), new ToolId("media.read"),
        "media-read-fixture", args);

    private static string Temp()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-media-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
