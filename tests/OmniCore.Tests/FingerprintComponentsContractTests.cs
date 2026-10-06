using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Engine;

namespace OmniCore.Tests;

public sealed class FingerprintComponentsContractTests
{
    private static ExecutionFingerprint Create(params FingerprintComponent[] components) =>
        new("model", "harness", "tools", "context", "overrides", "build", "policy", "tokenizer", components);

    private static FingerprintComponent Component(string name, string value = "a") =>
        new(name, "v1", ContentHash.Sha256(value));

    [Fact]
    public void Empty_components_preserve_exact_legacy_hash()
    {
        var values = new[] { "model", "harness", "tools", "tokenizer", "context", "overrides", "build", "policy" };
        var canonical = string.Concat(values.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value + ";"));
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        Assert.Equal(expected, Create().Hash());
        Assert.Equal(expected, new ExecutionFingerprint("model", "harness", "tools", "context", "overrides", "build", "policy", "tokenizer").Hash());
    }

    [Fact]
    public void Old_json_without_components_retains_hash()
    {
        const string json = """{"ModelKey":"model","HarnessPolicyHash":"harness","ToolkitHash":"tools","TokenizerHash":"tokenizer","ContextPolicyHash":"context","OverridesHash":"overrides","Build":"build","ModelPolicyHash":"policy"}""";
        var decoded = JsonSerializer.Deserialize<ExecutionFingerprint>(json)!;
        Assert.Empty(decoded.Components);
        Assert.Equal(Create().Hash(), decoded.Hash());
    }

    [Fact]
    public void Ordering_is_canonical_and_input_cannot_mutate_fingerprint()
    {
        var route = Component("model.route");
        var build = Component("runtime.build");
        var input = new[] { build, route };
        var fingerprint = Create(input);
        var expectedHash = fingerprint.Hash();
        Assert.Equal(Create(route, build).Hash(), expectedHash);
        input[0] = build with { Hash = ContentHash.Sha256("mutated") };
        Assert.Equal(expectedHash, fingerprint.Hash());
        Assert.Throws<NotSupportedException>(() => ((IList<FingerprintComponent>)fingerprint.Components)[0] = build);
    }

    [Theory]
    [InlineData("model.route")]
    [InlineData("model.profile")]
    [InlineData("context.policy")]
    [InlineData("tools.plan")]
    [InlineData("prompt.template")]
    [InlineData("agent.profile")]
    [InlineData("skills.active")]
    [InlineData("plan.revision")]
    [InlineData("runtime.build")]
    public void Each_component_changes_the_hash(string name)
    {
        Assert.NotEqual(Create().Hash(), Create(Component(name)).Hash());
        Assert.NotEqual(Create(Component(name)).Hash(), Create(Component(name, "b")).Hash());
        Assert.NotEqual(Create(Component(name)).Hash(), Create(Component(name) with { Version = "v2" }).Hash());
    }

    [Fact]
    public void Duplicate_invalid_and_null_components_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Create(Component("model.route"), Component("model.route", "b")));
        Assert.Throws<ArgumentException>(() => Create(Component(" ")));
        Assert.Throws<ArgumentException>(() => Create(Component("model.route") with { Hash = null! }));
        Assert.Throws<ArgumentException>(() => Create(new FingerprintComponent[] { null! }));
    }

    [Fact]
    public void Typed_journal_codec_preserves_components_and_hash()
    {
        var hash = ContentHash.Sha256(new string('a', 64));
        var artifact = new ArtifactRef(ArtifactId.New(), hash, 15, "application/json", ArtifactKind.Other, Sensitivity.Normal);
        var fingerprint = Create(new FingerprintComponent("model.route", "v1", hash, artifact), Component("runtime.build"));
        var codecs = EventCodecs.Create();
        var payload = new TurnStarted(TurnId.New(), LaneId.New(), fingerprint, null);
        var json = codecs.CodecFor(payload.Type()).Encode(payload);
        var envelope = DomainEvent.Create(SessionId.New(), payload.Type(), payload.SchemaVersion(),
            null, null, null, null, null, null, null, null, Array.Empty<ArtifactRef>(), json);
        var decoded = Assert.IsType<TurnStarted>(codecs.Decode(envelope));
        Assert.NotNull(decoded.Fingerprint);
        Assert.Equal(fingerprint.Components, decoded.Fingerprint.Components);
        Assert.Equal(fingerprint.Hash(), decoded.Fingerprint.Hash());
    }

    [Fact]
    public void Turn_envelope_indexes_component_content_without_a_context_snapshot()
    {
        var hash = ContentHash.Sha256(new string('b', 64));
        var artifact = new ArtifactRef(ArtifactId.New(), hash, 15, "application/json", ArtifactKind.Other, Sensitivity.Normal);
        var fingerprint = Create(new FingerprintComponent("model.route", "v1", hash, artifact));
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var run = TestRun.Open(stream, session);
        stream.Append(new TurnStarted(TurnId.New(), run.RootLane, fingerprint));
        var envelope = Assert.Single(store.ReadFrom(session, 1), evt => evt.Type.Value() == "turn.started");
        Assert.Equal(artifact, Assert.Single(envelope.ArtifactRefs));
    }
}
