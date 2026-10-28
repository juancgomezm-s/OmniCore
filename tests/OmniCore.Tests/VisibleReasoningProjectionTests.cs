namespace OmniCore.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

public sealed class VisibleReasoningProjectionTests
{
    [Theory]
    [InlineData("Full", true)]
    [InlineData("Summarized", true)]
    [InlineData("Omitted", false)]
    [InlineData("future-value", false)]
    public void Only_declared_visible_reasoning_crosses_the_projection(string visibility, bool visible)
    {
        var (wire, _) = Map(visibility);
        var fields = JsonObj.Parse(wire.PayloadJson);
        Assert.DoesNotContain("opaque-secret", wire.PayloadJson);
        Assert.Equal(visible, fields.ContainsKey("reasoningText"));
        var projection = new ClientProjection(new Localization("es"));
        var state = projection.Apply(ClientState.Empty(), wire);
        Assert.Equal(visible ? 1 : 0, state.Conversation.Blocks.Count);
        if (visible)
        {
            var block = Assert.Single(state.Conversation.Blocks);
            Assert.Equal(ConversationRole.Reasoning, block.Role);
            Assert.Equal("A public summary", block.Text);
            Assert.Single(projection.Apply(state, wire).Conversation.Blocks);
        }
        var child = WireEnvelope.Event("child", wire.PayloadJson[..^1] + ",\"conversationScope\":\"child\"}");
        Assert.Empty(projection.Apply(ClientState.Empty(), child).Conversation.Blocks);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Malformed_or_unverified_artifacts_never_disclose_content(bool corrupt, bool malformed)
    {
        var (wire, _) = Map("Full", corrupt, malformed);
        Assert.DoesNotContain("reasoningText", wire.PayloadJson);
    }

    [Fact]
    public void Public_text_is_bounded_and_redacted()
    {
        var (wire, _) = Map("Full", text: "api_key=abcdef123456 " + new string('x', 20_000));
        var text = JsonObj.Parse(wire.PayloadJson)["reasoningText"]!;
        Assert.DoesNotContain("abcdef123456", text);
        Assert.True(text.Length <= 16_384);
    }

    private static (WireEnvelope Wire, MemoryArtifacts Artifacts) Map(string visibility,
        bool corrupt = false, bool malformed = false, string text = "A public summary")
    {
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "fixture");
        var stream = new EventStream(store, codecs, session);
        var artifacts = new MemoryArtifacts();
        var json = malformed ? "{}" : JsonSerializer.Serialize(new
        {
            visibleContent = new { version = 1, blocks = new[] { new { kind = "reasoning", text, visibility, opaque = "opaque-secret" } } },
            providerState = "opaque-secret"
        });
        var reference = artifacts.PutText(json, "application/vnd.omnicore.model-usage+json", ArtifactKind.ModelResponse, Sensitivity.Sensitive);
        artifacts.Valid = !corrupt;
        stream.Append(new ModelStepCompleted(TurnId.New(), 0, new(0, 0, 0, 0, 0), StopReason.EndTurn,
            reference, "2026-10-27", null));
        var wire = Assert.Single(new ProtocolMapper(codecs, artifacts).Map(store.ReadFrom(session, 1)),
            envelope => JsonObj.Parse(envelope.PayloadJson)["type"] == "model_step.completed");
        return (wire, artifacts);
    }

    private sealed class MemoryArtifacts : IArtifactStore
    {
        private string _text = "";
        public bool Valid { get; set; } = true;
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            _text = content;
            var bytes = Encoding.UTF8.GetBytes(content);
            return new(ArtifactId.New(), ContentHash.Sha256(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()), bytes.Length, mediaType, kind, sensitivity);
        }
        public string? GetText(ContentHash hash) => _text;
        public bool Verify(ContentHash hash, long expectedSize) => Valid;
    }
}
