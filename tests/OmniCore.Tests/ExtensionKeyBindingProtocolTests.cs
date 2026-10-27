using System.Text.Json;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ExtensionKeyBindingProtocolTests
{
    private static ExtensionKeyBindingContribution Binding() => new("Ctrl+Alt+R", "acme.tools:review",
        ["{\"text\":\"Único 😀 $(literal)\"}"], "input", "Extension:acme.tools@1.0.0", new('a', 64), new('b', 64), new('c', 64));

    [Fact]
    public void Protocol_roundtrip_preserves_literal_argv_provenance_and_future_optional_fields()
    {
        var original = Binding();
        var json = ExtensionKeyBindingProtocol.Encode(new([original], [new("command_not_owned", "source", "acme.tools")]));
        var result = ExtensionKeyBindingProtocol.Decode(json.TrimEnd('}') + ",\"futureMetric\":true}");
        var binding = Assert.Single(result.Bindings);
        Assert.Equal(original.Keys, binding.Keys); Assert.Equal(original.Arguments, binding.Arguments);
        Assert.Equal(original.Source, binding.Source); Assert.Equal(original.Command, binding.Command);
        Assert.Equal(original.ManifestSha256, binding.ManifestSha256); Assert.Equal(original.BindingFingerprint, binding.BindingFingerprint);
        Assert.Single(result.Diagnostics);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"bindings\":null,\"diagnostics\":[]}")]
    [InlineData("{\"bindings\":[],\"diagnostics\":[{}]}")]
    [InlineData("{\"bindings\":[{}],\"diagnostics\":[]}")]
    [InlineData("{\"bindings\":[null],\"diagnostics\":[]}")]
    [InlineData("{\"bindings\":[],\"diagnostics\":[null]}")]
    public void Malformed_metadata_raises_JsonException_instead_of_null_reference_failure(string json)
        => Assert.Throws<JsonException>(() => ExtensionKeyBindingProtocol.Decode(json));

    [Theory]
    [InlineData("keys")]
    [InlineData("command")]
    [InlineData("arguments")]
    [InlineData("argument_size")]
    [InlineData("when")]
    [InlineData("source")]
    [InlineData("hash_null")]
    [InlineData("hash_length")]
    [InlineData("hash_format")]
    public void Oversized_or_invalid_contributions_are_rejected_as_metadata_errors(string field)
    {
        var binding = Binding();
        binding = field switch {
            "keys" => binding with { Keys = new('x', 257) }, "command" => binding with { Command = new('x', 161) },
            "arguments" => binding with { Arguments = Enumerable.Repeat("x", 17).ToArray() },
            "argument_size" => binding with { Arguments = [new('x', 4097)] }, "when" => binding with { When = new('x', 513) },
            "source" => binding with { Source = new('x', 513) }, "hash_null" => binding with { ManifestSha256 = null! },
            "hash_length" => binding with { PackageSha256 = "a" }, _ => binding with { BindingFingerprint = new('z', 64) } };
        Assert.Throws<JsonException>(() => ExtensionKeyBindingProtocol.Decode(ExtensionKeyBindingProtocol.Encode(new([binding], []))));
    }

    [Fact]
    public void Snapshot_has_its_own_256_contribution_and_diagnostic_limits()
    {
        var binding = Binding();
        Assert.Equal(256, ExtensionKeyBindingProtocol.Decode(ExtensionKeyBindingProtocol.Encode(
            new(Enumerable.Repeat(binding, 256).ToArray(), []))).Bindings.Count);
        Assert.Throws<JsonException>(() => ExtensionKeyBindingProtocol.Decode(ExtensionKeyBindingProtocol.Encode(
            new(Enumerable.Repeat(binding, 257).ToArray(), []))));
        Assert.Throws<JsonException>(() => ExtensionKeyBindingProtocol.Decode(ExtensionKeyBindingProtocol.Encode(
            new([], Enumerable.Repeat(new ExtensionKeyBindingDiagnostic("code", "source", "ext"), 257).ToArray()))));
    }
}
