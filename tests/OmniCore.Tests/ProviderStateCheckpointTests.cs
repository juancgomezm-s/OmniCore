using System.Text.Json;
using System.Text.Json.Nodes;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;

namespace OmniCore.Tests;

public sealed class ProviderStateCheckpointTests
{
    private static readonly string Model = "model-α";
    private static readonly RouteId Route = new("provider/route");
    private static readonly ModelRoute PhysicalRoute = new("provider", "https://one.example/v1",
        ProviderFamily.OpenAiChatCompatible, "profile-a", Model, Route);
    private static readonly string RouteIdentityHash = AuthorizedModelRoute.From(PhysicalRoute, BillingMode.Unknown).IdentityHash;
    private static readonly TurnId Turn = TurnId.New();
    private const int Step = 3;

    [Fact]
    public void Persists_sensitive_full_state_and_restores_exact_strings_without_descriptor_leak()
    {
        using var fixture = new Fixture();
        var state = new ProviderState("opaque.kind", "{\"unicode\":\"café 🚀\",\"lines\":\"first\\nsecond\",\"marker\":\"opaque-checkpoint-marker\"}");

        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store, state, Model, Route, Turn, Step, RouteIdentityHash)!;
        var json = JsonDocument.Parse(descriptor).RootElement;
        var reference = json.GetProperty("StateRef");
        Assert.Equal(2, json.GetProperty("Version").GetInt32());
        Assert.Equal(Model, json.GetProperty("ModelId").GetString());
        Assert.Equal(Route.Value, json.GetProperty("RouteId").GetString());
        Assert.Equal(RouteIdentityHash, json.GetProperty("RouteIdentityHash").GetString());
        Assert.Equal(Turn.ToString(), json.GetProperty("TurnId").GetString());
        Assert.Equal(Step, json.GetProperty("StepIndex").GetInt32());
        Assert.DoesNotContain("opaque-checkpoint-marker", descriptor, StringComparison.Ordinal);
        Assert.DoesNotContain(state.Kind, descriptor, StringComparison.Ordinal);

        var artifactRef = JsonSerializer.Deserialize<ArtifactRef>(reference.GetRawText())!;
        Assert.Equal(ArtifactKind.ProviderOpaqueState, artifactRef.Kind);
        Assert.Equal(Sensitivity.Sensitive, artifactRef.Sensitivity);
        Assert.False(artifactRef.Redacted);
        Assert.True(fixture.Store.Verify(artifactRef.Hash, artifactRef.Size));
        Assert.Equal(state, ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, Route, Turn, Step, RouteIdentityHash));
    }

    [Fact]
    public void Null_state_is_a_tombstone()
    {
        using var fixture = new Fixture();
        Assert.Null(ProviderStateCheckpoint.Persist(fixture.Store, null, Model, Route, Turn, Step, RouteIdentityHash));
    }

    [Fact]
    public void Redaction_of_opaque_state_fails_closed_with_constant_safe_error()
    {
        const string secret = "opaque-provider-secret-123456";
        var redactor = new SecretRedactor();
        redactor.RegisterSecret(secret);
        using var fixture = new Fixture(redactor);

        var error = Assert.Throws<InvalidDataException>(() =>
            ProviderStateCheckpoint.Persist(fixture.Store, new ProviderState("kind", secret), Model, Route, Turn, Step, RouteIdentityHash));

        Assert.Equal("Provider state checkpoint is invalid.", error.Message);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Persist_rejects_an_artifact_with_the_wrong_media_type()
    {
        using var fixture = new Fixture();
        fixture.Store.ReturnWrongMediaType = true;

        var error = Assert.Throws<InvalidDataException>(() =>
            ProviderStateCheckpoint.Persist(fixture.Store, new ProviderState("kind", "payload"), Model, Route, Turn, Step, RouteIdentityHash));

        Assert.Equal("Provider state checkpoint is invalid.", error.Message);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("route")]
    [InlineData("turn")]
    [InlineData("step")]
    public void Identity_mismatch_returns_null_before_artifact_access(string mismatch)
    {
        using var fixture = new Fixture();
        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store,
            new ProviderState("kind", "payload"), Model, Route, Turn, Step, RouteIdentityHash)!;
        fixture.Store.ResetReads();

        var result = mismatch switch
        {
            "model" => ProviderStateCheckpoint.Restore(fixture.Store, descriptor, "other-model", Route, Turn, Step, RouteIdentityHash),
            "route" => ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, new RouteId("other-route"), Turn, Step, RouteIdentityHash),
            "turn" => ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, Route, TurnId.New(), Step, RouteIdentityHash),
            _ => ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, Route, Turn, Step + 1, RouteIdentityHash),
        };

        Assert.Null(result);
        Assert.Equal(0, fixture.Store.VerifyCalls);
        Assert.Equal(0, fixture.Store.GetCalls);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown-version")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-sensitivity")]
    [InlineData("redacted")]
    [InlineData("invalid-hash")]
    [InlineData("invalid-size")]
    [InlineData("wrong-media-type")]
    public void Invalid_descriptors_fail_with_constant_safe_error(string corruption)
    {
        using var fixture = new Fixture();
        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store,
            new ProviderState("kind", "safe-marker"), Model, Route, Turn, Step, RouteIdentityHash)!;
        if (corruption == "malformed") descriptor = "{not-json";
        else
        {
            var node = JsonNode.Parse(descriptor)!.AsObject();
            if (corruption == "unknown-version") node["Version"] = 3;
            else
            {
                var reference = node["StateRef"]!.AsObject();
                if (corruption == "wrong-kind") reference["Kind"] = (int)ArtifactKind.Other;
                if (corruption == "wrong-sensitivity") reference["Sensitivity"] = (int)Sensitivity.Normal;
                if (corruption == "redacted") reference["Redacted"] = true;
                if (corruption == "invalid-hash") reference["Hash"]!["Algorithm"] = "sha1";
                if (corruption == "invalid-size") reference["Size"] = -1;
                if (corruption == "wrong-media-type") reference["MediaType"] = "application/json";
            }
            descriptor = node.ToJsonString();
        }

        var error = Assert.Throws<InvalidDataException>(() =>
            ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, Route, Turn, Step, RouteIdentityHash));
        Assert.Equal("Provider state checkpoint is invalid.", error.Message);
        Assert.DoesNotContain("safe-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_corrupt_blob_fails_closed(bool corrupt)
    {
        using var fixture = new Fixture();
        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store,
            new ProviderState("kind", "payload"), Model, Route, Turn, Step, RouteIdentityHash)!;
        var root = JsonDocument.Parse(descriptor).RootElement;
        var hash = root.GetProperty("StateRef").GetProperty("Hash").GetProperty("Value").GetString()!;
        var blob = Path.Combine(fixture.Data, "blobs", "sha256", hash[..2], hash.Substring(2, 2), hash);
        if (corrupt) File.WriteAllText(blob, "changed content"); else File.Delete(blob);

        var error = Assert.Throws<InvalidDataException>(() =>
            ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, Route, Turn, Step, RouteIdentityHash));
        Assert.Equal("Provider state checkpoint is invalid.", error.Message);
    }

    [Fact]
    public void Descriptor_artifact_reference_round_trips_guid_and_metadata()
    {
        using var fixture = new Fixture();
        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store,
            new ProviderState("kind", "payload"), Model, Route, Turn, Step, RouteIdentityHash)!;
        var refElement = JsonDocument.Parse(descriptor).RootElement.GetProperty("StateRef");
        var restored = JsonSerializer.Deserialize<ArtifactRef>(refElement.GetRawText());

        Assert.NotNull(restored);
        Assert.NotEqual(Guid.Empty, restored.Id.Value);
        Assert.Equal(ArtifactKind.ProviderOpaqueState, restored.Kind);
        Assert.Equal(Sensitivity.Sensitive, restored.Sensitivity);
    }

    [Fact]
    public void Same_legacy_route_id_does_not_authorize_state_after_physical_endpoint_change()
    {
        using var fixture = new Fixture();
        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store,
            new ProviderState("kind", "endpoint-bound"), Model, Route, Turn, Step, RouteIdentityHash)!;
        var changedEndpoint = new ModelRoute("provider", "https://two.example/v1",
            ProviderFamily.OpenAiChatCompatible, "profile-a", Model, Route);
        var changedHash = AuthorizedModelRoute.From(changedEndpoint, BillingMode.Unknown).IdentityHash;

        Assert.Equal(Route, changedEndpoint.Id);
        Assert.NotEqual(RouteIdentityHash, changedHash);
        fixture.Store.ResetReads();
        Assert.Null(ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, Route, Turn, Step, changedHash));
        Assert.Equal(0, fixture.Store.VerifyCalls);
        Assert.Equal(0, fixture.Store.GetCalls);
    }

    [Fact]
    public void Missing_physical_binding_persists_but_never_restores()
    {
        using var fixture = new Fixture();
        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store,
            new ProviderState("kind", "unqualified"), Model, Route, Turn, Step, null)!;
        fixture.Store.ResetReads();

        Assert.Null(ProviderStateCheckpoint.Restore(fixture.Store, descriptor, Model, Route, Turn, Step, RouteIdentityHash));
        Assert.Equal(0, fixture.Store.VerifyCalls);
        Assert.Equal(0, fixture.Store.GetCalls);
    }

    [Fact]
    public void Legacy_v1_checkpoint_is_readable_but_not_authorized_for_replay()
    {
        using var fixture = new Fixture();
        var descriptor = ProviderStateCheckpoint.Persist(fixture.Store,
            new ProviderState("kind", "legacy"), Model, Route, Turn, Step, RouteIdentityHash)!;
        var node = JsonNode.Parse(descriptor)!.AsObject();
        node["Version"] = 1;
        node.Remove("RouteIdentityHash");
        fixture.Store.ResetReads();

        Assert.Null(ProviderStateCheckpoint.Restore(fixture.Store, node.ToJsonString(), Model, Route, Turn, Step, RouteIdentityHash));
        Assert.Equal(0, fixture.Store.VerifyCalls);
        Assert.Equal(0, fixture.Store.GetCalls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-provider-state-" + Guid.NewGuid().ToString("N"));
        public string Data { get; }
        public CountingArtifactStore Store { get; }
        public Fixture(SecretRedactor? redactor = null)
        {
            Data = Path.Combine(_root, "data");
            Directory.CreateDirectory(Data);
            Store = new CountingArtifactStore(new FileArtifactStore(Data, redactor));
        }
        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class CountingArtifactStore(IArtifactStore inner) : IArtifactStore, IDisposable
    {
        public int VerifyCalls { get; private set; }
        public int GetCalls { get; private set; }
        public bool ReturnWrongMediaType { get; set; }
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            var artifact = inner.PutText(content, mediaType, kind, sensitivity);
            return ReturnWrongMediaType ? artifact with { MediaType = "application/json" } : artifact;
        }
        public string? GetText(ContentHash hash) { GetCalls++; return inner.GetText(hash); }
        public bool Verify(ContentHash hash, long expectedSize) { VerifyCalls++; return inner.Verify(hash, expectedSize); }
        public void ResetReads() { VerifyCalls = 0; GetCalls = 0; }
        public void Dispose() { if (inner is IDisposable disposable) disposable.Dispose(); }
    }
}
