using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class QualificationFingerprintTests
{
    private const string KeyHashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyHashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void Qualification_key_revision_and_state_change_only_profile_component()
    {
        var key = ModelQualificationKey.For("fixture-provider", "fixture-model",
            ToolCallFormat.Native, ToolMode.Direct, "http://fixture.invalid/v1", "chat", "runtime/1");
        var first = Create(Snapshot(key, KeyHashA, 4, ModelQualificationState.Qualified));
        var changedKey = Create(Snapshot(key, KeyHashB, 4, ModelQualificationState.Qualified));
        var changedRevision = Create(Snapshot(key, KeyHashA, 5, ModelQualificationState.Qualified));
        var changedState = Create(Snapshot(key, KeyHashA, 4, ModelQualificationState.Calibrated));

        Assert.Equal(first.Hash(), Create(Snapshot(key, KeyHashA, 4, ModelQualificationState.Qualified)).Hash());
        Assert.NotEqual(first.Hash(), changedKey.Hash());
        Assert.NotEqual(first.Hash(), changedRevision.Hash());
        Assert.NotEqual(first.Hash(), changedState.Hash());
        Assert.NotEqual(Part(first, "model.profile"), Part(changedKey, "model.profile"));
        Assert.NotEqual(Part(first, "model.profile"), Part(changedRevision, "model.profile"));
        Assert.NotEqual(Part(first, "model.profile"), Part(changedState, "model.profile"));

        Assert.Equal(WithoutProfile(first), WithoutProfile(changedKey));
        Assert.Equal(WithoutProfile(first), WithoutProfile(changedRevision));
        Assert.Equal(WithoutProfile(first), WithoutProfile(changedState));
    }

    [Fact]
    public void Missing_qualification_hashes_the_exact_v2_profile_with_explicit_nulls()
    {
        var component = Part(Create(qualification: null), "model.profile");
        const string canonicalJson = "{\"modelId\":\"fixture-model\",\"routeId\":\"fixture-route\",\"contextWindow\":8192,\"recommendedUsableContext\":7000,\"maxOutputTokens\":1024,\"inputModalities\":[\"text\"],\"toolCallFormats\":[\"Native\"],\"supportsParallelTools\":false,\"traits\":{\"fixture-trait\":0.5},\"qualificationKeyHash\":null,\"qualificationRevision\":null,\"qualificationState\":null}";

        Assert.Equal("2", component.Version);
        Assert.Equal(ContentHash.Sha256(Digest(canonicalJson)), component.Hash);
    }

    [Fact]
    public void Known_qualification_hashes_the_exact_v2_profile_metadata()
    {
        var key = ModelQualificationKey.For("fixture-provider", "fixture-model",
            ToolCallFormat.Native, ToolMode.Direct, "http://fixture.invalid/v1", "chat", "runtime/1");
        var component = Part(Create(Snapshot(key, KeyHashA, 7, ModelQualificationState.Calibrated)), "model.profile");
        const string canonicalJson = "{\"modelId\":\"fixture-model\",\"routeId\":\"fixture-route\",\"contextWindow\":8192,\"recommendedUsableContext\":7000,\"maxOutputTokens\":1024,\"inputModalities\":[\"text\"],\"toolCallFormats\":[\"Native\"],\"supportsParallelTools\":false,\"traits\":{\"fixture-trait\":0.5},\"qualificationKeyHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"qualificationRevision\":7,\"qualificationState\":\"Calibrated\"}";

        Assert.Equal("2", component.Version);
        Assert.Equal(ContentHash.Sha256(Digest(canonicalJson)), component.Hash);
    }

    [Fact]
    public void Absent_qualification_is_distinct_from_a_known_snapshot()
    {
        var key = ModelQualificationKey.For("fixture-provider", "fixture-model",
            ToolCallFormat.Native, ToolMode.Direct, "http://fixture.invalid/v1", "chat", "runtime/1");
        var absent = Create(qualification: null);
        var known = Create(Snapshot(key, KeyHashA, 1, ModelQualificationState.Declared));

        Assert.NotEqual(absent.Hash(), known.Hash());
        Assert.NotEqual(Part(absent, "model.profile"), Part(known, "model.profile"));
        Assert.Equal(WithoutProfile(absent), WithoutProfile(known));
    }

    private static ModelQualificationSnapshot Snapshot(ModelQualificationKey key, string keyHash,
        long revision, ModelQualificationState state) =>
        new(key, keyHash, revision, state, new Dictionary<string, double> { ["fixture-trait"] = .5 });

    private static ExecutionFingerprint Create(ModelQualificationSnapshot? qualification)
    {
        var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
        var route = new ModelRoute(model.ProviderId, "http://fixture.invalid/v1",
            ProviderFamily.OpenAiChatCompatible, null, model.Id, new RouteId("fixture-route"));
        var selection = new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct, null,
            route.Id, route);
        var profile = new EffectiveModelProfile(model.Id, 8192, 7000, 1024,
            new[] { "text" }, new[] { ToolCallFormat.Native }, false,
            new Dictionary<string, double> { ["fixture-trait"] = .5 }, route.Id);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        return RuntimeFingerprintFactory.Create(model, profile, harness, selection,
            "fixture-harness-hash", "fixture-context-hash", "fixture-policy-hash",
            "fixture-tokenizer/1", qualification: qualification);
    }

    private static FingerprintComponent Part(ExecutionFingerprint fingerprint, string name) =>
        Assert.Single(fingerprint.Components, component => component.Name == name);

    private static FingerprintComponent[] WithoutProfile(ExecutionFingerprint fingerprint) =>
        fingerprint.Components.Where(component => component.Name != "model.profile").ToArray();

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
