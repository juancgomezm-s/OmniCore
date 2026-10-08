using System.Text.Json;
using System.Text.Json.Nodes;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;

namespace OmniCore.Tests;

/// <summary>Local encrypted CAS fixtures, not provider authentication or token consumption.</summary>
public sealed class ProtectedProviderStateTests
{
    [Fact]
    public void Opaque_secrets_and_unicode_survive_reopen_without_plaintext_in_any_artifact()
    {
        using var fixture = new Fixture();
        var redactor = new SecretRedactor();
        const string marker = "registered-sensitive-marker-for-opaque-state";
        redactor.RegisterSecret(marker);
        var store = new FileArtifactStore(fixture.Root, redactor);
        var state = new ProviderState("signed", "{\"signature\":\"" + marker + "\",\"reasoning\":\"café 🚀\\nsecond\"}");
        var descriptor = fixture.Persist(store, state);
        Assert.Equal(3, JsonNode.Parse(descriptor)!["Version"]!.GetValue<int>());
        Assert.DoesNotContain(marker, descriptor);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(fixture.Root, "blobs"), "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(marker, File.ReadAllText(path));
            Assert.DoesNotContain(state.PayloadJson, File.ReadAllText(path));
        }
        var reference = Reference(descriptor);
        Assert.False(reference.Redacted);
        Assert.Equal(Sensitivity.Sensitive, reference.Sensitivity);
        Assert.Equal(ArtifactKind.ProviderOpaqueState, reference.Kind);
        Assert.True(store.Verify(reference.Hash, reference.Size));
        Assert.StartsWith("omni-protected:v1:", store.GetText(reference.Hash));
        Assert.Equal(state, fixture.Restore(new FileArtifactStore(fixture.Root), descriptor));
    }

    [Theory]
    [InlineData("turn")]
    [InlineData("step")]
    [InlineData("model")]
    [InlineData("route")]
    [InlineData("physical")]
    public void Rebinding_descriptor_cannot_move_ciphertext_to_another_owner(string changed)
    {
        using var fixture = new Fixture();
        var store = new FileArtifactStore(fixture.Root);
        var node = JsonNode.Parse(fixture.Persist(store, new ProviderState("kind", "private-state")))!;
        var model = "fixture-model";
        var route = new RouteId("fixture-route");
        var turn = fixture.Turn;
        var step = 1;
        var physical = fixture.Physical;
        switch (changed)
        {
            case "turn": turn = TurnId.New(); node["TurnId"] = turn.ToString(); break;
            case "step": step = 2; node["StepIndex"] = step; break;
            case "model": model = "different-model"; node["ModelId"] = model; break;
            case "route": route = new RouteId("other-route"); node["RouteId"] = route.Value; break;
            case "physical": physical = new string('b', 64); node["RouteIdentityHash"] = physical; break;
        }
        var error = Assert.Throws<InvalidDataException>(() => ProviderStateCheckpoint.Restore(store,
            node.ToJsonString(), model, route, turn, step, physical));
        Assert.Equal("Provider state checkpoint is invalid.", error.Message);
        Assert.DoesNotContain("private-state", error.Message);
    }

    [Fact]
    public void Integrity_valid_ciphertext_with_invalid_authentication_fails_closed()
    {
        using var fixture = new Fixture();
        var store = new FileArtifactStore(fixture.Root);
        var node = JsonNode.Parse(fixture.Persist(store, new ProviderState("kind", "private-state")))!;
        var original = Reference(node.ToJsonString());
        var cipher = store.GetText(original.Hash)!;
        var separator = cipher.LastIndexOf(':');
        var bytes = Convert.FromBase64String(cipher[(separator + 1)..]);
        bytes[^1] ^= 1;
        var tampered = store.PutText(cipher[..(separator + 1)] + Convert.ToBase64String(bytes),
            original.MediaType, original.Kind, original.Sensitivity);
        Assert.True(store.Verify(tampered.Hash, tampered.Size));
        node["StateRef"] = JsonSerializer.SerializeToNode(tampered);
        Assert.Throws<InvalidDataException>(() => fixture.Restore(store, node.ToJsonString()));
    }

    [Fact]
    public void Missing_protection_capability_never_interprets_ciphertext_as_provider_state()
    {
        using var fixture = new Fixture();
        var store = new FileArtifactStore(fixture.Root);
        var descriptor = fixture.Persist(store, new ProviderState("kind", "private-state"));
        Assert.Throws<InvalidDataException>(() => fixture.Restore(new PlainStore(store), descriptor));
    }

    [Fact]
    public void Ciphertext_changed_by_redactor_is_rejected_not_replayed()
    {
        using var fixture = new Fixture();
        var store = new FileArtifactStore(fixture.Root, new RejectCiphertext());
        Assert.Throws<InvalidDataException>(() => fixture.Persist(store, new ProviderState("kind", "private-state")));
    }

    private static ArtifactRef Reference(string descriptor) => JsonSerializer.Deserialize<ArtifactRef>(
        JsonNode.Parse(descriptor)!["StateRef"]!.ToJsonString())!;

    private sealed class RejectCiphertext : OmniCore.Abstractions.ISecretRedactor
    {
        public void RegisterSecret(string secret) { }
        public string Redact(string text) => text.StartsWith("omni-protected:", StringComparison.Ordinal) ? "[REDACTED]" : text;
    }

    private sealed class PlainStore(FileArtifactStore store) : OmniCore.Abstractions.IArtifactStore
    {
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
            => store.PutText(content, mediaType, kind, sensitivity);
        public string? GetText(ContentHash hash) => store.GetText(hash);
        public bool Verify(ContentHash hash, long size) => store.Verify(hash, size);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-protected-state-" + Guid.NewGuid().ToString("N"));
        public TurnId Turn { get; } = TurnId.New();
        public string Physical { get; } = new('a', 64);
        public string Persist(OmniCore.Abstractions.IArtifactStore store, ProviderState state) =>
            ProviderStateCheckpoint.Persist(store, state, "fixture-model", new RouteId("fixture-route"), Turn, 1, Physical)!;
        public ProviderState? Restore(OmniCore.Abstractions.IArtifactStore store, string descriptor) =>
            ProviderStateCheckpoint.Restore(store, descriptor, "fixture-model", new RouteId("fixture-route"), Turn, 1, Physical);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
