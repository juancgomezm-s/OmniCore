using System.Globalization;
using OmniCore.Domain;

namespace OmniCore.Tests;

public sealed class RouteQualificationKeyTests
{
    private static ModelQualificationKey Legacy(string? endpoint = null, string? protocol = null, string? runtimeBuild = null) =>
        new("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M", new[] { "lora-a", "lora-b" },
            "ik_llama", "baac291", "tpl-9", "default", ToolCallFormat.PromptedJson,
            ToolMode.Direct, "prompt-profile-v3", endpoint, protocol, runtimeBuild);

    [Fact]
    public void Legacy_key_preserves_canonical_bytes_and_hash()
    {
        const string canonical = "{\"providerId\":\"local\",\"modelId\":\"qwen-x\",\"modelRevision\":\"gguf-hash-abc123\",\"quantization\":\"Q4_K_M\",\"adapters\":[\"lora-a\",\"lora-b\"],\"backend\":\"ik_llama\",\"backendBuild\":\"baac291\",\"chatTemplateHash\":\"tpl-9\",\"adapterProfile\":\"default\",\"toolCallFormat\":\"PromptedJson\",\"toolMode\":\"Direct\",\"promptProfileVersion\":\"prompt-profile-v3\"}";
        var key = Legacy();

        Assert.Equal(canonical, key.CanonicalJson());
        Assert.Equal("8cf1aa2bc497b8d375887051615533f5bfcbd6b5a1de922c3adfae33c895f9d4", key.QualificationKeyHash());
        Assert.Null(key.Endpoint);
        Assert.Null(key.Protocol);
        Assert.Null(key.RuntimeBuild);
    }

    [Theory]
    [InlineData("https://api.example.test", null, null)]
    [InlineData(null, "OpenAIResponses", null)]
    [InlineData(null, null, "runtime-42")]
    public void Each_route_field_changes_identity(string? endpoint, string? protocol, string? runtimeBuild)
    {
        var baseline = Legacy();
        var changed = Legacy(endpoint, protocol, runtimeBuild);

        Assert.NotEqual(baseline.CanonicalJson(), changed.CanonicalJson());
        Assert.NotEqual(baseline.QualificationKeyHash(), changed.QualificationKeyHash());
        Assert.False(baseline.Equals(changed));
        Assert.NotEqual(baseline.GetHashCode(), changed.GetHashCode());
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("protocol")]
    [InlineData("runtimeBuild")]
    public void Null_and_empty_route_values_are_distinct(string field)
    {
        var nullKey = field switch
        {
            "endpoint" => Legacy(endpoint: null),
            "protocol" => Legacy(protocol: null),
            _ => Legacy(runtimeBuild: null),
        };
        var emptyKey = field switch
        {
            "endpoint" => Legacy(endpoint: ""),
            "protocol" => Legacy(protocol: ""),
            _ => Legacy(runtimeBuild: ""),
        };

        Assert.NotEqual(nullKey.CanonicalJson(), emptyKey.CanonicalJson());
        Assert.NotEqual(nullKey.QualificationKeyHash(), emptyKey.QualificationKeyHash());
        Assert.False(nullKey.Equals(emptyKey));
    }

    [Fact]
    public void Any_route_field_appends_all_fields_in_fixed_order_with_explicit_nulls()
    {
        var json = Legacy(endpoint: "https://api.example.test").CanonicalJson();

        Assert.EndsWith("\"endpoint\":\"https://api.example.test\",\"protocol\":null,\"runtimeBuild\":null}", json);
    }

    [Fact]
    public void Route_key_hash_is_stable_across_cultures()
    {
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = Legacy(endpoint: "https://example.test/ı", protocol: "OpenAIResponses", runtimeBuild: "v1");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = Legacy(endpoint: "https://example.test/ı", protocol: "OpenAIResponses", runtimeBuild: "v1");

            Assert.Equal(turkish.CanonicalJson(), french.CanonicalJson());
            Assert.Equal(turkish.QualificationKeyHash(), french.QualificationKeyHash());
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public void Route_fields_do_not_change_adapter_order_semantics()
    {
        var a = new ModelQualificationKey("p", "m", null, null, new[] { "adapter-a", "adapter-b" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1",
            "endpoint", "protocol", "runtime");
        var b = new ModelQualificationKey("p", "m", null, null, new[] { "adapter-b", "adapter-a" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1",
            "endpoint", "protocol", "runtime");

        Assert.NotEqual(a.CanonicalJson(), b.CanonicalJson());
        Assert.NotEqual(a.QualificationKeyHash(), b.QualificationKeyHash());
        Assert.False(a.Equals(b));
        Assert.Equal(new[] { "adapter-a", "adapter-b" }, a.Adapters);
    }

    [Fact]
    public void For_helper_accepts_route_fields_and_legacy_call_stays_null()
    {
        var legacy = ModelQualificationKey.For("p", "m", ToolCallFormat.Native, ToolMode.Direct);
        var routed = ModelQualificationKey.For("p", "m", ToolCallFormat.Native, ToolMode.Direct,
            "endpoint", "protocol", "runtime");

        Assert.Null(legacy.Endpoint);
        Assert.Null(legacy.Protocol);
        Assert.Null(legacy.RuntimeBuild);
        Assert.Equal("endpoint", routed.Endpoint);
        Assert.Equal("protocol", routed.Protocol);
        Assert.Equal("runtime", routed.RuntimeBuild);
    }
}
